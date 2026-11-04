import type { AppData, ConfigData, ConfigField, ConfigIntegration, FieldStatus } from '../types';
import { buildFieldPayload, postJson } from '../utils/api';

// How long the quiet "Saved" note stays next to a field.
const SAVED_NOTE_MS = 2500;

/** One setting row's status chip in the Sources tab. */
export interface RowStatus {
    kind: 'ok' | 'warn' | 'err' | 'off';
    label: string;
}

/*
 * Settings save as they change; there is no Save button.
 * - Credentials and other text are tested with the provider first (on paste, Enter or leaving the field).
 *   A value that passes is saved; one that fails stays unsaved, with "Save anyway" for a service that's down.
 * - Numbers, switches and choices are saved directly.
 *
 * After a save the local state is patched instead of re-fetching /api/config: the server's options reload
 * lags the file write, so an immediate re-fetch returns the old values and would undo the change on screen.
 */
const autosaveMethods: Record<string, unknown> & ThisType<AppData> = {
    _integration(key: string): ConfigIntegration | undefined {
        return (this.configData as ConfigData | null)?.integrations?.[key];
    },

    _setFieldStatus(key: string, status: FieldStatus | null) {
        const { [key]: _, ...rest } = this.fieldStatus;
        this.fieldStatus = status ? { ...rest, [key]: status } : rest;
    },

    _flashSaved(key: string) {
        this._setFieldStatus(key, { state: 'saved' });
        setTimeout(() => {
            if (this.fieldStatus[key]?.state === 'saved') this._setFieldStatus(key, null);
        }, SAVED_NOTE_MS);
    },

    async _postSettings(values: Record<string, string>): Promise<boolean> {
        try {
            const res = await postJson('/api/config', values);
            return res.ok;
        } catch {
            return false;
        }
    },

    /** Saves one provider field if it changed. Pass force to skip the connection test ("Save anyway"). */
    async commitField(integrationKey: string, fieldKey: string, force = false) {
        const integration = this._integration(integrationKey);
        const field = integration?.fields.find(f => f.key === fieldKey);
        if (!integration || !field) return;

        const status = this.fieldStatus[fieldKey];
        if (status?.state === 'testing' || status?.state === 'saving') return;

        const value = (this.configEdits[fieldKey] ?? '').trim();
        const isSecret = field.type === 'password';
        if (isSecret ? value === '' : value === (field.value ?? '')) return;
        // A value that already failed its test isn't sent again until it changes: each Bluesky test is a sign-in.
        if (!force && status?.state === 'error' && status.tried === value) return;

        if (field.type === 'number') {
            const n = Number(value);
            if (!Number.isInteger(n) || n < 1) {
                this._setFieldStatus(fieldKey, { state: 'error', message: this.t('Settings_NumberInvalid'), tried: value });
                return;
            }
        }

        let tested = false;
        if (!force && field.type !== 'number' && value !== '') {
            this._setFieldStatus(fieldKey, { state: 'testing' });
            const payload = buildFieldPayload(integration.fields, { ...this.configEdits, [fieldKey]: value },
                { includeExistingPlaceholder: true });
            let ok = false;
            let message: string | undefined;
            try {
                const res = await postJson(`/api/test/${integrationKey}`, payload);
                const data = await res.json() as { ok?: boolean; message?: string };
                ok = !!data.ok;
                message = data.message;
            } catch {
                message = this.t('Config_RequestFailed');
            }
            const result = { status: ok ? 'ok' : 'error', message };
            this.lastTestResults = { ...this.lastTestResults, [integrationKey]: result };
            if (!ok) {
                this._setFieldStatus(fieldKey, {
                    state: 'error',
                    message: message || this.t('Config_ConnectionFailed'),
                    canForce: true,
                    tried: value,
                });
                return;
            }
            tested = true;
        }

        // A passing test vouches for every value it was run with, so the other unsaved edits go too: a handle
        // whose own test failed (no app password yet) is saved once the pasted password makes it work.
        const values: Record<string, string> = { [fieldKey]: value };
        if (tested) {
            for (const other of integration.fields) {
                if (other.key === fieldKey || other.type === 'number') continue;
                const edit = (this.configEdits[other.key] ?? '').trim();
                const changed = other.type === 'password' ? edit !== '' : edit !== (other.value ?? '');
                if (changed) values[other.key] = edit;
            }
        }

        for (const key of Object.keys(values)) this._setFieldStatus(key, { state: 'saving' });
        if (!await this._postSettings(values)) {
            for (const key of Object.keys(values)) {
                this._setFieldStatus(key, { state: 'error', message: this.t('Settings_SaveFailed'), canForce: true, tried: values[key] });
            }
            return;
        }

        for (const [key, saved] of Object.entries(values)) {
            const f = integration.fields.find(x => x.key === key)!;
            f.isOverridden = true;
            f.hasValue = saved !== '';
            if (f.type === 'password') this.configEdits[key] = '';
            else f.value = saved;
            this._flashSaved(key);
        }
        if (tested) integration.configured = true;
    },

    /** Drops the value saved here, so the environment variable (or the default) applies again. */
    async useEnvValue(integrationKey: string, fieldKey: string) {
        const field = this._integration(integrationKey)?.fields.find(f => f.key === fieldKey);
        if (!field) return;
        this._setFieldStatus(fieldKey, { state: 'saving' });
        try {
            const res = await fetch('/api/config', {
                method: 'DELETE',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify([fieldKey]),
            });
            if (!res.ok) throw new Error(String(res.status));
        } catch {
            this._setFieldStatus(fieldKey, { state: 'error', message: this.t('Settings_SaveFailed') });
            return;
        }
        const env = field.envValue ?? '';
        field.isOverridden = false;
        field.hasValue = env !== '';
        if (field.type !== 'password') {
            field.value = env;
            this.configEdits[fieldKey] = env;
        } else {
            this.configEdits[fieldKey] = '';
        }
        this._flashSaved(fieldKey);
    },

    /** Saves one app preference (WatchBack__*) straight away. */
    async setPreference(key: string, value: string): Promise<boolean> {
        this._setFieldStatus(key, { state: 'saving' });
        if (!await this._postSettings({ [key]: value })) {
            this._setFieldStatus(key, { state: 'error', message: this.t('Settings_SaveFailed') });
            return false;
        }
        const prefs = (this.configData as ConfigData | null)?.preferences;
        if (prefs) prefs.overrides = { ...prefs.overrides, [key]: true };
        this._flashSaved(key);
        return true;
    },

    /** The watch source in use: the saved one, else the first (the server falls back the same way). */
    activeWatchProvider(): string {
        const prefs = (this.configData as ConfigData | null)?.preferences;
        const options = prefs?.watchProviders ?? [];
        const saved = (prefs?.watchProvider ?? '').toLowerCase();
        return options.find(o => o.value.toLowerCase() === saved)?.value ?? options[0]?.value ?? '';
    },

    async chooseWatchProvider(value: string) {
        const prefs = (this.configData as ConfigData | null)?.preferences;
        if (!prefs || this.activeWatchProvider() === value) return;
        const previous = prefs.watchProvider;
        prefs.watchProvider = value;
        this.prefEdits = { ...this.prefEdits, watchProvider: value };
        if (!await this.setPreference('WatchBack__WatchProvider', value)) {
            prefs.watchProvider = previous;
            this.prefEdits = { ...this.prefEdits, watchProvider: previous };
        }
    },

    async commitTimeMachine() {
        const key = 'WatchBack__TimeMachineDays';
        const prefs = (this.configData as ConfigData | null)?.preferences;
        const n = Number((this.prefEdits as Record<string, unknown>)['timeMachineDays']);
        if (!Number.isInteger(n) || n < 1 || n > 365) {
            this._setFieldStatus(key, { state: 'error', message: this.t('Settings_DaysInvalid') });
            return;
        }
        if (prefs && prefs.timeMachineDays === n) {
            this._setFieldStatus(key, null);
            return;
        }
        if (await this.setPreference(key, String(n)) && prefs) prefs.timeMachineDays = n;
    },

    async setSearchEngine(engine: string) {
        const prefs = (this.configData as ConfigData | null)?.preferences;
        this.prefEdits = { ...this.prefEdits, searchEngine: engine };
        if (await this.setPreference('WatchBack__SearchEngine', engine) && prefs) prefs.searchEngine = engine;
    },

    async commitCustomSearchUrl() {
        const prefs = (this.configData as ConfigData | null)?.preferences;
        const url = String((this.prefEdits as Record<string, unknown>)['customSearchUrl'] ?? '').trim();
        if (prefs && prefs.customSearchUrl === url) return;
        if (await this.setPreference('WatchBack__CustomSearchUrl', url) && prefs) prefs.customSearchUrl = url;
    },

    async setPreferenceSwitch(prefKey: 'segmentedProgressBar' | 'enableSentimentAnalysis', on: boolean) {
        const keys = { segmentedProgressBar: 'WatchBack__SegmentedProgressBar', enableSentimentAnalysis: 'WatchBack__EnableSentimentAnalysis' };
        const previous = (this.prefEdits as Record<string, unknown>)[prefKey];
        this.prefEdits = { ...this.prefEdits, [prefKey]: on };
        if (!await this.setPreference(keys[prefKey], String(on))) {
            this.prefEdits = { ...this.prefEdits, [prefKey]: previous };
            return;
        }
        const prefs = (this.configData as ConfigData | null)?.preferences as unknown as Record<string, unknown> | undefined;
        if (prefs) prefs[prefKey] = on;
    },

    /** The chip on a provider row. Watch sources have no on/off switch, so they ignore "disabled". */
    rowStatus(key: string, asWatchSource = false): RowStatus {
        const integration = this._integration(key);
        if (!integration) return { kind: 'ok', label: this.t('Settings_StatusReady') };
        if (!asWatchSource && integration.disabled) return { kind: 'off', label: this.t('Settings_StatusOff') };
        const last = this.lastTestResults[key] as { status?: string } | undefined;
        if (last?.status === 'error') return { kind: 'err', label: this.t('Settings_StatusFailed') };
        if (last?.status === 'ok') return { kind: 'ok', label: this.t('Config_Connected') };
        if (integration.configured) return { kind: 'ok', label: this.t('Settings_StatusReady') };
        return { kind: 'warn', label: this.t('Settings_StatusNeedsSetup') };
    },

    /** The row's second line: the provider's own non-secret settings (a host, a handle), else its description. */
    rowSummary(key: string): string {
        const integration = this._integration(key);
        if (!integration) return '';
        const parts = integration.fields
            .filter(f => (f.type === 'text' || f.type === 'url') && f.value && !f.secret)
            .map(f => f.value!.replace(/^https?:\/\//, '').replace(/\/$/, ''));
        return parts.length ? parts.join(' · ') : (integration.description ?? '');
    },

    /** Opens one settings row and closes any other. Reddit's row loads the subreddit lists. */
    toggleRow(id: string) {
        this.openRow = this.openRow === id ? null : id;
        if (this.openRow?.endsWith(':reddit')) void this.fetchMappings();
    },

    /** The quiet note beside a field's label: its save state, else where its value comes from. */
    fieldNote(field: ConfigField): string {
        const note = this._statusNote(field.key);
        if (note) return note;
        if (field.envValue && !field.isOverridden) return this.t('Settings_FromEnv', field.key);
        return '';
    },

    /** Same, for an app preference. */
    prefNote(key: string): string {
        const note = this._statusNote(key);
        if (note) return note;
        const prefs = (this.configData as ConfigData | null)?.preferences;
        if (prefs?.envValues?.[key] && !prefs.overrides?.[key]) return this.t('Settings_FromEnv', key);
        return '';
    },

    _statusNote(key: string): string {
        switch (this.fieldStatus[key]?.state) {
            case 'testing': return this.t('Common_Testing');
            case 'saving': return this.t('Common_Saving');
            case 'saved': return '\u2713 ' + this.t('Common_Saved');
            default: return '';
        }
    },
};

export default autosaveMethods;

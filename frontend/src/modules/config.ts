import type { AppData, ConfigData } from '../types';
import { buildFieldPayload, postJson } from '../utils/api';
import type { IntegrationMap } from '../utils/api';

const configMethods: Record<string, unknown> & ThisType<AppData> = {
    _initConfigEdits(configData: ConfigData) {
        const edits: Record<string, string> = {};
        const integrations = configData.integrations;
        for (const integration of Object.values(integrations)) {
            for (const field of integration.fields) {
                edits[field.key] = field.type === 'password' ? '' : (field.value ?? '');
            }
        }
        this.configEdits = edits;
        const prefs = configData.preferences;
        this.prefEdits = {
            timeMachineDays: prefs.timeMachineDays ?? 14,
            watchProvider: prefs.watchProvider ?? prefs.watchProviders?.[0]?.value ?? 'jellyfin',
            searchEngine: prefs.searchEngine ?? 'google',
            customSearchUrl: prefs.customSearchUrl ?? '',
            segmentedProgressBar: prefs.segmentedProgressBar ?? false,
            enableSentimentAnalysis: prefs.enableSentimentAnalysis ?? false,
        };
    },

    async saveConfig(integrationKey: string) {
        const integrations = (this.configData?.['integrations'] as IntegrationMap | undefined);
        const integration = integrations?.[integrationKey];
        if (!integration) return;

        const payload = buildFieldPayload(integration.fields, this.configEdits);

        this.saveStatus = { ...this.saveStatus, [integrationKey]: 'saving' };
        try {
            const res = await postJson('/api/config', payload);
            if (res.ok) {
                this.saveStatus = { ...this.saveStatus, [integrationKey]: 'saved' };
                const cRes = await fetch('/api/config');
                if (cRes.ok) {
                    this.configData = await cRes.json() as ConfigData;
                    const updatedIntegrations = this.configData?.integrations as IntegrationMap | undefined;
                    for (const field of updatedIntegrations?.[integrationKey]?.fields ?? []) {
                        if (field.type !== 'password')
                            this.configEdits[field.key] = field.value ?? '';
                    }
                }
            } else {
                this.saveStatus = { ...this.saveStatus, [integrationKey]: 'error' };
            }
        } catch {
            this.saveStatus = { ...this.saveStatus, [integrationKey]: 'error' };
        }
        setTimeout(() => {
            const { [integrationKey]: _, ...rest } = this.saveStatus;
            this.saveStatus = rest;
        }, 3000);
    },

    async saveAllConfig() {
        const integrations = (this.configData?.['integrations'] as IntegrationMap | undefined) ?? {};
        const allFields = Object.values(integrations).flatMap(i => i.fields);
        const payload = buildFieldPayload(allFields, this.configEdits);

        this.saveAllStatus = 'saving';
        try {
            const res = await postJson('/api/config', payload);
            if (res.ok) {
                this.saveAllStatus = 'saved';
                const cRes = await fetch('/api/config');
                if (cRes.ok) {
                    this.configData = await cRes.json() as ConfigData;
                }
            } else {
                this.saveAllStatus = 'error';
            }
        } catch {
            this.saveAllStatus = 'error';
        }
        setTimeout(() => { this.saveAllStatus = null; }, 3000);
    },

    redditSearchUrl(title: string, season: number, episode: number): string {
        // This is for both bad data from Watch Providers and edge cases like specials that Jellyfin classifies as "Season 0"
        const querySeason = season === 0 ? '' : 'S' + String(season).padStart(2, '0');
        const queryEpisode = episode === 0 ? '' : 'E' + String(episode).padStart(2, '0');
        
        const episodeCode = querySeason + queryEpisode;
        const query = encodeURIComponent(
            (episodeCode ? title + ' ' + episodeCode : title) + ' reddit'
        );
        const prefs = (this.prefEdits as Record<string, unknown>);
        const config = (this.configData?.['preferences'] as Record<string, unknown> | undefined) ?? {};
        const engine = (prefs['searchEngine'] as string | undefined) ?? (config['searchEngine'] as string | undefined) ?? 'google';
        const custom = (prefs['customSearchUrl'] as string | undefined) ?? (config['customSearchUrl'] as string | undefined) ?? '';
        const bases: Record<string, string> = {
            google: 'https://www.google.com/search?q=',
            duckduckgo: 'https://duckduckgo.com/?q=',
            bing: 'https://www.bing.com/search?q=',
        };
        const base = engine === 'custom' ? (custom || bases['google']) : (bases[engine] ?? bases['google']);
        return base + query;
    },

    async resetConfigKeys(keys: string[], refetch = true) {
        await fetch('/api/config', {
            method: 'DELETE',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(keys),
        });
        if (refetch) {
            const res = await fetch('/api/config');
            if (res.ok) {
                this.configData = await res.json() as ConfigData;
                if (this.configData) this._initConfigEdits(this.configData);
            }
        }
    },

    async savePreferences() {
        const payload: Record<string, string> = {};
        const p = this.prefEdits as Record<string, unknown>;
        if (p['timeMachineDays'] != null)
            payload['WatchBack__TimeMachineDays'] = String(p['timeMachineDays']);
        if (p['watchProvider'])
            payload['WatchBack__WatchProvider'] = p['watchProvider'] as string;
        if (p['searchEngine'])
            payload['WatchBack__SearchEngine'] = p['searchEngine'] as string;
        payload['WatchBack__CustomSearchUrl'] = (p['customSearchUrl'] as string) ?? '';
        payload['WatchBack__SegmentedProgressBar'] = String(p['segmentedProgressBar'] ?? false);
        payload['WatchBack__EnableSentimentAnalysis'] = String(p['enableSentimentAnalysis'] ?? false);

        this.prefSaveStatus = 'saving';
        try {
            const res = await fetch('/api/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
            });
            this.prefSaveStatus = res.ok ? 'saved' : 'error';
            if (res.ok) {
                const cRes = await fetch('/api/config');
                if (cRes.ok) this.configData = await cRes.json() as ConfigData;
            }
        } catch {
            this.prefSaveStatus = 'error';
        }
        setTimeout(() => { this.prefSaveStatus = null; }, 3000);
    },

    testIcon(service: string): { icon: string; cls: string; label: string } {
        const r = (this.lastTestResults[service] as Record<string, unknown> | undefined);
        if (!r) {
            const integrations = (this.configData?.['integrations'] as Record<string, { configured?: boolean }> | undefined);
            const configured = integrations?.[service]?.configured;
            if (configured) return { icon: '\u2713', cls: 'wb-accent-text', label: this.t('Config_Configured') };
            return { icon: '\u2717', cls: 'wb-text-faint', label: this.t('Config_NotConfiguredStatus') };
        }
        if (r['status'] === 'ok') return { icon: '\u2713', cls: 'wb-success-text', label: this.t('Config_Connected') };
        return { icon: '\u2717', cls: 'wb-error-text', label: this.t('Config_ConnectionFailed') };
    },

    async testService(service: string) {
        this.testResults = { ...this.testResults, [service]: { status: 'testing' } };

        const integrations = (this.configData?.['integrations'] as IntegrationMap | undefined);
        const integration = integrations?.[service];
        const payload = buildFieldPayload(
            integration?.fields ?? [],
            this.configEdits,
            { includeExistingPlaceholder: true }
        );

        try {
            const res = await postJson(`/api/test/${service}`, payload);
            const data = await res.json() as Record<string, unknown>;
            const result = { status: data['ok'] ? 'ok' : 'error', message: data['message'] };
            this.testResults = { ...this.testResults, [service]: result };
            this.lastTestResults = { ...this.lastTestResults, [service]: result };
        } catch {
            const result = { status: 'error', message: this.t('Config_RequestFailed') };
            this.testResults = { ...this.testResults, [service]: result };
            this.lastTestResults = { ...this.lastTestResults, [service]: result };
        }
        setTimeout(() => {
            const { [service]: _, ...rest } = this.testResults;
            this.testResults = rest;
        }, 4000);
    },

    async testAll() {
        this.testAllStatus = 'testing';
        const integrations = (this.configData?.['integrations'] as Record<string, unknown> | undefined) ?? {};
        const services = Object.keys(integrations);
        await Promise.all(services.map((s) => this.testService(s)));
        const results = services.map(s => this.lastTestResults[s] as Record<string, unknown> | undefined);
        const anyResult = results.some(r => r);
        const allOk = anyResult && results.every(r => r?.['status'] === 'ok');
        const allFailed = anyResult && results.every(r => r?.['status'] === 'error');
        this.testAllStatus = allOk ? 'ok' : allFailed ? 'error' : 'warn';
        setTimeout(() => { this.testAllStatus = null; }, 5000);
    },

    async toggleDisabled(key: string) {
        const integrations = (this.configData?.['integrations'] as IntegrationMap | undefined) ?? {};
        const currentlyDisabled = integrations[key]?.disabled ?? false;
        const disabledKeys = Object.entries(integrations)
            .filter(([, v]) => v.disabled)
            .map(([k]) => k);
        const newDisabled = currentlyDisabled
            ? disabledKeys.filter(k => k !== key)
            : [...disabledKeys, key];

        // Optimistic update — card animates immediately
        if (this.configData?.['integrations']) {
            const intMap = this.configData['integrations'] as IntegrationMap;
            intMap[key] = { ...intMap[key], disabled: !currentlyDisabled };
        }

        // If Reddit is being disabled while the mappings tab is active, switch away
        if (key.toLowerCase() === 'reddit' && !currentlyDisabled && this.configTab === 'mappings') {
            this.configTab = 'settings';
        }

        try {
            if (newDisabled.length === 0) {
                await this.resetConfigKeys(['WatchBack__DisabledProviders'], false);
            } else {
                await postJson('/api/config', { 'WatchBack__DisabledProviders': newDisabled.join(',') });
            }
        } catch {
            // Revert optimistic update on failure
            if (this.configData?.['integrations']) {
                const intMap = this.configData['integrations'] as IntegrationMap;
                intMap[key] = { ...intMap[key], disabled: currentlyDisabled };
            }
        }
        // No re-fetch: IOptionsSnapshot has a debounce delay on file-change reload, so a
        // GET immediately after the POST returns stale data and reverts the optimistic update.
        // The local state is already correct; SyncService reads DisabledProviders fresh per-request.
    },
};

export default configMethods;

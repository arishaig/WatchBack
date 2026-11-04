import type { AppData, ConfigData } from '../types';
import type { IntegrationMap } from '../utils/api';

const wizardMethods: Record<string, unknown> & ThisType<AppData> = {
    async wizardNext() {
        this.wizardStep = Math.min(this.wizardStep + 1, 4);
    },

    wizardBack() {
        this.wizardStep = Math.max(this.wizardStep - 1, 0);
    },

    wizardSkip() {
        this.wizardActive = false;
    },

    // First start, "Set it up with an AI assistant": mint an API key for the MCP endpoint and show how to
    // connect an assistant to it. The key is shown once and kept only in memory.
    wizardStartAi() {
        this.wizardAiMode = true;
        this.wizardAiKey = null;
        this.wizardAiStatus = 'idle';
    },

    async wizardCreateAiKey() {
        this.wizardAiStatus = 'creating';
        try {
            const res = await fetch('/api/keys', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: `Setup assistant (${new Date().toISOString().slice(0, 10)})` }),
            });
            if (!res.ok) {
                this.wizardAiStatus = 'error';
                return;
            }
            this.wizardAiKey = (await res.json() as { key: string }).key;
            this.wizardAiStatus = 'idle';
        } catch {
            this.wizardAiStatus = 'error';
        }
    },

    async wizardAiCopy(what: string, text: string) {
        try {
            await navigator.clipboard.writeText(text);
            this.wizardAiCopied = what;
            setTimeout(() => { if (this.wizardAiCopied === what) this.wizardAiCopied = null; }, 2000);
        } catch {
            // Clipboard API unavailable (e.g. plain http): the text is selectable instead.
        }
    },

    wizardAiDone() {
        this.wizardAiKey = null;
        this.wizardAiMode = false;
        this.wizardComplete();
    },

    wizardAiBack() {
        this.wizardAiKey = null;
        this.wizardAiMode = false;
    },

    wizardComplete() {
        this.wizardActive = false;
        localStorage.setItem('wb_wizardCompleted', 'true');
        const integrations = (this.configData?.['integrations'] as Record<string, unknown> | undefined) ?? {};
        localStorage.setItem('wb_seenProviders', JSON.stringify(Object.keys(integrations)));
        void this.sync();
    },

    wizardSelectWatch(key: string) {
        this.wizardSelectedWatch = this.wizardSelectedWatch === key ? null : key;
    },

    wizardToggleComment(key: string) {
        if (this.wizardSelectedComments.has(key)) {
            this.wizardSelectedComments.delete(key);
        } else {
            this.wizardSelectedComments.add(key);
        }
        // Trigger Alpine reactivity
        this.wizardSelectedComments = new Set(this.wizardSelectedComments);
    },

    async wizardSaveAndContinue() {
        this.wizardSaving = true;
        try {
            if (this.wizardStep === 1 && this.wizardSelectedWatch) {
                await this.saveConfig(this.wizardSelectedWatch);
            } else if (this.wizardStep === 2) {
                for (const key of this.wizardSelectedComments) {
                    await this.saveConfig(key);
                }
            } else if (this.wizardStep === 3) {
                await this.savePreferences();
            }
            // Reload config to get updated `configured` status
            const cRes = await fetch('/api/config');
            if (cRes.ok) {
                this.configData = await cRes.json() as ConfigData;
                if (this.configData) this._initConfigEdits(this.configData);
            }
            this.wizardStep = Math.min(this.wizardStep + 1, 4);
        } finally {
            this.wizardSaving = false;
        }
    },

    newProviderToggle(key: string) {
        if (this.newProviderSelected.has(key)) {
            this.newProviderSelected.delete(key);
        } else {
            this.newProviderSelected.add(key);
        }
        this.newProviderSelected = new Set(this.newProviderSelected);
    },

    dismissNewProviders() {
        this.newProvidersActive = false;
        const integrations = (this.configData?.['integrations'] as Record<string, unknown> | undefined) ?? {};
        localStorage.setItem('wb_seenProviders', JSON.stringify(Object.keys(integrations)));
    },

    async saveAndDismissNewProviders() {
        this.newProviderSaving = true;
        try {
            for (const key of this.newProviderKeys) {
                await this.saveConfig(key);
            }
            const cRes = await fetch('/api/config');
            if (cRes.ok) {
                this.configData = await cRes.json() as ConfigData;
                if (this.configData) this._initConfigEdits(this.configData);
            }
        } finally {
            this.newProviderSaving = false;
            this.dismissNewProviders();
        }
    },

    dismissChecklist() {
        this.checklistDismissed = true;
    },

    // Opens an integration's settings: the panel, its card expanded and scrolled to, and the first secret
    // field without a value focused, ready for a paste. Used by #settings/<key> links, which the MCP
    // setup tools hand to people so secrets go into WatchBack rather than into an assistant's chat.
    openSettingsLink(key: string): boolean {
        const integrations = (this.configData?.['integrations'] as IntegrationMap | undefined) ?? {};
        const integration = integrations[key] as unknown as
            { fields?: Array<{ key: string; type: string; hasValue: boolean }> } | undefined;
        if (!integration) return false;

        this.wizardActive = false;
        this.showConfig = true;
        this.configTab = 'settings';
        this.collapsedSections = { ...this.collapsedSections, [key]: false };

        // After the panel and card have rendered.
        setTimeout(() => {
            const card = document.querySelector(`[data-integration="${CSS.escape(key)}"]`);
            card?.scrollIntoView({ block: 'start', behavior: 'smooth' });
            const fields = integration.fields ?? [];
            const target = fields.find(f => f.type === 'password' && !f.hasValue) ?? fields.find(f => !f.hasValue);
            const input = target ? document.getElementById('field-' + target.key) : null;
            (input as HTMLInputElement | null)?.focus({ preventScroll: true });
        }, 350);
        return true;
    },

    handleSettingsHash() {
        const match = /^#settings\/([a-z0-9_-]+)$/i.exec(window.location.hash);
        if (!match || !this.configData) return;
        // Drop the fragment so a reload doesn't reopen the panel.
        history.replaceState(null, '', window.location.pathname + window.location.search);
        this.openSettingsLink(match[1].toLowerCase());
    },

    openConfigForItem(type: string) {
        this.showConfig = true;
        this.configTab = 'settings';
        // Uncollapse the first matching integration card
        const integrations = (this.configData?.['integrations'] as IntegrationMap | undefined) ?? {};
        for (const [key, integration] of Object.entries(integrations)) {
            const types = (integration as unknown as { providerTypes?: string[] }).providerTypes ?? [];
            if (types.includes(type)) {
                this.collapsedSections = { ...this.collapsedSections, [key]: false };
                break;
            }
        }
    },
};

export default wizardMethods;

import type { AppData } from '../types';
import type { IntegrationMap } from '../utils/api';

const wizardMethods: Record<string, unknown> & ThisType<AppData> = {
    async wizardNext() {
        this.wizardStep = Math.min(this.wizardStep + 1, 3);
    },

    wizardBack() {
        this.wizardStep = Math.max(this.wizardStep - 1, 0);
    },

    wizardSkip() {
        this.wizardActive = false;
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
            }
            // Reload config to get updated `configured` status
            const cRes = await fetch('/api/config');
            if (cRes.ok) {
                this.configData = await cRes.json() as Record<string, unknown>;
                this._initConfigEdits(this.configData);
            }
            this.wizardStep = Math.min(this.wizardStep + 1, 3);
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
                this.configData = await cRes.json() as Record<string, unknown>;
                this._initConfigEdits(this.configData);
            }
        } finally {
            this.newProviderSaving = false;
            this.dismissNewProviders();
        }
    },

    dismissChecklist() {
        this.checklistDismissed = true;
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

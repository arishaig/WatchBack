import type { AppData, ApiKeyEntry, SaveStatus } from '../types';

const mcpMethods: Record<string, unknown> & ThisType<AppData> = {
    async loadApiKeys() {
        try {
            const res = await fetch('/api/keys');
            if (res.ok) this.apiKeys = await res.json() as ApiKeyEntry[];
        } catch {
            // Non-fatal — table will stay empty
        }
    },

    async generateApiKey() {
        const name = (this.newApiKeyName as string)?.trim();
        if (!name) return;

        this.apiKeySaveStatus = 'saving';
        try {
            const res = await fetch('/api/keys', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name }),
            });
            if (res.ok) {
                const data = await res.json() as ApiKeyEntry & { key: string };
                this.newlyGeneratedKey = data.key;
                this.newApiKeyName = '';
                this.apiKeySaveStatus = 'saved';
                await this.loadApiKeys();
            } else {
                this.apiKeySaveStatus = 'error';
            }
        } catch {
            this.apiKeySaveStatus = 'error';
        }
        setTimeout(() => { this.apiKeySaveStatus = null as SaveStatus; }, 3000);
    },

    async revokeApiKey(id: number) {
        try {
            const res = await fetch(`/api/keys/${id}`, { method: 'DELETE' });
            if (res.ok) await this.loadApiKeys();
        } catch {
            // Non-fatal
        }
    },

    async copyApiKey(key: string) {
        try {
            await navigator.clipboard.writeText(key);
            this.apiKeyCopied = true;
            setTimeout(() => { this.apiKeyCopied = false; }, 2000);
        } catch {
            // Clipboard API unavailable in some contexts
        }
    },

    dismissGeneratedKey() {
        this.newlyGeneratedKey = null;
        this.apiKeyCopied = false;
    },
};

export default mcpMethods;

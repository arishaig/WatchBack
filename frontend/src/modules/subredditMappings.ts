import type { AppData } from '../types';

const subredditMappingsMethods: Record<string, unknown> & ThisType<AppData> = {
    async fetchMappings() {
        try {
            const res = await fetch('/api/subreddit-mappings');
            if (res.ok) {
                this.mappingSources = await res.json() as AppData['mappingSources'];
            }
        } catch {
            // Silently ignore — section will show empty
        }
    },

    async addLocalMapping() {
        const title = (this.newMappingTitle as string).trim();
        const subredditsRaw = (this.newMappingSubreddits as string).trim();
        if (!title || !subredditsRaw) return;

        const subreddits = subredditsRaw.split(',').map(s => s.trim()).filter(Boolean);
        if (subreddits.length === 0) return;

        this.mappingSaveStatus = 'saving';
        try {
            const res = await fetch('/api/subreddit-mappings/local', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title, subreddits }),
            });
            if (res.ok) {
                this.newMappingTitle = '';
                this.newMappingSubreddits = '';
                this.mappingSaveStatus = 'saved';
                await this.fetchMappings();
            } else {
                this.mappingSaveStatus = 'error';
            }
        } catch {
            this.mappingSaveStatus = 'error';
        }
        setTimeout(() => { this.mappingSaveStatus = null; }, 3000);
    },

    async deleteLocalMapping(title: string) {
        await fetch(`/api/subreddit-mappings/local/${encodeURIComponent(title)}`, {
            method: 'DELETE',
        });
        await this.fetchMappings();
    },

    async importMappingSource() {
        const json = (this.mappingImportJson as string).trim();
        const name = (this.mappingImportName as string).trim() || 'Imported';
        if (!json) return;

        this.mappingImportStatus = 'saving';
        try {
            const res = await fetch('/api/subreddit-mappings/import', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ json, name }),
            });
            if (res.ok) {
                this.mappingImportJson = '';
                this.mappingImportName = '';
                this.mappingImportStatus = 'saved';
                await this.fetchMappings();
            } else {
                this.mappingImportStatus = 'error';
            }
        } catch {
            this.mappingImportStatus = 'error';
        }
        setTimeout(() => { this.mappingImportStatus = null; }, 3000);
    },

    async deleteMappingSource(id: string) {
        await fetch(`/api/subreddit-mappings/sources/${encodeURIComponent(id)}`, {
            method: 'DELETE',
        });
        await this.fetchMappings();
    },

    async promoteMappingEntry(sourceId: string, index: number) {
        await fetch(`/api/subreddit-mappings/sources/${encodeURIComponent(sourceId)}/promote/${index}`, {
            method: 'POST',
        });
        await this.fetchMappings();
    },

    handleMappingFileDrop(e: DragEvent) {
        this.mappingDropActive = false;
        const file = e.dataTransfer?.files?.[0];
        if (!file) return;
        if (!(this.mappingImportName as string).trim()) {
            this.mappingImportName = file.name.replace(/\.json$/i, '');
        }
        const reader = new FileReader();
        reader.onload = (ev) => {
            this.mappingImportJson = (ev.target?.result as string) ?? '';
        };
        reader.readAsText(file);
    },

    async exportMappingSource(id: string) {
        try {
            const res = await fetch(`/api/subreddit-mappings/sources/${encodeURIComponent(id)}/export`);
            if (!res.ok) return;
            const blob = await res.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `${id}.json`;
            a.click();
            URL.revokeObjectURL(url);
        } catch {
            // Silently ignore download failures
        }
    },
};

export default subredditMappingsMethods;
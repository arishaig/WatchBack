import type { AppData } from '../types';

function parseSubreddits(raw: string): string[] {
    return [...new Set(
        raw.split(/[\s,]+/)
            .map(s => s.replace(/^\/?r\//i, '').trim())
            .filter(s => /^[A-Za-z0-9_]+$/.test(s))
    )];
}

type MappingSource = AppData['mappingSources'][number];
type MappingEntry = MappingSource['entries'][number];

// How many matches the built-in list search shows at once.
const MAX_MAPPING_MATCHES = 20;

const subredditMappingsMethods: Record<string, unknown> & ThisType<AppData> = {
    /** The mappings you added yourself, shown first in the Reddit row. */
    localMappings(): MappingEntry[] {
        return (this.mappingSources as MappingSource[]).find(s => s.id === 'local')?.entries ?? [];
    },

    /** How many shows the built-in and imported lists cover. */
    otherMappingCount(): number {
        return (this.mappingSources as MappingSource[])
            .filter(s => s.id !== 'local')
            .reduce((n, s) => n + s.entries.length, 0);
    },

    /** Built-in and imported mappings matching the search box: a title or a subreddit, case-insensitive. */
    matchingMappings(): { source: MappingSource; entry: MappingEntry }[] {
        const q = (this.mappingFilter as string).trim().toLowerCase().replace(/^\/?r\//, '');
        if (!q) return [];
        const matches: { source: MappingSource; entry: MappingEntry }[] = [];
        for (const source of (this.mappingSources as MappingSource[]).filter(s => s.id !== 'local')) {
            for (const entry of source.entries) {
                if ((entry.title ?? '').toLowerCase().includes(q) || entry.subreddits.some(r => r.toLowerCase().includes(q))) {
                    matches.push({ source, entry });
                    if (matches.length >= MAX_MAPPING_MATCHES) return matches;
                }
            }
        }
        return matches;
    },

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

    async searchForMapping() {
        const query = (this.newMappingTitle as string).trim();
        if (!query) return;

        this.mappingSearchLoading = true;
        this.mappingSearchResults = [];
        try {
            const res = await fetch(`/api/search?q=${encodeURIComponent(query)}`);
            if (res.ok) {
                this.mappingSearchResults = await res.json() as unknown[];
            }
        } catch {
            // Silently ignore
        }
        this.mappingSearchLoading = false;
    },

    selectMappingSearchResult(result: Record<string, unknown>) {
        this.newMappingTitle = (result['title'] as string) ?? '';
        this.newMappingImdbId = (result['imdbId'] as string) ?? '';
        this.mappingSearchResults = [];
    },

    async addLocalMapping() {
        const title = (this.newMappingTitle as string).trim();
        const subredditsRaw = (this.newMappingSubreddits as string).trim();
        if (!title || !subredditsRaw) return;

        const subreddits = parseSubreddits(subredditsRaw);
        if (subreddits.length === 0) return;

        const imdbId = (this.newMappingImdbId as string).trim();
        const body: Record<string, unknown> = { title, subreddits };
        if (imdbId) {
            body['externalIds'] = { imdb: imdbId };
        }

        this.mappingSaveStatus = 'saving';
        try {
            const res = await fetch('/api/subreddit-mappings/local', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            });
            if (res.ok) {
                this.newMappingTitle = '';
                this.newMappingSubreddits = '';
                this.newMappingImdbId = '';
                this.mappingSearchResults = [];
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

    async shareMappingSource(id: string, name: string) {
        try {
            const res = await fetch(`/api/subreddit-mappings/sources/${encodeURIComponent(id)}/export`);
            if (!res.ok) return;
            const text = await res.text();
            if (navigator.share) {
                await navigator.share({ title: `${name} subreddit mappings`, text });
            } else {
                await navigator.clipboard.writeText(text);
                this.mappingShareCopied = id;
                setTimeout(() => { this.mappingShareCopied = ''; }, 2000);
            }
        } catch {
            // User cancelled share or clipboard unavailable
        }
    },
};

export default subredditMappingsMethods;
import type { AppData } from '../types';

/**
 * Computed getters for the Alpine component.
 * Exported as property descriptors so main.ts can merge them via
 * Object.defineProperties, preserving the getter semantics.
 */
const _computed: Record<string, unknown> & ThisType<AppData> = {
    get filteredLogs(): unknown[] {
        const levels = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'];
        const minIdx = this.logLevel === 'All' ? -1 : levels.indexOf(this.logLevel);
        if (minIdx < 0) return this.logEntries;
        return this.logEntries.filter(e => levels.indexOf((e as { level: string }).level) >= minIdx);
    },

    get showTimeMachine(): boolean {
        if (!this.data) return false;
        const tm = (this.data['timeMachineThoughts'] as unknown[]) ?? [];
        if (tm.length === 0) return false;
        const premiere = (this.data['metadata'] as Record<string, unknown> | undefined)?.['releaseDate'] as string | undefined;
        if (premiere) {
            const cutoff = new Date(premiere);
            cutoff.setDate(cutoff.getDate() + ((this.data['timeMachineDays'] as number | undefined) ?? 14));
            if (cutoff >= new Date()) return false;
        }
        return true;
    },

    get activeThoughts(): unknown[] {
        if (!this.data) return [];
        const list = this.mode === 'time'
            ? ((this.data['timeMachineThoughts'] as unknown[]) ?? [])
            : ((this.data['allThoughts'] as unknown[]) ?? []);
        if (this.sourceFilter.size === 0) return list;
        return list.filter(c => this.sourceFilter.has((c as { source: string }).source));
    },

    get hasThreadGroups(): boolean {
        return (this.activeThoughts as { postTitle?: string }[]).some(c => c.postTitle);
    },

    get timeMachineCount(): number {
        if (!this.data) return 0;
        const list = (this.data['timeMachineThoughts'] as unknown[]) ?? [];
        if (this.sourceFilter.size === 0) return list.length;
        return list.filter(c => this.sourceFilter.has((c as { source: string }).source)).length;
    },

    get allThoughtsCount(): number {
        if (!this.data) return 0;
        const list = (this.data['allThoughts'] as unknown[]) ?? [];
        if (this.sourceFilter.size === 0) return list.length;
        return list.filter(c => this.sourceFilter.has((c as { source: string }).source)).length;
    },

    get threadCount(): number {
        return (this.groupedThoughts as { title: string | null }[]).filter(g => g.title).length;
    },

    get availableSources(): { name: string; brandColor: string; brandLogoSvg: string }[] {
        if (!this.data) return [];
        const map = new Map<string, { brandColor: string; brandLogoSvg: string }>();
        const list = (this.data['allThoughts'] as { source?: string; brandColor?: string; brandLogoSvg?: string }[]) ?? [];
        list.forEach(t => {
            if (t.source && !map.has(t.source))
                map.set(t.source, { brandColor: t.brandColor ?? '', brandLogoSvg: t.brandLogoSvg ?? '' });
        });
        return [...map.entries()].map(([name, brand]) => ({ name, ...brand }));
    },

    get renderGroups(): unknown[] {
        if (!this.groupByThread) {
            return (this.activeThoughts as unknown[]).map(c => ({ title: null, url: null, thoughts: [c] }));
        }
        return this.groupedThoughts;
    },

    get groupedThoughts(): unknown[] {
        const thoughts = this.activeThoughts as { postTitle?: string; postUrl?: string; postBody?: string; source: string; brandColor?: string }[];
        const groups: { title: string | null; url: string | null; body: string | null; brandColor: string; thoughts: unknown[] }[] = [];
        const map = new Map<string, { title: string | null; url: string | null; body: string | null; brandColor: string; thoughts: unknown[] }>();
        for (const c of thoughts) {
            const key = c.postTitle || '';
            if (!map.has(key)) {
                const g = { title: c.postTitle || null, url: c.postUrl || null, body: c.postBody || null, brandColor: c.brandColor ?? '', thoughts: [] as unknown[] };
                map.set(key, g);
                groups.push(g);
            }
            map.get(key)!.thoughts.push(c);
        }
        return groups;
    },

    get showSearchBox(): boolean {
        const integrations = this.configData?.['integrations'] as Record<string, { configured: boolean }> | undefined;
        if (!integrations?.['omdb']?.configured) return false;
        if (this.alwaysShowSearch) return true;
        if (!this.data || this.data['status'] !== 'Watching') return true;
        // Show search when the active watch provider requires manual input (user picks their own media)
        const watchProvider = this.data['watchProvider'] as string | undefined;
        const prefs = this.configData?.['preferences'] as Record<string, unknown> | undefined;
        const providers = (prefs?.['watchProviders'] as { value: string; requiresManualInput?: boolean }[]) ?? [];
        const provider = providers.find(p => p.value === watchProvider);
        if (provider) return provider.requiresManualInput === true;
        return watchProvider === 'Manual';
    },
};

export default Object.getOwnPropertyDescriptors(_computed);

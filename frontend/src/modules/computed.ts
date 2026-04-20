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

    get collapsedSyncHistory(): { timestamp: string; status: string; title: string | null; count: number; thoughtCount: number; avgDurationMs: number | null }[] {
        type RawEntry = { status: string; title: string | null; timestamp: string; thoughtCount: number; durationMs: number | null };
        const entries = this.syncHistoryEntries as RawEntry[];
        if (entries.length === 0) return [];

        const groups: { timestamp: string; status: string; title: string | null; count: number; thoughtCount: number; avgDurationMs: number | null }[] = [];
        let cur = { ...entries[0], count: 1, totalDurationMs: entries[0].durationMs as number | null };

        for (let i = 1; i < entries.length; i++) {
            const e = entries[i];
            if (e.status === cur.status && e.title === cur.title) {
                cur.count++;
                if (e.durationMs != null)
                    cur.totalDurationMs = (cur.totalDurationMs ?? 0) + e.durationMs;
            } else {
                groups.push({
                    timestamp: cur.timestamp,
                    status: cur.status,
                    title: cur.title,
                    count: cur.count,
                    thoughtCount: cur.thoughtCount,
                    avgDurationMs: cur.totalDurationMs != null ? Math.round(cur.totalDurationMs / cur.count) : null,
                });
                cur = { ...e, count: 1, totalDurationMs: e.durationMs };
            }
        }
        groups.push({
            timestamp: cur.timestamp,
            status: cur.status,
            title: cur.title,
            count: cur.count,
            thoughtCount: cur.thoughtCount,
            avgDurationMs: cur.totalDurationMs != null ? Math.round(cur.totalDurationMs / cur.count) : null,
        });
        return groups;
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

        const src = this.mode === 'time'
            ? ((this.data['timeMachineThoughts'] as unknown[]) ?? [])
            : ((this.data['allThoughts'] as unknown[]) ?? []);

        const sentimentEnabled = (this.configData?.['preferences'] as Record<string, unknown> | undefined)
            ?.['enableSentimentAnalysis'] as boolean | undefined;

        // Fast path: no filters active — return the source array directly
        if (this.sourceFilter.size === 0 && (!sentimentEnabled || this.sentimentCategory === 'all')) {
            return src;
        }

        // Cache hit: all inputs match the last computation
        if (
            this._cAtResult !== null &&
            this._cAtSrc === src &&
            this._cAtMode === this.mode &&
            this._cAtFilter === this.sourceFilter &&
            this._cAtSentCat === this.sentimentCategory &&
            this._cAtSentEnabled === sentimentEnabled
        ) {
            return this._cAtResult;
        }

        let list: unknown[] = src;
        if (this.sourceFilter.size > 0)
            list = list.filter(c => this.sourceFilter.has((c as { source: string }).source));
        if (sentimentEnabled && this.sentimentCategory !== 'all') {
            list = list.filter(c => {
                const s = (c as { sentiment?: number | null }).sentiment;
                if (s == null) return false;
                if (this.sentimentCategory === 'positive') return s >= 0.05;
                if (this.sentimentCategory === 'negative') return s <= -0.05;
                if (this.sentimentCategory === 'mixed') return s > -0.05 && s < 0.05;
                return true;
            });
        }

        // Store in cache
        this._cAtSrc = src;
        this._cAtMode = this.mode;
        this._cAtFilter = this.sourceFilter;
        this._cAtSentCat = this.sentimentCategory;
        this._cAtSentEnabled = sentimentEnabled;
        this._cAtResult = list;
        return list;
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

        // Cache hit: activeThoughts reference is stable when its own cache is valid
        if (this._cGtInput === (thoughts as unknown[]) && this._cGtResult !== null) {
            return this._cGtResult;
        }

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

        this._cGtInput = thoughts as unknown[];
        this._cGtResult = groups;
        return groups;
    },

    get hasWatchProvider(): boolean {
        const integrations = (this.configData?.['integrations'] as Record<string, { configured?: boolean; providerTypes?: string[]; disabled?: boolean }> | undefined) ?? {};
        return Object.values(integrations).some(i => i.providerTypes?.includes('watchState') && i.configured && !i.disabled);
    },

    get hasCommentSource(): boolean {
        const integrations = (this.configData?.['integrations'] as Record<string, { configured?: boolean; providerTypes?: string[]; disabled?: boolean }> | undefined) ?? {};
        return Object.values(integrations).some(i => i.providerTypes?.includes('thought') && i.configured && !i.disabled);
    },

    get hasCompletedSync(): boolean {
        const status = this.data?.['status'] as string | undefined;
        return status === 'Watching' || status === 'Idle';
    },

    get checklistAllComplete(): boolean {
        return this.hasWatchProvider && this.hasCommentSource && this.hasCompletedSync;
    },

    get showChecklist(): boolean {
        if (this.authState !== 'app') return false;
        if (this.wizardActive) return false;
        if (this.checklistAutoComplete) return true;
        if (this.checklistAllComplete) return false;
        if (this.checklistDismissed) return false;
        if (localStorage.getItem('wb_checklistCompleted') === 'true') return false;
        return true;
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

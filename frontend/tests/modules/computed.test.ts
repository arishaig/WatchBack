import { describe, it, expect } from 'vitest';
import computedDescriptors from '../../src/modules/computed';

// Helper: call a named getter with a mock context
function get<K extends keyof typeof computedDescriptors>(
    name: K,
    ctx: object,
): unknown {
    return computedDescriptors[name].get!.call(ctx);
}

// Minimal mock state factory
function makeState(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        data: null,
        mode: 'all',
        sourceFilter: new Set<string>(),
        logEntries: [],
        logLevel: 'All',
        groupByThread: false,
        alwaysShowSearch: false,
        configData: null,
        // Wizard/checklist state
        authState: 'app',
        wizardActive: false,
        checklistDismissed: false,
        checklistAutoComplete: false,
        // computed getters that other getters depend on — supplied as plain values
        activeThoughts: [],
        groupedThoughts: [],
        hasWatchProvider: false,
        hasCommentSource: false,
        hasCompletedSync: false,
        checklistAllComplete: false,
        ...overrides,
    };
}

// ── filteredLogs ──────────────────────────────────────────────────────────────

describe('filteredLogs', () => {
    const entry = (level: string) => ({ level, message: 'msg' });

    it('returns all entries when logLevel is "All"', () => {
        const ctx = makeState({
            logEntries: [entry('Trace'), entry('Debug'), entry('Warning')],
            logLevel: 'All',
        });
        expect(get('filteredLogs', ctx)).toHaveLength(3);
    });

    it('filters to Warning and above when logLevel is "Warning"', () => {
        const ctx = makeState({
            logEntries: [entry('Trace'), entry('Debug'), entry('Information'), entry('Warning'), entry('Error')],
            logLevel: 'Warning',
        });
        const result = get('filteredLogs', ctx) as { level: string }[];
        expect(result.map(e => e.level)).toEqual(['Warning', 'Error']);
    });

    it('shows only Critical when logLevel is "Critical"', () => {
        const ctx = makeState({
            logEntries: [entry('Trace'), entry('Error'), entry('Critical')],
            logLevel: 'Critical',
        });
        const result = get('filteredLogs', ctx) as { level: string }[];
        expect(result.map(e => e.level)).toEqual(['Critical']);
    });

    it('returns empty array when no entries', () => {
        const ctx = makeState({ logEntries: [], logLevel: 'All' });
        expect(get('filteredLogs', ctx)).toEqual([]);
    });
});

// ── showTimeMachine ───────────────────────────────────────────────────────────

describe('showTimeMachine', () => {
    it('returns false when data is null', () => {
        expect(get('showTimeMachine', makeState())).toBe(false);
    });

    it('returns false when timeMachineThoughts is empty', () => {
        const ctx = makeState({ data: { timeMachineThoughts: [] } });
        expect(get('showTimeMachine', ctx)).toBe(false);
    });

    it('returns false when premiere + timeMachineDays is in the future', () => {
        const future = new Date(Date.now() + 30 * 86_400_000).toISOString();
        const ctx = makeState({
            data: {
                timeMachineThoughts: [{ id: 1 }],
                timeMachineDays: 14,
                metadata: { releaseDate: future },
            },
        });
        expect(get('showTimeMachine', ctx)).toBe(false);
    });

    it('returns true when premiere + timeMachineDays is in the past', () => {
        const past = new Date(Date.now() - 60 * 86_400_000).toISOString();
        const ctx = makeState({
            data: {
                timeMachineThoughts: [{ id: 1 }],
                timeMachineDays: 14,
                metadata: { releaseDate: past },
            },
        });
        expect(get('showTimeMachine', ctx)).toBe(true);
    });

    it('returns true when there are thoughts and no releaseDate', () => {
        const ctx = makeState({
            data: {
                timeMachineThoughts: [{ id: 1 }],
                metadata: {},
            },
        });
        expect(get('showTimeMachine', ctx)).toBe(true);
    });
});

// ── activeThoughts ────────────────────────────────────────────────────────────

describe('activeThoughts', () => {
    it('returns empty array when data is null', () => {
        expect(get('activeThoughts', makeState())).toEqual([]);
    });

    it('returns allThoughts when mode is not "time"', () => {
        const thoughts = [{ id: 1, source: 'reddit' }, { id: 2, source: 'reddit' }];
        const ctx = makeState({ data: { allThoughts: thoughts }, mode: 'all' });
        expect(get('activeThoughts', ctx)).toEqual(thoughts);
    });

    it('returns timeMachineThoughts when mode is "time"', () => {
        const tm = [{ id: 10, source: 'reddit' }];
        const ctx = makeState({
            data: { allThoughts: [{ id: 1 }], timeMachineThoughts: tm },
            mode: 'time',
        });
        expect(get('activeThoughts', ctx)).toEqual(tm);
    });

    it('filters by sourceFilter when set', () => {
        const thoughts = [
            { id: 1, source: 'reddit' },
            { id: 2, source: 'letterboxd' },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            mode: 'all',
            sourceFilter: new Set(['reddit']),
        });
        const result = get('activeThoughts', ctx) as { source: string }[];
        expect(result).toHaveLength(1);
        expect(result[0].source).toBe('reddit');
    });

    it('returns all thoughts when sourceFilter is empty', () => {
        const thoughts = [{ id: 1, source: 'reddit' }, { id: 2, source: 'tv' }];
        const ctx = makeState({ data: { allThoughts: thoughts }, sourceFilter: new Set() });
        expect(get('activeThoughts', ctx)).toHaveLength(2);
    });

    it('returns all thoughts when sentimentCategory is "all" even with enableSentimentAnalysis true', () => {
        const thoughts = [
            { id: 1, source: 'reddit', sentiment: 0.8 },
            { id: 2, source: 'reddit', sentiment: -0.6 },
            { id: 3, source: 'reddit', sentiment: null },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            sentimentCategory: 'all',
            configData: { preferences: { enableSentimentAnalysis: true } },
        });
        expect(get('activeThoughts', ctx)).toHaveLength(3);
    });

    it('filters to positive thoughts (>= 0.05) when sentimentCategory is "positive"', () => {
        const thoughts = [
            { id: 1, source: 'reddit', sentiment: 0.5 },
            { id: 2, source: 'reddit', sentiment: 0.04 },
            { id: 3, source: 'reddit', sentiment: -0.3 },
            { id: 4, source: 'reddit', sentiment: null },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            sentimentCategory: 'positive',
            configData: { preferences: { enableSentimentAnalysis: true } },
        });
        const result = get('activeThoughts', ctx) as { id: number }[];
        expect(result).toHaveLength(1);
        expect(result[0].id).toBe(1);
    });

    it('filters to negative thoughts (<= -0.05) when sentimentCategory is "negative"', () => {
        const thoughts = [
            { id: 1, source: 'reddit', sentiment: 0.5 },
            { id: 2, source: 'reddit', sentiment: -0.1 },
            { id: 3, source: 'reddit', sentiment: -0.9 },
            { id: 4, source: 'reddit', sentiment: null },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            sentimentCategory: 'negative',
            configData: { preferences: { enableSentimentAnalysis: true } },
        });
        const result = get('activeThoughts', ctx) as { id: number }[];
        expect(result).toHaveLength(2);
        expect(result.map(t => t.id)).toEqual([2, 3]);
    });

    it('filters to mixed thoughts (-0.05 < s < 0.05) when sentimentCategory is "mixed"', () => {
        const thoughts = [
            { id: 1, source: 'reddit', sentiment: 0.0 },
            { id: 2, source: 'reddit', sentiment: 0.04 },
            { id: 3, source: 'reddit', sentiment: -0.04 },
            { id: 4, source: 'reddit', sentiment: 0.5 },
            { id: 5, source: 'reddit', sentiment: null },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            sentimentCategory: 'mixed',
            configData: { preferences: { enableSentimentAnalysis: true } },
        });
        const result = get('activeThoughts', ctx) as { id: number }[];
        expect(result).toHaveLength(3);
        expect(result.map(t => t.id)).toEqual([1, 2, 3]);
    });

    it('ignores sentiment category filter when enableSentimentAnalysis is false', () => {
        const thoughts = [
            { id: 1, source: 'reddit', sentiment: 0.8 },
            { id: 2, source: 'reddit', sentiment: -0.6 },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            sentimentCategory: 'positive',
            configData: { preferences: { enableSentimentAnalysis: false } },
        });
        expect(get('activeThoughts', ctx)).toHaveLength(2);
    });

    it('ignores sentiment category filter when configData is null', () => {
        const thoughts = [
            { id: 1, source: 'reddit', sentiment: -0.8 },
        ];
        const ctx = makeState({
            data: { allThoughts: thoughts },
            sentimentCategory: 'positive',
            configData: null,
        });
        expect(get('activeThoughts', ctx)).toHaveLength(1);
    });
});

// ── hasThreadGroups ───────────────────────────────────────────────────────────

describe('hasThreadGroups', () => {
    it('returns false when activeThoughts has no postTitle', () => {
        const ctx = makeState({ activeThoughts: [{ id: 1 }, { id: 2 }] });
        expect(get('hasThreadGroups', ctx)).toBe(false);
    });

    it('returns true when at least one thought has postTitle', () => {
        const ctx = makeState({
            activeThoughts: [{ id: 1 }, { id: 2, postTitle: 'A Thread' }],
        });
        expect(get('hasThreadGroups', ctx)).toBe(true);
    });

    it('returns false for empty activeThoughts', () => {
        const ctx = makeState({ activeThoughts: [] });
        expect(get('hasThreadGroups', ctx)).toBe(false);
    });
});

// ── timeMachineCount ──────────────────────────────────────────────────────────

describe('timeMachineCount', () => {
    it('returns 0 when data is null', () => {
        expect(get('timeMachineCount', makeState())).toBe(0);
    });

    it('counts all when no source filter', () => {
        const ctx = makeState({
            data: { timeMachineThoughts: [{ source: 'a' }, { source: 'b' }] },
            sourceFilter: new Set(),
        });
        expect(get('timeMachineCount', ctx)).toBe(2);
    });

    it('filters by source when filter is set', () => {
        const ctx = makeState({
            data: { timeMachineThoughts: [{ source: 'a' }, { source: 'b' }, { source: 'a' }] },
            sourceFilter: new Set(['a']),
        });
        expect(get('timeMachineCount', ctx)).toBe(2);
    });
});

// ── allThoughtsCount ──────────────────────────────────────────────────────────

describe('allThoughtsCount', () => {
    it('returns 0 when data is null', () => {
        expect(get('allThoughtsCount', makeState())).toBe(0);
    });

    it('counts all thoughts without filter', () => {
        const ctx = makeState({
            data: { allThoughts: [{ source: 'x' }, { source: 'y' }, { source: 'z' }] },
        });
        expect(get('allThoughtsCount', ctx)).toBe(3);
    });

    it('counts only matching source when filter is set', () => {
        const ctx = makeState({
            data: { allThoughts: [{ source: 'x' }, { source: 'x' }, { source: 'y' }] },
            sourceFilter: new Set(['x']),
        });
        expect(get('allThoughtsCount', ctx)).toBe(2);
    });
});

// ── threadCount ───────────────────────────────────────────────────────────────

describe('threadCount', () => {
    it('counts only groups with non-null title', () => {
        const ctx = makeState({
            groupedThoughts: [
                { title: 'Thread A', thoughts: [] },
                { title: null, thoughts: [] },
                { title: 'Thread B', thoughts: [] },
            ],
        });
        expect(get('threadCount', ctx)).toBe(2);
    });

    it('returns 0 when no titled groups', () => {
        const ctx = makeState({
            groupedThoughts: [{ title: null }, { title: null }],
        });
        expect(get('threadCount', ctx)).toBe(0);
    });

    it('returns 0 for empty groupedThoughts', () => {
        const ctx = makeState({ groupedThoughts: [] });
        expect(get('threadCount', ctx)).toBe(0);
    });
});

// ── availableSources ──────────────────────────────────────────────────────────

describe('availableSources', () => {
    it('returns empty array when data is null', () => {
        expect(get('availableSources', makeState())).toEqual([]);
    });

    it('returns unique sources from allThoughts', () => {
        const ctx = makeState({
            data: {
                allThoughts: [
                    { source: 'reddit', brandColor: '#ff4500', brandLogoSvg: '<svg/>' },
                    { source: 'letterboxd', brandColor: '#00c030', brandLogoSvg: '' },
                    { source: 'reddit', brandColor: '#ff4500', brandLogoSvg: '<svg/>' },
                ],
            },
        });
        const result = get('availableSources', ctx) as { name: string; brandColor: string; brandLogoSvg: string }[];
        expect(result.map(s => s.name)).toContain('reddit');
        expect(result.map(s => s.name)).toContain('letterboxd');
        expect(result).toHaveLength(2);
    });

    it('omits thoughts without a source', () => {
        const ctx = makeState({
            data: { allThoughts: [{ source: 'reddit' }, {}] },
        });
        const result = get('availableSources', ctx) as { name: string; brandColor: string; brandLogoSvg: string }[];
        expect(result).toEqual([{ name: 'reddit', brandColor: '', brandLogoSvg: '' }]);
    });
});

// ── groupedThoughts ───────────────────────────────────────────────────────────

describe('groupedThoughts', () => {
    it('groups thoughts with the same postTitle together', () => {
        const thoughts = [
            { postTitle: 'Thread A', postUrl: 'http://a', source: 'reddit' },
            { postTitle: 'Thread A', postUrl: 'http://a', source: 'reddit' },
            { postTitle: 'Thread B', source: 'reddit' },
        ];
        // Need activeThoughts in ctx for groupedThoughts to use
        const ctx = makeState({ activeThoughts: thoughts });
        const result = get('groupedThoughts', ctx) as { title: string; thoughts: unknown[] }[];
        expect(result).toHaveLength(2);
        expect(result[0].title).toBe('Thread A');
        expect(result[0].thoughts).toHaveLength(2);
        expect(result[1].title).toBe('Thread B');
    });

    it('groups thoughts without postTitle under null title', () => {
        const thoughts = [{ source: 'reddit' }, { source: 'tv' }];
        const ctx = makeState({ activeThoughts: thoughts });
        const result = get('groupedThoughts', ctx) as { title: string | null; thoughts: unknown[] }[];
        expect(result).toHaveLength(1);
        expect(result[0].title).toBeNull();
        expect(result[0].thoughts).toHaveLength(2);
    });

    it('preserves insertion order of groups', () => {
        const thoughts = [
            { postTitle: 'Z', source: 'x' },
            { postTitle: 'A', source: 'x' },
        ];
        const ctx = makeState({ activeThoughts: thoughts });
        const result = get('groupedThoughts', ctx) as { title: string }[];
        expect(result[0].title).toBe('Z');
        expect(result[1].title).toBe('A');
    });
});

// ── renderGroups ──────────────────────────────────────────────────────────────

describe('renderGroups', () => {
    it('wraps each thought in its own group when groupByThread is false', () => {
        const thoughts = [{ id: 1 }, { id: 2 }];
        const ctx = makeState({ activeThoughts: thoughts, groupByThread: false });
        const result = get('renderGroups', ctx) as { title: null; thoughts: unknown[] }[];
        expect(result).toHaveLength(2);
        expect(result[0].title).toBeNull();
        expect(result[0].thoughts).toEqual([{ id: 1 }]);
    });

    it('delegates to groupedThoughts when groupByThread is true', () => {
        const grouped = [{ title: 'Thread', url: null, body: null, thoughts: [{ id: 1 }] }];
        const ctx = makeState({ groupByThread: true, groupedThoughts: grouped, activeThoughts: [] });
        expect(get('renderGroups', ctx)).toEqual(grouped);
    });
});

// ── showSearchBox ─────────────────────────────────────────────────────────────

describe('showSearchBox', () => {
    it('returns false when OMDB is not configured', () => {
        const ctx = makeState({
            configData: { integrations: { omdb: { configured: false } } },
        });
        expect(get('showSearchBox', ctx)).toBe(false);
    });

    it('returns false when configData has no omdb integration', () => {
        const ctx = makeState({ configData: { integrations: {} } });
        expect(get('showSearchBox', ctx)).toBe(false);
    });

    it('returns true when alwaysShowSearch is enabled (OMDB configured)', () => {
        const ctx = makeState({
            configData: { integrations: { omdb: { configured: true } } },
            alwaysShowSearch: true,
            data: { status: 'Watching', watchProvider: 'jellyfin' },
        });
        expect(get('showSearchBox', ctx)).toBe(true);
    });

    it('returns true when not currently watching (OMDB configured)', () => {
        const ctx = makeState({
            configData: { integrations: { omdb: { configured: true } } },
            alwaysShowSearch: false,
            data: { status: 'Idle' },
        });
        expect(get('showSearchBox', ctx)).toBe(true);
    });

    it('returns false when Watching with non-Manual provider', () => {
        const ctx = makeState({
            configData: { integrations: { omdb: { configured: true } } },
            alwaysShowSearch: false,
            data: { status: 'Watching', watchProvider: 'jellyfin' },
        });
        expect(get('showSearchBox', ctx)).toBe(false);
    });

    it('returns true when Watching with Manual provider', () => {
        const ctx = makeState({
            configData: { integrations: { omdb: { configured: true } } },
            alwaysShowSearch: false,
            data: { status: 'Watching', watchProvider: 'Manual' },
        });
        expect(get('showSearchBox', ctx)).toBe(true);
    });

    it('returns true when data is null (OMDB configured)', () => {
        const ctx = makeState({
            configData: { integrations: { omdb: { configured: true } } },
            alwaysShowSearch: false,
            data: null,
        });
        expect(get('showSearchBox', ctx)).toBe(true);
    });
});

// ── hasWatchProvider ──────────────────────────────────────────────────────────

describe('hasWatchProvider', () => {
    it('returns false when configData is null', () => {
        expect(get('hasWatchProvider', makeState())).toBe(false);
    });

    it('returns false when no watchState provider is configured', () => {
        const ctx = makeState({
            configData: {
                integrations: {
                    jellyfin: { configured: false, providerTypes: ['watchState'] },
                    reddit: { configured: true, providerTypes: ['thought'] },
                },
            },
        });
        expect(get('hasWatchProvider', ctx)).toBe(false);
    });

    it('returns true when a watchState provider is configured', () => {
        const ctx = makeState({
            configData: {
                integrations: {
                    jellyfin: { configured: true, providerTypes: ['watchState'] },
                },
            },
        });
        expect(get('hasWatchProvider', ctx)).toBe(true);
    });

    it('returns true when a dual-role provider with watchState is configured', () => {
        const ctx = makeState({
            configData: {
                integrations: {
                    trakt: { configured: true, providerTypes: ['watchState', 'thought'] },
                },
            },
        });
        expect(get('hasWatchProvider', ctx)).toBe(true);
    });
});

// ── hasCommentSource ─────────────────────────────────────────────────────────

describe('hasCommentSource', () => {
    it('returns false when configData is null', () => {
        expect(get('hasCommentSource', makeState())).toBe(false);
    });

    it('returns false when no thought provider is configured', () => {
        const ctx = makeState({
            configData: {
                integrations: {
                    reddit: { configured: false, providerTypes: ['thought'] },
                    jellyfin: { configured: true, providerTypes: ['watchState'] },
                },
            },
        });
        expect(get('hasCommentSource', ctx)).toBe(false);
    });

    it('returns true when a thought provider is configured', () => {
        const ctx = makeState({
            configData: {
                integrations: {
                    reddit: { configured: true, providerTypes: ['thought'] },
                },
            },
        });
        expect(get('hasCommentSource', ctx)).toBe(true);
    });
});

// ── hasCompletedSync ─────────────────────────────────────────────────────────

describe('hasCompletedSync', () => {
    it('returns false when data is null', () => {
        expect(get('hasCompletedSync', makeState())).toBe(false);
    });

    it('returns false when status is Error', () => {
        const ctx = makeState({ data: { status: 'Error' } });
        expect(get('hasCompletedSync', ctx)).toBe(false);
    });

    it('returns true when status is Watching', () => {
        const ctx = makeState({ data: { status: 'Watching' } });
        expect(get('hasCompletedSync', ctx)).toBe(true);
    });

    it('returns true when status is Idle', () => {
        const ctx = makeState({ data: { status: 'Idle' } });
        expect(get('hasCompletedSync', ctx)).toBe(true);
    });
});

// ── checklistAllComplete ─────────────────────────────────────────────────────

describe('checklistAllComplete', () => {
    it('returns false when nothing is complete', () => {
        const ctx = makeState({
            hasWatchProvider: false,
            hasCommentSource: false,
            hasCompletedSync: false,
        });
        expect(get('checklistAllComplete', ctx)).toBe(false);
    });

    it('returns false when only some items are complete', () => {
        const ctx = makeState({
            hasWatchProvider: true,
            hasCommentSource: true,
            hasCompletedSync: false,
        });
        expect(get('checklistAllComplete', ctx)).toBe(false);
    });

    it('returns true when all items are complete', () => {
        const ctx = makeState({
            hasWatchProvider: true,
            hasCommentSource: true,
            hasCompletedSync: true,
        });
        expect(get('checklistAllComplete', ctx)).toBe(true);
    });
});

// ── showChecklist ────────────────────────────────────────────────────────────

describe('showChecklist', () => {
    it('returns false when authState is not app', () => {
        const ctx = makeState({ authState: 'login' });
        expect(get('showChecklist', ctx)).toBe(false);
    });

    it('returns false when wizard is active', () => {
        const ctx = makeState({ authState: 'app', wizardActive: true });
        expect(get('showChecklist', ctx)).toBe(false);
    });

    it('returns false when all checklist items are complete', () => {
        const ctx = makeState({
            authState: 'app',
            checklistAllComplete: true,
        });
        expect(get('showChecklist', ctx)).toBe(false);
    });

    it('returns false when checklist is dismissed', () => {
        const ctx = makeState({
            authState: 'app',
            checklistDismissed: true,
        });
        expect(get('showChecklist', ctx)).toBe(false);
    });

    it('returns true when checklist is auto-completing (all done animation)', () => {
        const ctx = makeState({
            authState: 'app',
            checklistAutoComplete: true,
            checklistAllComplete: true,
        });
        expect(get('showChecklist', ctx)).toBe(true);
    });

    it('returns true when app state and incomplete setup', () => {
        const ctx = makeState({ authState: 'app' });
        expect(get('showChecklist', ctx)).toBe(true);
    });

    it('returns false when wb_checklistCompleted is set in localStorage', () => {
        localStorage.setItem('wb_checklistCompleted', 'true');
        const ctx = makeState({ authState: 'app' });
        expect(get('showChecklist', ctx)).toBe(false);
        localStorage.removeItem('wb_checklistCompleted');
    });
});

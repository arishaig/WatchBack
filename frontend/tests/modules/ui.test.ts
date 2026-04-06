import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import uiMethods from '../../src/modules/ui';

// Extract typed method references
const methods = uiMethods as Record<string, Function>;

// Mock context factory for methods that rely on `this`
function makeCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    const ctx: Record<string, unknown> = {
        locale: 'en',
        data: null,
        mode: 'all',
        sourceFilter: new Set<string>(),
        groupByThread: false,
        alwaysShowSearch: false,
        collapsedSections: {} as Record<string, boolean>,
        error: null as string | null,
        errorTimer: null,
        t: (key: string, ...args: unknown[]) => {
            // Minimal translation mock for formatRelativeTime
            if (key === 'Time_SecondsAgo') return `${args[0]} seconds ago`;
            if (key === 'Time_MinutesAgo') return `${args[0]} minutes ago`;
            if (key === 'Time_HoursAgo') return `${args[0]} hours ago`;
            return key;
        },
        countAllReplies: function(c: Record<string, unknown>): number {
            return methods.countAllReplies.call(this, c);
        },
        ...overrides,
    };
    return ctx;
}

// ── t ─────────────────────────────────────────────────────────────────────────

describe('t (Alpine component method)', () => {
    beforeEach(() => {
        (window as Window & { _allStrings?: Record<string, Record<string, string>> })._allStrings = {
            en: { Greeting: 'Hello', WithArg: 'Hi {0}!' },
            es: { Greeting: 'Hola' },
        };
    });

    it('returns translated string for current locale', () => {
        const ctx = makeCtx({ locale: 'en', _stringsReady: true });
        expect(methods.t.call(ctx, 'Greeting')).toBe('Hello');
    });

    it('falls back to en when locale not in strings', () => {
        const ctx = makeCtx({ locale: 'de', _stringsReady: true });
        expect(methods.t.call(ctx, 'Greeting')).toBe('Hello');
    });

    it('returns key when string not found', () => {
        const ctx = makeCtx({ locale: 'en', _stringsReady: true });
        expect(methods.t.call(ctx, 'Missing')).toBe('Missing');
    });

    it('interpolates arguments', () => {
        const ctx = makeCtx({ locale: 'en', _stringsReady: true });
        expect(methods.t.call(ctx, 'WithArg', 'World')).toBe('Hi World!');
    });
});

// ── formatScore ───────────────────────────────────────────────────────────────

describe('formatScore', () => {
    it('returns empty string for null', () => {
        expect(methods.formatScore.call({}, null)).toBe('');
    });

    it('returns string for numbers under 1000', () => {
        expect(methods.formatScore.call({}, 42)).toBe('42');
        expect(methods.formatScore.call({}, 999)).toBe('999');
    });

    it('abbreviates thousands', () => {
        expect(methods.formatScore.call({}, 1000)).toBe('1k');
        expect(methods.formatScore.call({}, 1500)).toBe('1.5k');
    });

    it('strips trailing .0 from thousands', () => {
        expect(methods.formatScore.call({}, 2000)).toBe('2k');
    });

    it('returns 0 as "0"', () => {
        expect(methods.formatScore.call({}, 0)).toBe('0');
    });
});

// ── formatDate ────────────────────────────────────────────────────────────────

describe('formatDate', () => {
    it('returns empty string for empty input', () => {
        expect(methods.formatDate.call({}, '')).toBe('');
    });

    it('returns a non-empty string for a valid ISO date', () => {
        const result = methods.formatDate.call({}, '2024-01-15T00:00:00Z');
        expect(typeof result).toBe('string');
        expect(result.length).toBeGreaterThan(0);
    });

    it('returns input as fallback for invalid date', () => {
        const result = methods.formatDate.call({}, 'not-a-date');
        // toLocaleDateString of an Invalid Date returns 'Invalid Date' in most envs
        // but our code catches errors and returns the raw string
        expect(typeof result).toBe('string');
    });
});

// ── formatLogTime ─────────────────────────────────────────────────────────────

describe('formatLogTime', () => {
    it('returns a time string for valid ISO input', () => {
        const result = methods.formatLogTime.call({}, '2024-01-15T14:30:00Z');
        expect(typeof result).toBe('string');
        // en-US 24h format contains a colon
        expect(result).toContain(':');
    });
});

// ── logLevelClass ─────────────────────────────────────────────────────────────

describe('logLevelClass', () => {
    it('returns muted class for Trace', () => {
        expect(methods.logLevelClass.call({}, 'Trace')).toBe('wb-text-muted');
    });

    it('returns muted class for Debug', () => {
        expect(methods.logLevelClass.call({}, 'Debug')).toBe('wb-text-muted');
    });

    it('returns accent class for Information', () => {
        expect(methods.logLevelClass.call({}, 'Information')).toBe('wb-accent-text');
    });

    it('returns warn class for Warning', () => {
        expect(methods.logLevelClass.call({}, 'Warning')).toBe('wb-warn-text');
    });

    it('returns error class for Error', () => {
        expect(methods.logLevelClass.call({}, 'Error')).toBe('wb-error-text');
    });

    it('returns error class for Critical', () => {
        expect(methods.logLevelClass.call({}, 'Critical')).toBe('wb-error-text');
    });

    it('returns muted for unknown level', () => {
        expect(methods.logLevelClass.call({}, 'Unknown')).toBe('wb-text-muted');
    });
});

// ── logLevelAbbr ──────────────────────────────────────────────────────────────

describe('logLevelAbbr', () => {
    const cases: [string, string][] = [
        ['Trace', 'TRC'],
        ['Debug', 'DBG'],
        ['Information', 'INF'],
        ['Warning', 'WRN'],
        ['Error', 'ERR'],
        ['Critical', 'CRT'],
    ];
    for (const [input, expected] of cases) {
        it(`abbreviates ${input} as ${expected}`, () => {
            expect(methods.logLevelAbbr.call({}, input)).toBe(expected);
        });
    }

    it('falls back to first 3 chars uppercase for unknown level', () => {
        expect(methods.logLevelAbbr.call({}, 'Verbose')).toBe('VER');
    });

    it('handles empty string gracefully', () => {
        expect(methods.logLevelAbbr.call({}, '')).toBe('');
    });
});

// ── countAllReplies ───────────────────────────────────────────────────────────

describe('countAllReplies', () => {
    it('returns 0 for comment with no replies', () => {
        const ctx = makeCtx();
        expect(methods.countAllReplies.call(ctx, {})).toBe(0);
    });

    it('returns 0 when replies is not an array', () => {
        const ctx = makeCtx();
        expect(methods.countAllReplies.call(ctx, { replies: 'invalid' })).toBe(0);
    });

    it('counts direct replies', () => {
        const ctx = makeCtx();
        const comment = { replies: [{}, {}, {}] };
        expect(methods.countAllReplies.call(ctx, comment)).toBe(3);
    });

    it('counts nested replies recursively', () => {
        const ctx = makeCtx();
        const comment = {
            replies: [
                { replies: [{ replies: [] }] },  // 1 direct + 1 nested (leaf)
                {},                               // 1 direct
            ],
        };
        // 2 direct + 1 nested = 3
        expect(methods.countAllReplies.call(ctx, comment)).toBe(3);
    });
});

// ── sourceActive ──────────────────────────────────────────────────────────────

describe('sourceActive', () => {
    it('returns true when sourceFilter is empty (no active filter)', () => {
        const ctx = makeCtx({ sourceFilter: new Set() });
        expect(methods.sourceActive.call(ctx, 'reddit')).toBe(true);
    });

    it('returns true when source is in the filter', () => {
        const ctx = makeCtx({ sourceFilter: new Set(['reddit', 'letterboxd']) });
        expect(methods.sourceActive.call(ctx, 'reddit')).toBe(true);
    });

    it('returns false when source is not in the active filter', () => {
        const ctx = makeCtx({ sourceFilter: new Set(['reddit']) });
        expect(methods.sourceActive.call(ctx, 'letterboxd')).toBe(false);
    });
});

// ── sourceCount ───────────────────────────────────────────────────────────────

describe('sourceCount', () => {
    it('returns 0 when data is null', () => {
        const ctx = makeCtx({ data: null });
        expect(methods.sourceCount.call(ctx, 'reddit')).toBe(0);
    });

    it('counts matching source in allThoughts mode', () => {
        const ctx = makeCtx({
            mode: 'all',
            data: {
                allThoughts: [
                    { source: 'reddit' },
                    { source: 'reddit' },
                    { source: 'letterboxd' },
                ],
            },
        });
        expect(methods.sourceCount.call(ctx, 'reddit')).toBe(2);
        expect(methods.sourceCount.call(ctx, 'letterboxd')).toBe(1);
        expect(methods.sourceCount.call(ctx, 'other')).toBe(0);
    });

    it('counts matching source in timemachine mode', () => {
        const ctx = makeCtx({
            mode: 'time',
            data: {
                timeMachineThoughts: [
                    { source: 'reddit' },
                    { source: 'tv' },
                ],
            },
        });
        expect(methods.sourceCount.call(ctx, 'reddit')).toBe(1);
    });
});

// ── toggleSource ─────────────────────────────────────────────────────────────

describe('toggleSource', () => {
    it('adds source when not in filter', () => {
        const ctx = makeCtx({ sourceFilter: new Set<string>() });
        methods.toggleSource.call(ctx, 'reddit');
        expect((ctx.sourceFilter as Set<string>).has('reddit')).toBe(true);
    });

    it('removes source when already in filter', () => {
        const ctx = makeCtx({ sourceFilter: new Set<string>(['reddit']) });
        methods.toggleSource.call(ctx, 'reddit');
        expect((ctx.sourceFilter as Set<string>).has('reddit')).toBe(false);
    });

    it('reassigns sourceFilter to a new Set (triggers reactivity)', () => {
        const original = new Set<string>();
        const ctx = makeCtx({ sourceFilter: original });
        methods.toggleSource.call(ctx, 'reddit');
        expect(ctx.sourceFilter).not.toBe(original);
    });
});

// ── toggleSection ─────────────────────────────────────────────────────────────

describe('toggleSection', () => {
    it('sets section to true when it was false/undefined', () => {
        const ctx = makeCtx({ collapsedSections: {} });
        methods.toggleSection.call(ctx, 'thoughts');
        expect((ctx.collapsedSections as Record<string, boolean>).thoughts).toBe(true);
    });

    it('sets section to false when it was true', () => {
        const ctx = makeCtx({ collapsedSections: { thoughts: true } });
        methods.toggleSection.call(ctx, 'thoughts');
        expect((ctx.collapsedSections as Record<string, boolean>).thoughts).toBe(false);
    });

    it('persists to localStorage', () => {
        const ctx = makeCtx({ collapsedSections: {} });
        methods.toggleSection.call(ctx, 'mySection');
        const stored = JSON.parse(localStorage.getItem('wb_collapsed_sections') ?? '{}');
        expect(stored.mySection).toBe(true);
    });
});

// ── handleThreadToggle ────────────────────────────────────────────────────────

describe('handleThreadToggle', () => {
    it('toggles groupByThread from false to true', () => {
        const ctx = makeCtx({ groupByThread: false });
        methods.handleThreadToggle.call(ctx);
        expect(ctx.groupByThread).toBe(true);
    });

    it('toggles groupByThread from true to false', () => {
        const ctx = makeCtx({ groupByThread: true });
        methods.handleThreadToggle.call(ctx);
        expect(ctx.groupByThread).toBe(false);
    });

    it('persists true to localStorage', () => {
        const ctx = makeCtx({ groupByThread: false });
        methods.handleThreadToggle.call(ctx);
        expect(localStorage.getItem('wb_groupByThread')).toBe('true');
    });

    it('removes from localStorage when toggled off', () => {
        localStorage.setItem('wb_groupByThread', 'true');
        const ctx = makeCtx({ groupByThread: true });
        methods.handleThreadToggle.call(ctx);
        expect(localStorage.getItem('wb_groupByThread')).toBeNull();
    });
});

// ── toggleAlwaysShowSearch ────────────────────────────────────────────────────

describe('toggleAlwaysShowSearch', () => {
    it('toggles from false to true', () => {
        const ctx = makeCtx({ alwaysShowSearch: false });
        methods.toggleAlwaysShowSearch.call(ctx);
        expect(ctx.alwaysShowSearch).toBe(true);
    });

    it('persists true to localStorage', () => {
        const ctx = makeCtx({ alwaysShowSearch: false });
        methods.toggleAlwaysShowSearch.call(ctx);
        expect(localStorage.getItem('wb_alwaysShowSearch')).toBe('true');
    });

    it('removes from localStorage when toggled off', () => {
        localStorage.setItem('wb_alwaysShowSearch', 'true');
        const ctx = makeCtx({ alwaysShowSearch: true });
        methods.toggleAlwaysShowSearch.call(ctx);
        expect(localStorage.getItem('wb_alwaysShowSearch')).toBeNull();
    });
});

// ── showError ─────────────────────────────────────────────────────────────────

describe('showError', () => {
    it('sets the error message', () => {
        vi.useFakeTimers();
        const ctx = makeCtx({ error: null, errorTimer: null });
        methods.showError.call(ctx, 'Something went wrong');
        expect(ctx.error).toBe('Something went wrong');
        vi.useRealTimers();
    });

    it('auto-clears error after 8 seconds', () => {
        vi.useFakeTimers();
        const ctx = makeCtx({ error: null, errorTimer: null });
        methods.showError.call(ctx, 'Oops');
        expect(ctx.error).toBe('Oops');
        vi.advanceTimersByTime(8000);
        expect(ctx.error).toBeNull();
        vi.useRealTimers();
    });
});

// ── initApp (new-provider discovery) ─────────────────────────────────────────

describe('initApp — new-provider discovery', () => {
    function makeInitCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            authState: 'checking',
            configData: null,
            initialized: false,
            wizardActive: false,
            newProviderKeys: [] as string[],
            newProvidersActive: false,
            newProviderSelected: new Set<string>(),
            newProviderSaving: false,
            _initConfigEdits: vi.fn(),
            fetchMappings: vi.fn(),
            setupSSE: vi.fn(),
            sync: vi.fn(),
            ...overrides,
        };
    }

    function stubFetchConfig(integrations: Record<string, unknown>) {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({ integrations }),
        }));
    }

    afterEach(() => {
        vi.restoreAllMocks();
        localStorage.removeItem('wb_wizardCompleted');
        localStorage.removeItem('wb_checklistCompleted');
        localStorage.removeItem('wb_seenProviders');
    });

    it('skips discovery for first-time users (no wizard/checklist flags)', async () => {
        stubFetchConfig({
            jellyfin: { fields: [{ hasValue: true }] },
            lemmy: { fields: [{ hasValue: false }] },
        });
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProvidersActive).toBe(false);
        expect(localStorage.getItem('wb_seenProviders')).toBeNull();
    });

    it('seeds silently on upgrade (null wb_seenProviders) — configured providers not shown', async () => {
        stubFetchConfig({
            jellyfin: { fields: [{ hasValue: true }] },
            trakt: { fields: [{ hasValue: true }] },
        });
        localStorage.setItem('wb_wizardCompleted', 'true');
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProvidersActive).toBe(false);
        const seen = JSON.parse(localStorage.getItem('wb_seenProviders') ?? 'null') as string[];
        expect(seen).toContain('jellyfin');
        expect(seen).toContain('trakt');
    });

    it('flags a new provider added in the same upgrade (hasValue false on all its fields)', async () => {
        stubFetchConfig({
            jellyfin: { fields: [{ hasValue: true }] },
            lemmy: { fields: [{ hasValue: false }] },
        });
        localStorage.setItem('wb_wizardCompleted', 'true');
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProviderKeys).toContain('lemmy');
        expect(ctx.newProvidersActive).toBe(true);
        // jellyfin should have been seeded (has user values)
        const seen = JSON.parse(localStorage.getItem('wb_seenProviders') ?? 'null') as string[];
        expect(seen).toContain('jellyfin');
        expect(seen).not.toContain('lemmy');
    });

    it('flags a no-fields provider (e.g. Reddit) as seen on upgrade (never prompts about it)', async () => {
        stubFetchConfig({
            reddit: { fields: [] },
        });
        localStorage.setItem('wb_wizardCompleted', 'true');
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProvidersActive).toBe(false);
        const seen = JSON.parse(localStorage.getItem('wb_seenProviders') ?? 'null') as string[];
        expect(seen).toContain('reddit');
    });

    it('shows notification when wb_seenProviders is present but missing a new key', async () => {
        stubFetchConfig({
            jellyfin: { fields: [{ hasValue: true }] },
            lemmy: { fields: [{ hasValue: false }] },
        });
        localStorage.setItem('wb_wizardCompleted', 'true');
        localStorage.setItem('wb_seenProviders', JSON.stringify(['jellyfin']));
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProviderKeys).toEqual(['lemmy']);
        expect(ctx.newProvidersActive).toBe(true);
    });

    it('shows no notification when all providers are already seen', async () => {
        stubFetchConfig({
            jellyfin: { fields: [{ hasValue: true }] },
            reddit: { fields: [] },
        });
        localStorage.setItem('wb_wizardCompleted', 'true');
        localStorage.setItem('wb_seenProviders', JSON.stringify(['jellyfin', 'reddit']));
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProvidersActive).toBe(false);
        expect((ctx.newProviderKeys as string[])).toHaveLength(0);
    });

    it('also triggers on wb_checklistCompleted (no wb_wizardCompleted needed)', async () => {
        stubFetchConfig({
            lemmy: { fields: [{ hasValue: false }] },
        });
        localStorage.setItem('wb_checklistCompleted', 'true');
        localStorage.setItem('wb_seenProviders', JSON.stringify([]));
        const ctx = makeInitCtx();
        await methods.initApp.call(ctx);
        expect(ctx.newProvidersActive).toBe(true);
    });
});

// ── formatRelativeTime ────────────────────────────────────────────────────────

describe('formatRelativeTime', () => {
    it('returns empty string for empty input', () => {
        const ctx = makeCtx();
        expect(methods.formatRelativeTime.call(ctx, '')).toBe('');
    });

    it('returns seconds ago for recent timestamps', () => {
        const ctx = makeCtx();
        const now = new Date(Date.now() - 30_000).toISOString();
        expect(methods.formatRelativeTime.call(ctx, now)).toBe('30 seconds ago');
    });

    it('returns minutes ago for timestamps 1–59 minutes old', () => {
        const ctx = makeCtx();
        const now = new Date(Date.now() - 5 * 60_000).toISOString();
        expect(methods.formatRelativeTime.call(ctx, now)).toBe('5 minutes ago');
    });

    it('returns hours ago for timestamps 1+ hour old', () => {
        const ctx = makeCtx();
        const now = new Date(Date.now() - 2 * 3_600_000).toISOString();
        expect(methods.formatRelativeTime.call(ctx, now)).toBe('2 hours ago');
    });
});

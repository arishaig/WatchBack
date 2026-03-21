import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import configMethods from '../../src/modules/config';

const methods = configMethods as Record<string, Function>;

function makeCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        configData: null,
        configEdits: {} as Record<string, string>,
        prefEdits: {
            timeMachineDays: 14,
            watchProvider: 'jellyfin',
            searchEngine: 'google',
            customSearchUrl: '',
            segmentedProgressBar: false,
        },
        saveStatus: {} as Record<string, string>,
        saveAllStatus: null,
        prefSaveStatus: null,
        testResults: {} as Record<string, unknown>,
        lastTestResults: {} as Record<string, unknown>,
        testAllStatus: null,
        t: (key: string) => key,
        testService: vi.fn(),
        ...overrides,
    };
}

// ── _initConfigEdits ──────────────────────────────────────────────────────────

describe('_initConfigEdits', () => {
    it('sets configEdits from integration fields', () => {
        const ctx = makeCtx();
        const configData = {
            integrations: {
                jellyfin: {
                    fields: [
                        { key: 'Jellyfin__Url', type: 'text', value: 'http://localhost:8096' },
                        { key: 'Jellyfin__ApiKey', type: 'password', value: 'secret' },
                    ],
                },
            },
            preferences: {},
        };
        methods._initConfigEdits.call(ctx, configData);
        expect((ctx.configEdits as Record<string, string>)['Jellyfin__Url']).toBe('http://localhost:8096');
        // Password fields initialise to empty string
        expect((ctx.configEdits as Record<string, string>)['Jellyfin__ApiKey']).toBe('');
    });

    it('uses defaults for missing preference values', () => {
        const ctx = makeCtx();
        methods._initConfigEdits.call(ctx, { integrations: {}, preferences: {} });
        const prefs = ctx.prefEdits as Record<string, unknown>;
        expect(prefs.timeMachineDays).toBe(14);
        expect(prefs.watchProvider).toBe('jellyfin');
        expect(prefs.searchEngine).toBe('google');
        expect(prefs.customSearchUrl).toBe('');
        expect(prefs.segmentedProgressBar).toBe(false);
    });

    it('loads preference values from configData', () => {
        const ctx = makeCtx();
        methods._initConfigEdits.call(ctx, {
            integrations: {},
            preferences: { timeMachineDays: 30, watchProvider: 'plex', segmentedProgressBar: true },
        });
        const prefs = ctx.prefEdits as Record<string, unknown>;
        expect(prefs.timeMachineDays).toBe(30);
        expect(prefs.watchProvider).toBe('plex');
        expect(prefs.segmentedProgressBar).toBe(true);
    });
});

// ── redditSearchUrl ───────────────────────────────────────────────────────────

describe('redditSearchUrl', () => {
    it('formats episode query correctly (S01E05)', () => {
        const ctx = makeCtx();
        const url = methods.redditSearchUrl.call(ctx, 'The Wire', 1, 5) as string;
        // encodeURIComponent uses %20 for spaces
        expect(url).toContain('The%20Wire%20S01E05%20reddit');
    });

    it('zero-pads single-digit season and episode', () => {
        const ctx = makeCtx();
        const url = methods.redditSearchUrl.call(ctx, 'Show', 3, 7) as string;
        expect(url).toContain('S03E07');
    });

    it('uses google as default base URL', () => {
        const ctx = makeCtx();
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://www.google.com/search?q=');
    });

    it('uses duckduckgo when searchEngine is "duckduckgo"', () => {
        const ctx = makeCtx({ prefEdits: { searchEngine: 'duckduckgo', customSearchUrl: '' } });
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://duckduckgo.com/?q=');
    });

    it('uses bing when searchEngine is "bing"', () => {
        const ctx = makeCtx({ prefEdits: { searchEngine: 'bing', customSearchUrl: '' } });
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://www.bing.com/search?q=');
    });

    it('uses custom URL when searchEngine is "custom"', () => {
        const ctx = makeCtx({
            prefEdits: { searchEngine: 'custom', customSearchUrl: 'https://custom.search/?q=' },
        });
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://custom.search/?q=');
    });

    it('falls back to google when custom URL is empty', () => {
        const ctx = makeCtx({ prefEdits: { searchEngine: 'custom', customSearchUrl: '' } });
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://www.google.com/search?q=');
    });

    it('falls back to google for unknown engine', () => {
        const ctx = makeCtx({ prefEdits: { searchEngine: 'yahoo', customSearchUrl: '' } });
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://www.google.com/search?q=');
    });

    it('prefers prefEdits engine over configData preferences', () => {
        const ctx = makeCtx({
            prefEdits: { searchEngine: 'bing', customSearchUrl: '' },
            configData: { preferences: { searchEngine: 'duckduckgo' } },
        });
        const url = methods.redditSearchUrl.call(ctx, 'Show', 1, 1) as string;
        expect(url).toContain('https://www.bing.com/search?q=');
    });
});

// ── testIcon ──────────────────────────────────────────────────────────────────

describe('testIcon', () => {
    it('returns not-configured icon when no test results and not configured', () => {
        const ctx = makeCtx({
            lastTestResults: {},
            configData: { integrations: { jellyfin: { configured: false } } },
        });
        const icon = methods.testIcon.call(ctx, 'jellyfin') as { icon: string; cls: string };
        expect(icon.cls).toBe('wb-text-faint');
    });

    it('returns configured icon when no test results but service is configured', () => {
        const ctx = makeCtx({
            lastTestResults: {},
            configData: { integrations: { jellyfin: { configured: true } } },
        });
        const icon = methods.testIcon.call(ctx, 'jellyfin') as { icon: string; cls: string };
        expect(icon.cls).toBe('wb-accent-text');
    });

    it('returns success icon when last test result is ok', () => {
        const ctx = makeCtx({
            lastTestResults: { jellyfin: { status: 'ok' } },
            configData: { integrations: {} },
        });
        const icon = methods.testIcon.call(ctx, 'jellyfin') as { icon: string; cls: string };
        expect(icon.cls).toBe('wb-success-text');
    });

    it('returns error icon when last test result is error', () => {
        const ctx = makeCtx({
            lastTestResults: { jellyfin: { status: 'error' } },
            configData: { integrations: {} },
        });
        const icon = methods.testIcon.call(ctx, 'jellyfin') as { icon: string; cls: string };
        expect(icon.cls).toBe('wb-error-text');
    });
});

// ── saveConfig ────────────────────────────────────────────────────────────────

describe('saveConfig', () => {
    beforeEach(() => { vi.useFakeTimers(); });
    afterEach(() => { vi.useRealTimers(); });

    it('does nothing when integration key not found', async () => {
        const ctx = makeCtx({ configData: { integrations: {} } });
        await methods.saveConfig.call(ctx, 'unknown');
        expect(ctx.saveStatus).toEqual({});
    });

    it('POSTs to /api/config and sets saved status on success', async () => {
        const fetchMock = vi.fn()
            .mockResolvedValueOnce({ ok: true, json: async () => ({}) })   // POST
            .mockResolvedValueOnce({ ok: true, json: async () => ({ integrations: { jellyfin: { fields: [] } } }) }); // GET
        vi.stubGlobal('fetch', fetchMock);

        const ctx = makeCtx({
            configData: {
                integrations: {
                    jellyfin: {
                        fields: [{ key: 'Jellyfin__Url', type: 'text' }],
                    },
                },
            },
            configEdits: { 'Jellyfin__Url': 'http://localhost' },
        });

        await methods.saveConfig.call(ctx, 'jellyfin');

        expect((ctx.saveStatus as Record<string, string>).jellyfin).toBe('saved');
        const [url, opts] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(url).toBe('/api/config');
        expect(opts.method).toBe('POST');
        expect(JSON.parse(opts.body as string)).toEqual({ 'Jellyfin__Url': 'http://localhost' });
    });

    it('sets error status on failed POST', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
        const ctx = makeCtx({
            configData: {
                integrations: {
                    jellyfin: { fields: [{ key: 'Jellyfin__Url', type: 'text' }] },
                },
            },
            configEdits: { 'Jellyfin__Url': 'http://localhost' },
        });
        await methods.saveConfig.call(ctx, 'jellyfin');
        expect((ctx.saveStatus as Record<string, string>).jellyfin).toBe('error');
    });

    it('omits empty password fields from POST payload', async () => {
        let capturedBody: Record<string, string> = {};
        vi.stubGlobal('fetch', vi.fn()
            .mockImplementationOnce((_url: string, opts: RequestInit) => {
                capturedBody = JSON.parse(opts.body as string);
                return Promise.resolve({ ok: true, json: async () => ({}) });
            })
            .mockResolvedValue({ ok: true, json: async () => ({ integrations: { svc: { fields: [] } } }) }));

        const ctx = makeCtx({
            configData: {
                integrations: {
                    svc: {
                        fields: [
                            { key: 'Svc__Url', type: 'text' },
                            { key: 'Svc__Pass', type: 'password' },
                        ],
                    },
                },
            },
            configEdits: { 'Svc__Url': 'http://x', 'Svc__Pass': '' },
        });
        await methods.saveConfig.call(ctx, 'svc');
        expect(capturedBody).toHaveProperty('Svc__Url');
        expect(capturedBody).not.toHaveProperty('Svc__Pass');
    });

    it('includes non-empty password fields in POST payload', async () => {
        let capturedBody: Record<string, string> = {};
        vi.stubGlobal('fetch', vi.fn()
            .mockImplementationOnce((_url: string, opts: RequestInit) => {
                capturedBody = JSON.parse(opts.body as string);
                return Promise.resolve({ ok: true, json: async () => ({}) });
            })
            .mockResolvedValue({ ok: true, json: async () => ({ integrations: { svc: { fields: [] } } }) }));

        const ctx = makeCtx({
            configData: {
                integrations: {
                    svc: { fields: [{ key: 'Svc__Pass', type: 'password' }] },
                },
            },
            configEdits: { 'Svc__Pass': 'mySecret' },
        });
        await methods.saveConfig.call(ctx, 'svc');
        expect(capturedBody['Svc__Pass']).toBe('mySecret');
    });

    it('clears saveStatus after 3 seconds', async () => {
        vi.stubGlobal('fetch', vi.fn()
            .mockResolvedValueOnce({ ok: true, json: async () => ({}) })
            .mockResolvedValue({ ok: true, json: async () => ({ integrations: { svc: { fields: [] } } }) }));

        const ctx = makeCtx({
            configData: { integrations: { svc: { fields: [{ key: 'k', type: 'text' }] } } },
            configEdits: { k: 'v' },
        });
        await methods.saveConfig.call(ctx, 'svc');
        expect((ctx.saveStatus as Record<string, string>).svc).toBe('saved');
        vi.advanceTimersByTime(3000);
        expect((ctx.saveStatus as Record<string, string>).svc).toBeUndefined();
    });
});

// ── savePreferences ───────────────────────────────────────────────────────────

describe('savePreferences', () => {
    beforeEach(() => { vi.useFakeTimers(); });
    afterEach(() => { vi.useRealTimers(); });

    it('POSTs WatchBack__ prefixed keys', async () => {
        let capturedBody: Record<string, string> = {};
        vi.stubGlobal('fetch', vi.fn()
            .mockImplementationOnce((_url: string, opts: RequestInit) => {
                capturedBody = JSON.parse(opts.body as string);
                return Promise.resolve({ ok: true, json: async () => ({}) });
            })
            .mockResolvedValue({ ok: true, json: async () => ({}) }));

        const ctx = makeCtx({
            prefEdits: {
                timeMachineDays: 30,
                watchProvider: 'plex',
                searchEngine: 'bing',
                customSearchUrl: '',
                segmentedProgressBar: true,
            },
        });
        await methods.savePreferences.call(ctx);
        expect(capturedBody['WatchBack__TimeMachineDays']).toBe('30');
        expect(capturedBody['WatchBack__WatchProvider']).toBe('plex');
        expect(capturedBody['WatchBack__SearchEngine']).toBe('bing');
        expect(capturedBody['WatchBack__SegmentedProgressBar']).toBe('true');
    });

    it('sets prefSaveStatus to "saved" on success', async () => {
        vi.stubGlobal('fetch', vi.fn()
            .mockResolvedValueOnce({ ok: true, json: async () => ({}) })
            .mockResolvedValue({ ok: true, json: async () => ({}) }));
        const ctx = makeCtx();
        await methods.savePreferences.call(ctx);
        expect(ctx.prefSaveStatus).toBe('saved');
    });

    it('sets prefSaveStatus to "error" on failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
        const ctx = makeCtx();
        await methods.savePreferences.call(ctx);
        expect(ctx.prefSaveStatus).toBe('error');
    });
});

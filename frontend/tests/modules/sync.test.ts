import { describe, it, expect, vi, afterEach } from 'vitest';
import syncMethods from '../../src/modules/sync';

const methods = syncMethods as Record<string, Function>;

function makeCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        searchQuery: '',
        searchLoading: false,
        searchError: null,
        searchResults: [] as unknown[],
        searchDrilldown: null,
        drilldownSeason: null,
        drilldownEpisodes: [] as unknown[],
        drilldownLoading: false,
        data: null,
        t: (key: string) => key,
        showError: vi.fn(),
        sync: vi.fn(),
        setManualWatchState: vi.fn(),
        ...overrides,
    };
}

// ── sync ──────────────────────────────────────────────────────────────────────

describe('sync', () => {
    it('POSTs to /api/sync/trigger', async () => {
        const fetchMock = vi.fn().mockResolvedValue({});
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx();
        await methods.sync.call(ctx);
        expect(fetchMock).toHaveBeenCalledWith('/api/sync/trigger', { method: 'POST' });
    });

    it('calls showError on network failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Network')));
        const ctx = makeCtx();
        await methods.sync.call(ctx);
        expect((ctx.showError as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
    });
});

// ── searchMedia ───────────────────────────────────────────────────────────────

describe('searchMedia', () => {
    it('does nothing when searchQuery is empty', async () => {
        const fetchMock = vi.fn();
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({ searchQuery: '   ' });
        await methods.searchMedia.call(ctx);
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('sets searchResults on success', async () => {
        const results = [{ id: 1, title: 'Movie' }];
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            ok: true,
            json: async () => results,
        }));
        const ctx = makeCtx({ searchQuery: 'Movie' });
        await methods.searchMedia.call(ctx);
        expect(ctx.searchResults).toEqual(results);
        expect(ctx.searchLoading).toBe(false);
    });

    it('sets searchError for 503 (OMDB not configured)', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 503 }));
        const ctx = makeCtx({ searchQuery: 'Movie' });
        await methods.searchMedia.call(ctx);
        expect(ctx.searchError).toBe('Dashboard_OmdbNotConfigured');
    });

    it('sets searchError for other server errors', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 500 }));
        const ctx = makeCtx({ searchQuery: 'Movie' });
        await methods.searchMedia.call(ctx);
        expect(ctx.searchError).toBe('Dashboard_SearchFailed');
    });

    it('sets searchError on network failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error()));
        const ctx = makeCtx({ searchQuery: 'Movie' });
        await methods.searchMedia.call(ctx);
        expect(ctx.searchError).toBe('Dashboard_ConnectionFailed');
    });

    it('clears previous results and drilldown state before searching', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => [] }));
        const ctx = makeCtx({
            searchQuery: 'New',
            searchResults: [{ id: 99 }],
            searchDrilldown: { imdbId: 'tt123' },
            drilldownSeason: { seasonNumber: 1 },
            drilldownEpisodes: [{ id: 5 }],
            searchError: 'old error',
        });
        await methods.searchMedia.call(ctx);
        expect(ctx.searchResults).toEqual([]);
        expect(ctx.searchDrilldown).toBeNull();
        expect(ctx.drilldownSeason).toBeNull();
        expect(ctx.drilldownEpisodes).toEqual([]);
    });

    it('encodes the query string', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => [] });
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({ searchQuery: 'The Wire & Co' });
        await methods.searchMedia.call(ctx);
        const url = fetchMock.mock.calls[0][0] as string;
        expect(url).toContain(encodeURIComponent('The Wire & Co'));
    });
});

// ── selectSearchResult ────────────────────────────────────────────────────────

describe('selectSearchResult', () => {
    it('calls setManualWatchState for episode result', async () => {
        const ctx = makeCtx();
        const result = {
            type: 'episode',
            title: 'The Wire — All Prologue (S02E01)',
            releaseDate: '2003-06-01T00:00:00Z',
            imdbId: 'tt0306069',
        };
        await methods.selectSearchResult.call(ctx, result);
        expect((ctx.setManualWatchState as ReturnType<typeof vi.fn>)).toHaveBeenCalledWith({
            title: 'The Wire',
            episodeTitle: 'All Prologue',
            seasonNumber: 2,
            episodeNumber: 1,
            releaseDate: '2003-06-01T00:00:00Z',
            externalIds: { imdb: 'tt0306069' },
        });
    });

    it('calls setManualWatchState for movie result', async () => {
        const ctx = makeCtx();
        const result = {
            type: 'movie',
            title: 'Inception',
            releaseDate: '2010-07-16T00:00:00Z',
            imdbId: 'tt1375666',
        };
        await methods.selectSearchResult.call(ctx, result);
        expect((ctx.setManualWatchState as ReturnType<typeof vi.fn>)).toHaveBeenCalledWith({
            title: 'Inception',
            releaseDate: '2010-07-16T00:00:00Z',
            externalIds: { imdb: 'tt1375666' },
        });
    });

    it('constructs year-based releaseDate for movie without releaseDate', async () => {
        const ctx = makeCtx();
        await methods.selectSearchResult.call(ctx, {
            type: 'movie',
            title: 'Oldfilm',
            year: '1985',
            imdbId: 'tt0000001',
        });
        const call = (ctx.setManualWatchState as ReturnType<typeof vi.fn>).mock.calls[0][0];
        expect(call.releaseDate).toBe('1985-01-01T00:00:00Z');
    });

    it('sets up searchDrilldown for show result', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => [{ seasonNumber: 1 }] }));
        const ctx = makeCtx();
        await methods.selectSearchResult.call(ctx, {
            type: 'show',
            title: 'The Wire',
            imdbId: 'tt0306069',
            posterUrl: 'http://img',
        });
        expect((ctx.searchDrilldown as Record<string, unknown>)?.title).toBe('The Wire');
        expect((ctx.searchDrilldown as Record<string, unknown>)?.imdbId).toBe('tt0306069');
        expect(Array.isArray((ctx.searchDrilldown as Record<string, unknown>)?.seasons)).toBe(true);
    });
});

// ── setManualWatchState ───────────────────────────────────────────────────────

describe('setManualWatchState', () => {
    afterEach(() => { vi.restoreAllMocks(); });

    it('updates data and calls sync on success', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true }));
        const ctx = makeCtx();
        await methods.setManualWatchState.call(ctx, {
            title: 'Inception',
            releaseDate: '2010-07-16T00:00:00Z',
        });
        expect(ctx.searchQuery).toBe('');
        expect(ctx.searchResults).toEqual([]);
        expect((ctx.data as Record<string, unknown>)?.status).toBe('Watching');
        expect((ctx.data as Record<string, unknown>)?.title).toBe('Inception');
        expect((ctx.sync as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
    });

    it('sets searchError on failed response', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
        const ctx = makeCtx();
        await methods.setManualWatchState.call(ctx, { title: 'Movie' });
        expect(ctx.searchError).toBe('Dashboard_SetWatchStateFailed');
    });

    it('sets searchError on network failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error()));
        const ctx = makeCtx();
        await methods.setManualWatchState.call(ctx, { title: 'Movie' });
        expect(ctx.searchError).toBe('Dashboard_ConnectionFailed');
    });

    it('preserves existing timeMachineDays in new data object', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true }));
        const ctx = makeCtx({ data: { timeMachineDays: 30 } });
        await methods.setManualWatchState.call(ctx, { title: 'Show' });
        expect((ctx.data as Record<string, unknown>)?.timeMachineDays).toBe(30);
    });

    it('POSTs all context fields to /api/watchstate/manual', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ ok: true });
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx();
        await methods.setManualWatchState.call(ctx, {
            title: 'Show',
            episodeTitle: 'Ep 1',
            seasonNumber: 1,
            episodeNumber: 5,
            releaseDate: '2024-01-01T00:00:00Z',
            externalIds: { imdb: 'tt123' },
        });
        const [url, opts] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(url).toBe('/api/watchstate/manual');
        expect(opts.method).toBe('POST');
        const body = JSON.parse(opts.body as string);
        expect(body.title).toBe('Show');
        expect(body.episodeTitle).toBe('Ep 1');
        expect(body.seasonNumber).toBe(1);
        expect(body.episodeNumber).toBe(5);
    });
});

// ── clearManualWatchState ─────────────────────────────────────────────────────

describe('clearManualWatchState', () => {
    it('DELETEs /api/watchstate/manual and calls sync', async () => {
        const fetchMock = vi.fn().mockResolvedValue({});
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx();
        await methods.clearManualWatchState.call(ctx);
        const [url, opts] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(url).toBe('/api/watchstate/manual');
        expect(opts.method).toBe('DELETE');
        expect((ctx.sync as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
    });

    it('calls showError on network failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error()));
        const ctx = makeCtx();
        await methods.clearManualWatchState.call(ctx);
        expect((ctx.showError as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
    });
});

// ── selectSeason ──────────────────────────────────────────────────────────────

describe('selectSeason', () => {
    it('fetches episodes and stores them', async () => {
        const eps = [{ episodeNumber: 1 }, { episodeNumber: 2 }];
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => eps }));
        const ctx = makeCtx({ searchDrilldown: { imdbId: 'tt123' } });
        await methods.selectSeason.call(ctx, { seasonNumber: 2 });
        expect(ctx.drilldownSeason).toEqual({ seasonNumber: 2 });
        expect(ctx.drilldownEpisodes).toEqual(eps);
        expect(ctx.drilldownLoading).toBe(false);
    });

    it('sets searchError on network failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error()));
        const ctx = makeCtx({ searchDrilldown: { imdbId: 'tt123' } });
        await methods.selectSeason.call(ctx, { seasonNumber: 1 });
        expect(ctx.searchError).toBe('Dashboard_LoadEpisodesFailed');
        expect(ctx.drilldownLoading).toBe(false);
    });
});

// ── selectEpisode ─────────────────────────────────────────────────────────────

describe('selectEpisode', () => {
    it('calls setManualWatchState with episode context', async () => {
        const ctx = makeCtx({ searchDrilldown: { title: 'The Wire', imdbId: 'tt0306069' } });
        await methods.selectEpisode.call(ctx, {
            title: 'All Prologue',
            seasonNumber: 2,
            episodeNumber: 1,
            airDate: '2003-06-01T00:00:00Z',
            imdbId: 'tt0517546',
        });
        const call = (ctx.setManualWatchState as ReturnType<typeof vi.fn>).mock.calls[0][0];
        expect(call.title).toBe('The Wire');
        expect(call.episodeTitle).toBe('All Prologue');
        expect(call.seasonNumber).toBe(2);
        expect(call.episodeNumber).toBe(1);
        expect(call.externalIds).toEqual({ imdb: 'tt0306069', imdbEpisode: 'tt0517546' });
    });

    it('omits imdbEpisode when episode has no imdbId', async () => {
        const ctx = makeCtx({ searchDrilldown: { title: 'Show', imdbId: 'tt0001' } });
        await methods.selectEpisode.call(ctx, {
            title: 'Ep',
            seasonNumber: 1,
            episodeNumber: 1,
            airDate: '2024-01-01T00:00:00Z',
        });
        const call = (ctx.setManualWatchState as ReturnType<typeof vi.fn>).mock.calls[0][0];
        expect(call.externalIds).toEqual({ imdb: 'tt0001' });
    });
});

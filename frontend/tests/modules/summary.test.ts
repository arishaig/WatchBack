import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import summaryMethods from '../../src/modules/summary';

type Ctx = Record<string, any>;

const t = (key: string, ...args: unknown[]) => (args.length ? `${key}:${args.join(',')}` : key);

function makeCtx(overrides: Ctx = {}, prefs: Ctx = { summaryMode: 'collapsed' }): Ctx {
    const ctx: Ctx = {
        t,
        configData: { preferences: { ...prefs }, integrations: { ollama: { configured: true } } },
        prefEdits: {},
        fieldStatus: {},
        mode: 'all',
        data: { status: 'Watching', title: 'Show', allThoughts: [{ id: 'a' }, { id: 'b' }], timeMachineThoughts: [{ id: 'a' }] },
        summary: null,
        summaryExpanded: false,
        _summaryKey: '',
        _summaryAt: 0,
        _summaryTicket: 0,
        _summaryTimer: null,
        viewerQuery: () => '',
        setPreference: vi.fn().mockResolvedValue(true),
        ...overrides,
    };
    for (const [name, fn] of Object.entries(summaryMethods)) {
        if (typeof fn === 'function') ctx[name] = (fn as Function).bind(ctx);
    }
    return ctx;
}

function respond(body: unknown, ok = true) {
    const fetchMock = vi.fn().mockResolvedValue({ ok, status: ok ? 200 : 500, json: async () => body });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

describe('summaryMode / summaryShown', () => {
    it('reads the mode the server resolved, treating anything else as hidden', () => {
        expect(makeCtx({}, { summaryMode: 'open' }).summaryMode()).toBe('open');
        expect(makeCtx({}, { summaryMode: 'collapsed' }).summaryMode()).toBe('collapsed');
        expect(makeCtx({}, { summaryMode: 'hidden' }).summaryMode()).toBe('hidden');
        expect(makeCtx({}, {}).summaryMode()).toBe('hidden');
    });

    it('shows the block only for a ready, pending, failed or unavailable summary while watching', () => {
        for (const status of ['ready', 'pending', 'failed', 'unavailable']) {
            expect(makeCtx({ summary: { status, text: null, commentCount: 0 } }).summaryShown()).toBe(true);
        }
        expect(makeCtx({ summary: { status: 'empty', text: null, commentCount: 0 } }).summaryShown()).toBe(false);
        expect(makeCtx({ summary: { status: 'hidden', text: null, commentCount: 0 } }).summaryShown()).toBe(false);
        expect(makeCtx({ summary: null }).summaryShown()).toBe(false);
    });

    it('never shows when hidden or when nothing is playing', () => {
        const summary = { status: 'ready', text: 'x', commentCount: 1 };
        expect(makeCtx({ summary }, { summaryMode: 'hidden' }).summaryShown()).toBe(false);
        expect(makeCtx({ summary, data: { status: 'Idle' } }).summaryShown()).toBe(false);
        expect(makeCtx({ summary, data: null }).summaryShown()).toBe(false);
    });
});

describe('summaryText / summaryHint', () => {
    it('says what is going on in each state', () => {
        const text = (status: string, extra: Ctx = {}) =>
            makeCtx({ summary: { status, text: null, commentCount: 0, ...extra } }).summaryText();
        expect(text('ready', { text: 'Loved it.' })).toBe('Loved it.');
        expect(text('pending')).toBe('Summary_Writing');
        expect(text('failed')).toBe('Summary_Failed');
        expect(text('unavailable')).toBe('Summary_NeedsModel');
        expect(makeCtx().summaryText()).toBe('');
    });

    it('hints at progress or the basis while folded', () => {
        expect(makeCtx({ summary: { status: 'pending', text: null, commentCount: 4 } }).summaryHint()).toBe('Summary_Writing');
        expect(makeCtx({ summary: { status: 'ready', text: 'x', commentCount: 4 } }).summaryHint()).toBe('Summary_Basis:4');
        expect(makeCtx({ summary: { status: 'ready', text: 'x', commentCount: 9, compared: true } }).summaryHint()).toBe('Summary_BasisCompared:9');
        expect(makeCtx({ summary: { status: 'failed', text: null, commentCount: 4 } }).summaryHint()).toBe('');
    });
});

describe('refreshSummary', () => {
    it('does not ask while hidden, and clears what it had', async () => {
        const fetchMock = respond({});
        const ctx = makeCtx({ summary: { status: 'ready', text: 'x', commentCount: 1 } }, { summaryMode: 'hidden' });

        await ctx.refreshSummary();

        expect(fetchMock).not.toHaveBeenCalled();
        expect(ctx.summary).toBeNull();
    });

    it('asks for the all-thoughts summary, then the time-machine one when that view is chosen', async () => {
        const fetchMock = respond({ status: 'ready', text: 'x', commentCount: 2 });
        const ctx = makeCtx({ viewerQuery: () => '?userId=u' });

        await ctx.refreshSummary();
        ctx.mode = 'time';
        await ctx.refreshSummary();

        expect(fetchMock.mock.calls[0][0]).toBe('/api/summary?userId=u&timeMachine=false');
        expect(fetchMock.mock.calls[1][0]).toBe('/api/summary?userId=u&timeMachine=true');
        expect(ctx.summary.text).toBe('x');
    });

    it('does not ask again on a poll with nothing new, but does when a comment arrives', async () => {
        const fetchMock = respond({ status: 'ready', text: 'x', commentCount: 2 });
        const ctx = makeCtx();

        await ctx.refreshSummary();
        await ctx.refreshSummary();
        expect(fetchMock).toHaveBeenCalledTimes(1);

        ctx.data = { ...ctx.data, allThoughts: [{ id: 'a' }, { id: 'b' }, { id: 'c' }] };
        await ctx.refreshSummary();
        expect(fetchMock).toHaveBeenCalledTimes(2);
    });

    it('keeps asking every few seconds while the server is still writing it', async () => {
        const fetchMock = respond({ status: 'pending', text: null, commentCount: 2 });
        const ctx = makeCtx();

        await ctx.refreshSummary();
        expect(ctx.summary.status).toBe('pending');
        fetchMock.mockResolvedValue({ ok: true, json: async () => ({ status: 'ready', text: 'Done.', commentCount: 2 }) });
        await vi.advanceTimersByTimeAsync(3100);

        expect(fetchMock).toHaveBeenCalledTimes(2);
        expect(ctx.summary).toMatchObject({ status: 'ready', text: 'Done.' });
    });

    it('waits out the server\'s pause after a failure before asking again, unless told to retry', async () => {
        const fetchMock = respond({ status: 'failed', text: null, commentCount: 2 });
        const ctx = makeCtx();

        await ctx.refreshSummary();
        await ctx.refreshSummary();
        expect(fetchMock).toHaveBeenCalledTimes(1);

        await ctx.refreshSummary(true);
        expect(fetchMock).toHaveBeenCalledTimes(2);

        vi.advanceTimersByTime(31000);
        await ctx.refreshSummary();
        expect(fetchMock).toHaveBeenCalledTimes(3);
    });

    it('reports a failed summary when the request itself fails', async () => {
        respond({}, false);
        const ctx = makeCtx();

        await ctx.refreshSummary();

        expect(ctx.summary.status).toBe('failed');
    });

    it('ignores an answer that arrives after a newer question was asked', async () => {
        let release!: (v: unknown) => void;
        const slow = new Promise(resolve => (release = resolve));
        const fetchMock = vi.fn()
            .mockReturnValueOnce(slow)
            .mockResolvedValueOnce({ ok: true, json: async () => ({ status: 'ready', text: 'new', commentCount: 1 }) });
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx();

        const first = ctx.refreshSummary();
        ctx.mode = 'time';
        await ctx.refreshSummary();
        release({ ok: true, json: async () => ({ status: 'ready', text: 'old', commentCount: 1 }) });
        await first;

        expect(ctx.summary.text).toBe('new');
    });
});

describe('setSummaryMode', () => {
    it('saves the mode, opens or folds to match, and fetches when it is now wanted', async () => {
        const fetchMock = respond({ status: 'ready', text: 'x', commentCount: 1 });
        const ctx = makeCtx({}, { summaryMode: 'hidden' });

        await ctx.setSummaryMode('open');

        expect(ctx.setPreference).toHaveBeenCalledWith('WatchBack__SummaryMode', 'open');
        expect(ctx.configData.preferences.summaryMode).toBe('open');
        expect(ctx.summaryExpanded).toBe(true);
        expect(fetchMock).toHaveBeenCalledTimes(1);

        await ctx.setSummaryMode('collapsed');
        expect(ctx.summaryExpanded).toBe(false);
    });

    it('hides and stops asking when set to hidden', async () => {
        const fetchMock = respond({});
        const ctx = makeCtx({ summary: { status: 'ready', text: 'x', commentCount: 1 } });

        await ctx.setSummaryMode('hidden');

        expect(ctx.summary).toBeNull();
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('reverts the choice when the save fails', async () => {
        const fetchMock = respond({});
        const ctx = makeCtx({ setPreference: vi.fn().mockResolvedValue(false) }, { summaryMode: 'collapsed' });

        await ctx.setSummaryMode('open');

        expect(ctx.configData.preferences.summaryMode).toBe('collapsed');
        expect(ctx.prefEdits.summaryMode).toBe('collapsed');
        expect(fetchMock).not.toHaveBeenCalled();
    });
});

describe('toggleSummary / summaryModelConfigured', () => {
    it('folds and unfolds', () => {
        const ctx = makeCtx();
        ctx.toggleSummary();
        expect(ctx.summaryExpanded).toBe(true);
        ctx.toggleSummary();
        expect(ctx.summaryExpanded).toBe(false);
    });

    it('knows whether Ollama has been set up', () => {
        expect(makeCtx().summaryModelConfigured()).toBe(true);
        const ctx = makeCtx();
        ctx.configData.integrations.ollama.configured = false;
        expect(ctx.summaryModelConfigured()).toBe(false);
    });
});

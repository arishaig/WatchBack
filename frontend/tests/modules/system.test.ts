import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import systemMethods from '../../src/modules/system';

const methods = systemMethods as Record<string, Function>;

// ── setupSSE ───────────────────────────────────────────────────────────────────
// Regression coverage for the sync-bar flake fixes:
//   1. Cached cycles (only 0% and 100% events, no intermediates) never show
//      the bar — even when the cycle takes longer than SHOW_DELAY_MS.
//   2. Cycles with intermediate progress (provider ticks) show the bar after
//      SHOW_DELAY_MS.
//   3. The final 100% progress event updates syncProgress/syncSegments so the
//      bar visually completes instead of freezing at a partial value.
//   4. When a status event reports a new episode but the bar never became
//      visible, the bar pulses briefly so the user sees a reload happened.

interface FakeSSE {
    onmessage: ((e: MessageEvent) => void) | null;
    onerror: (() => void) | null;
}

function installFakeEventSource(): FakeSSE {
    const sse: FakeSSE = { onmessage: null, onerror: null };
    (globalThis as unknown as { window: unknown }).window = (globalThis as unknown as { window: Record<string, unknown> }).window || {};
    (globalThis as unknown as { window: Record<string, unknown> }).window.ReconnectingEventSource = function () {
        return sse;
    } as unknown;
    return sse;
}

function send(sse: FakeSSE, payload: Record<string, unknown>) {
    sse.onmessage?.({ data: JSON.stringify(payload) } as MessageEvent);
}

function makeCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        syncProgress: null,
        showSyncBar: false,
        syncSegments: [],
        data: null,
        ...overrides,
    };
}

describe('setupSSE', () => {
    beforeEach(() => { vi.useFakeTimers(); });
    afterEach(() => { vi.useRealTimers(); });

    it('keeps the bar hidden for a cached cycle regardless of wall time', () => {
        const sse = installFakeEventSource();
        const ctx = makeCtx();
        methods.setupSSE.call(ctx);

        // Cached cycle: only 0% and 100% events, no intermediates.
        send(sse, { completed: 0, total: 10, providers: [] });
        // The bar must stay hidden the whole time — even if the cycle
        // wall-time exceeds SHOW_DELAY_MS — because no provider ticks arrived.
        vi.advanceTimersByTime(2000);
        expect(ctx.showSyncBar).toBe(false);

        send(sse, { completed: 10, total: 10, providers: [] });
        send(sse, { status: 'Watching', title: 'Show', metadata: null });

        vi.advanceTimersByTime(500);
        expect(ctx.showSyncBar).toBe(false);
    });

    it('shows the bar after the debounce when intermediate progress arrives', () => {
        const sse = installFakeEventSource();
        const ctx = makeCtx();
        methods.setupSSE.call(ctx);

        // Intermediate event (0 < completed < total) — providers doing real work.
        send(sse, { completed: 0, total: 10, providers: [] });
        send(sse, { completed: 3, total: 10, providers: [] });
        vi.advanceTimersByTime(300);
        expect(ctx.showSyncBar).toBe(true);
    });

    it('updates syncProgress on the final 100% event', () => {
        const sse = installFakeEventSource();
        const ctx = makeCtx();
        methods.setupSSE.call(ctx);

        send(sse, { completed: 7, total: 10, providers: [{ provider: 'reddit' }] });
        send(sse, { completed: 10, total: 10, providers: [{ provider: 'reddit', completed: 10, total: 10 }] });

        expect(ctx.syncProgress).toEqual({ completed: 10, total: 10 });
        expect((ctx.syncSegments as unknown[]).length).toBe(1);
    });

    it('pulses the bar on episode change even when it was hidden during sync', () => {
        const sse = installFakeEventSource();
        const ctx = makeCtx({
            data: { status: 'Watching', title: 'Show', metadata: { title: 'Show', seasonNumber: 1, episodeNumber: 1 } },
        });
        methods.setupSSE.call(ctx);

        // Fully-cached cycle for the new episode — bar would stay hidden.
        send(sse, { completed: 0, total: 10 });
        send(sse, { completed: 10, total: 10 });
        send(sse, {
            status: 'Watching',
            title: 'Show',
            metadata: { title: 'Show', seasonNumber: 1, episodeNumber: 2 },
        });

        expect(ctx.showSyncBar).toBe(true);
        // After the pulse duration elapses the bar hides again.
        vi.advanceTimersByTime(700);
        expect(ctx.showSyncBar).toBe(false);
        expect(ctx.syncProgress).toBeNull();
    });

    it('does not pulse when the episode is unchanged', () => {
        const sse = installFakeEventSource();
        const ctx = makeCtx({
            data: { status: 'Watching', title: 'Show', metadata: { title: 'Show', seasonNumber: 1, episodeNumber: 1 } },
        });
        methods.setupSSE.call(ctx);

        send(sse, { completed: 0, total: 10 });
        send(sse, { completed: 10, total: 10 });
        send(sse, {
            status: 'Watching',
            title: 'Show',
            metadata: { title: 'Show', seasonNumber: 1, episodeNumber: 1 },
        });

        // Fast cached cycle, no episode change — bar must remain hidden.
        vi.advanceTimersByTime(700);
        expect(ctx.showSyncBar).toBe(false);
    });

    it('arms a fresh show timer for a back-to-back cycle within the hide delay', () => {
        const sse = installFakeEventSource();
        const ctx = makeCtx();
        methods.setupSSE.call(ctx);

        // Cycle 1: fully cached, bar stays hidden, clearTimer armed after status.
        send(sse, { completed: 0, total: 10 });
        send(sse, { completed: 10, total: 10 });
        send(sse, { status: 'Watching', title: 'Show', metadata: null });

        // Cycle 2 starts before HIDE_DELAY_MS elapses (e.g. manual trigger).
        vi.advanceTimersByTime(100);
        send(sse, { completed: 0, total: 10 });
        send(sse, { completed: 4, total: 10 });

        // This cycle reports real intermediate progress — show timer must fire.
        vi.advanceTimersByTime(300);
        expect(ctx.showSyncBar).toBe(true);
    });
});
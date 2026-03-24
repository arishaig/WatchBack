import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import wizardMethods from '../../src/modules/wizard';

const methods = wizardMethods as Record<string, Function>;

function makeCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        wizardStep: 0,
        wizardActive: true,
        wizardSelectedWatch: null,
        wizardSelectedComments: new Set<string>(),
        wizardSaving: false,
        checklistDismissed: false,
        showConfig: false,
        configTab: 'settings',
        collapsedSections: {} as Record<string, boolean>,
        configData: {
            integrations: {
                jellyfin: {
                    name: 'Jellyfin',
                    fields: [{ key: 'Jellyfin__BaseUrl', type: 'text', value: '' }],
                    configured: false,
                    providerTypes: ['watchState'],
                },
                trakt: {
                    name: 'Trakt',
                    fields: [{ key: 'Trakt__ClientId', type: 'text', value: '' }],
                    configured: false,
                    providerTypes: ['watchState', 'thought'],
                },
                reddit: {
                    name: 'Reddit',
                    fields: [{ key: 'Reddit__ClientId', type: 'text', value: '' }],
                    configured: false,
                    providerTypes: ['thought'],
                },
                bluesky: {
                    name: 'Bluesky',
                    fields: [],
                    configured: false,
                    providerTypes: ['thought'],
                },
            },
        },
        configEdits: {} as Record<string, string>,
        saveConfig: vi.fn(),
        _initConfigEdits: vi.fn(),
        sync: vi.fn(),
        ...overrides,
    };
}

// ── wizardNext ───────────────────────────────────────────────────────────────

describe('wizardNext', () => {
    it('advances wizardStep by 1', async () => {
        const ctx = makeCtx({ wizardStep: 0 });
        await methods.wizardNext.call(ctx);
        expect(ctx.wizardStep).toBe(1);
    });

    it('does not exceed step 3', async () => {
        const ctx = makeCtx({ wizardStep: 3 });
        await methods.wizardNext.call(ctx);
        expect(ctx.wizardStep).toBe(3);
    });
});

// ── wizardBack ───────────────────────────────────────────────────────────────

describe('wizardBack', () => {
    it('decrements wizardStep by 1', () => {
        const ctx = makeCtx({ wizardStep: 2 });
        methods.wizardBack.call(ctx);
        expect(ctx.wizardStep).toBe(1);
    });

    it('does not go below step 0', () => {
        const ctx = makeCtx({ wizardStep: 0 });
        methods.wizardBack.call(ctx);
        expect(ctx.wizardStep).toBe(0);
    });
});

// ── wizardSkip ───────────────────────────────────────────────────────────────

describe('wizardSkip', () => {
    it('sets wizardActive to false', () => {
        const ctx = makeCtx({ wizardActive: true });
        methods.wizardSkip.call(ctx);
        expect(ctx.wizardActive).toBe(false);
    });

    it('does not set wb_wizardCompleted in localStorage', () => {
        localStorage.removeItem('wb_wizardCompleted');
        const ctx = makeCtx();
        methods.wizardSkip.call(ctx);
        expect(localStorage.getItem('wb_wizardCompleted')).toBeNull();
    });
});

// ── wizardComplete ───────────────────────────────────────────────────────────

describe('wizardComplete', () => {
    beforeEach(() => { localStorage.removeItem('wb_wizardCompleted'); });

    it('sets wizardActive to false', () => {
        const ctx = makeCtx();
        methods.wizardComplete.call(ctx);
        expect(ctx.wizardActive).toBe(false);
    });

    it('sets wb_wizardCompleted in localStorage', () => {
        const ctx = makeCtx();
        methods.wizardComplete.call(ctx);
        expect(localStorage.getItem('wb_wizardCompleted')).toBe('true');
    });

    it('triggers sync', () => {
        const ctx = makeCtx();
        methods.wizardComplete.call(ctx);
        expect(ctx.sync as ReturnType<typeof vi.fn>).toHaveBeenCalled();
    });
});

// ── wizardSelectWatch ────────────────────────────────────────────────────────

describe('wizardSelectWatch', () => {
    it('selects a watch provider', () => {
        const ctx = makeCtx();
        methods.wizardSelectWatch.call(ctx, 'jellyfin');
        expect(ctx.wizardSelectedWatch).toBe('jellyfin');
    });

    it('deselects when clicking the same provider', () => {
        const ctx = makeCtx({ wizardSelectedWatch: 'jellyfin' });
        methods.wizardSelectWatch.call(ctx, 'jellyfin');
        expect(ctx.wizardSelectedWatch).toBeNull();
    });

    it('switches selection when clicking a different provider', () => {
        const ctx = makeCtx({ wizardSelectedWatch: 'jellyfin' });
        methods.wizardSelectWatch.call(ctx, 'trakt');
        expect(ctx.wizardSelectedWatch).toBe('trakt');
    });
});

// ── wizardToggleComment ──────────────────────────────────────────────────────

describe('wizardToggleComment', () => {
    it('adds a comment source', () => {
        const ctx = makeCtx();
        methods.wizardToggleComment.call(ctx, 'reddit');
        expect((ctx.wizardSelectedComments as Set<string>).has('reddit')).toBe(true);
    });

    it('removes a comment source when toggled again', () => {
        const ctx = makeCtx({ wizardSelectedComments: new Set(['reddit']) });
        methods.wizardToggleComment.call(ctx, 'reddit');
        expect((ctx.wizardSelectedComments as Set<string>).has('reddit')).toBe(false);
    });

    it('supports multiple selections', () => {
        const ctx = makeCtx();
        methods.wizardToggleComment.call(ctx, 'reddit');
        methods.wizardToggleComment.call(ctx, 'bluesky');
        const comments = ctx.wizardSelectedComments as Set<string>;
        expect(comments.has('reddit')).toBe(true);
        expect(comments.has('bluesky')).toBe(true);
    });

    it('creates a new Set for reactivity', () => {
        const original = new Set<string>();
        const ctx = makeCtx({ wizardSelectedComments: original });
        methods.wizardToggleComment.call(ctx, 'reddit');
        expect(ctx.wizardSelectedComments).not.toBe(original);
    });
});

// ── wizardSaveAndContinue ────────────────────────────────────────────────────

describe('wizardSaveAndContinue', () => {
    afterEach(() => { vi.restoreAllMocks(); });

    it('saves watch provider config on step 1', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({
                integrations: {
                    jellyfin: { fields: [], configured: true, providerTypes: ['watchState'] },
                },
            }),
        }));
        const ctx = makeCtx({ wizardStep: 1, wizardSelectedWatch: 'jellyfin' });
        await methods.wizardSaveAndContinue.call(ctx);
        expect(ctx.saveConfig as ReturnType<typeof vi.fn>).toHaveBeenCalledWith('jellyfin');
        expect(ctx.wizardStep).toBe(2);
    });

    it('saves each selected comment source on step 2', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({
                integrations: {
                    reddit: { fields: [], configured: true, providerTypes: ['thought'] },
                    bluesky: { fields: [], configured: true, providerTypes: ['thought'] },
                },
            }),
        }));
        const ctx = makeCtx({
            wizardStep: 2,
            wizardSelectedComments: new Set(['reddit', 'bluesky']),
        });
        await methods.wizardSaveAndContinue.call(ctx);
        const saveFn = ctx.saveConfig as ReturnType<typeof vi.fn>;
        expect(saveFn).toHaveBeenCalledWith('reddit');
        expect(saveFn).toHaveBeenCalledWith('bluesky');
        expect(ctx.wizardStep).toBe(3);
    });

    it('sets wizardSaving during operation', async () => {
        let savingDuringCall = false;
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({ integrations: {} }),
        }));
        const ctx = makeCtx({
            wizardStep: 1,
            wizardSelectedWatch: 'jellyfin',
            saveConfig: vi.fn().mockImplementation(function(this: Record<string, unknown>) {
                savingDuringCall = this.wizardSaving as boolean;
            }),
        });
        await methods.wizardSaveAndContinue.call(ctx);
        expect(savingDuringCall).toBe(true);
        expect(ctx.wizardSaving).toBe(false); // reset after
    });

    it('reloads configData after saving', async () => {
        const updatedConfig = { integrations: { jellyfin: { configured: true } } };
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            ok: true,
            json: async () => updatedConfig,
        }));
        const ctx = makeCtx({ wizardStep: 1, wizardSelectedWatch: 'jellyfin' });
        await methods.wizardSaveAndContinue.call(ctx);
        expect(ctx.configData).toEqual(updatedConfig);
        expect(ctx._initConfigEdits as ReturnType<typeof vi.fn>).toHaveBeenCalledWith(updatedConfig);
    });

    it('resets wizardSaving even on error', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Network')));
        const ctx = makeCtx({
            wizardStep: 1,
            wizardSelectedWatch: 'jellyfin',
            saveConfig: vi.fn().mockRejectedValue(new Error('fail')),
        });
        // The method catches via finally, so wizardSaving should be reset
        try {
            await methods.wizardSaveAndContinue.call(ctx);
        } catch {
            // expected
        }
        expect(ctx.wizardSaving).toBe(false);
    });
});

// ── dismissChecklist ─────────────────────────────────────────────────────────

describe('dismissChecklist', () => {
    it('sets checklistDismissed to true', () => {
        const ctx = makeCtx({ checklistDismissed: false });
        methods.dismissChecklist.call(ctx);
        expect(ctx.checklistDismissed).toBe(true);
    });
});

// ── openConfigForItem ────────────────────────────────────────────────────────

describe('openConfigForItem', () => {
    it('opens config panel', () => {
        const ctx = makeCtx({ showConfig: false });
        methods.openConfigForItem.call(ctx, 'watchState');
        expect(ctx.showConfig).toBe(true);
    });

    it('sets configTab to settings', () => {
        const ctx = makeCtx({ configTab: 'diagnostics' });
        methods.openConfigForItem.call(ctx, 'watchState');
        expect(ctx.configTab).toBe('settings');
    });

    it('uncollapses the first matching integration for watchState', () => {
        const ctx = makeCtx({ collapsedSections: { jellyfin: true, trakt: true } });
        methods.openConfigForItem.call(ctx, 'watchState');
        expect((ctx.collapsedSections as Record<string, boolean>).jellyfin).toBe(false);
        // trakt also has watchState but we only uncollapse the first match
        expect((ctx.collapsedSections as Record<string, boolean>).trakt).toBe(true);
    });

    it('uncollapses the first matching integration for thought', () => {
        const ctx = makeCtx({ collapsedSections: { reddit: true } });
        methods.openConfigForItem.call(ctx, 'thought');
        // trakt comes before reddit in the test data, and trakt has 'thought'
        expect((ctx.collapsedSections as Record<string, boolean>).trakt).toBe(false);
    });
});

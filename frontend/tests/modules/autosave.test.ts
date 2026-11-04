import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import autosaveMethods from '../../src/modules/autosave';

type Ctx = Record<string, any>;

function makeCtx(overrides: Ctx = {}): Ctx {
    const ctx: Ctx = {
        configData: {
            integrations: {
                bluesky: {
                    name: 'Bluesky',
                    description: 'Posts about the episode',
                    configured: false,
                    disabled: false,
                    providerTypes: ['thought'],
                    fields: [
                        { key: 'Bluesky__Handle', type: 'text', value: 'me.bsky.social', hasValue: true },
                        { key: 'Bluesky__AppPassword', type: 'password', value: '', hasValue: false },
                    ],
                },
                reddit: {
                    name: 'Reddit',
                    configured: true,
                    disabled: false,
                    providerTypes: ['thought'],
                    fields: [{ key: 'Reddit__MaxThreads', type: 'number', value: '3', hasValue: true }],
                },
                jellyfin: {
                    name: 'Jellyfin',
                    configured: true,
                    disabled: false,
                    providerTypes: ['watchState'],
                    fields: [{ key: 'Jellyfin__BaseUrl', type: 'text', value: 'http://saved', envValue: 'http://env', isOverridden: true, hasValue: true }],
                },
            },
            preferences: { timeMachineDays: 14, watchProvider: 'jellyfin', searchEngine: 'google', customSearchUrl: '', overrides: {} },
        },
        configEdits: { 'Bluesky__Handle': 'me.bsky.social', 'Bluesky__AppPassword': '', 'Reddit__MaxThreads': '3', 'Jellyfin__BaseUrl': 'http://saved' },
        prefEdits: { timeMachineDays: 14, watchProvider: 'jellyfin', searchEngine: 'google', customSearchUrl: '' },
        fieldStatus: {},
        lastTestResults: {},
        openRow: null,
        t: (key: string) => key,
        ...overrides,
    };
    for (const [name, fn] of Object.entries(autosaveMethods)) {
        if (typeof fn === 'function') ctx[name] = (fn as Function).bind(ctx);
    }
    return ctx;
}

function respond(...bodies: { ok: boolean; json?: unknown }[]) {
    const fetchMock = vi.fn();
    for (const b of bodies) {
        fetchMock.mockResolvedValueOnce({ ok: b.ok, json: async () => b.json ?? {} });
    }
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
}

function bodyOf(call: unknown[]): unknown {
    return JSON.parse((call[1] as RequestInit).body as string);
}

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('commitField', () => {
    it('tests a pasted password first, then saves only that field', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Bluesky__AppPassword'] = 'abcd-efgh';
        const fetchMock = respond({ ok: true, json: { ok: true, message: 'Signed in' } }, { ok: true });

        await ctx.commitField('bluesky', 'Bluesky__AppPassword');

        expect(fetchMock.mock.calls[0][0]).toBe('/api/test/bluesky');
        expect(bodyOf(fetchMock.mock.calls[0])).toMatchObject({ 'Bluesky__AppPassword': 'abcd-efgh', 'Bluesky__Handle': 'me.bsky.social' });
        expect(fetchMock.mock.calls[1][0]).toBe('/api/config');
        expect(bodyOf(fetchMock.mock.calls[1])).toEqual({ 'Bluesky__AppPassword': 'abcd-efgh' });
        expect(ctx.fieldStatus['Bluesky__AppPassword'].state).toBe('saved');
        expect(ctx.configEdits['Bluesky__AppPassword']).toBe('');
        expect(ctx.configData.integrations.bluesky.configured).toBe(true);
        expect(ctx.configData.integrations.bluesky.fields[1].hasValue).toBe(true);
        expect(ctx.lastTestResults['bluesky'].status).toBe('ok');
    });

    it('does not save a value that fails its test, and offers Save anyway', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Bluesky__AppPassword'] = 'wrong';
        const fetchMock = respond({ ok: true, json: { ok: false, message: 'Invalid identifier or password' } });

        await ctx.commitField('bluesky', 'Bluesky__AppPassword');

        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(ctx.fieldStatus['Bluesky__AppPassword']).toMatchObject({
            state: 'error', message: 'Invalid identifier or password', canForce: true, tried: 'wrong',
        });
        expect(ctx.configEdits['Bluesky__AppPassword']).toBe('wrong');
    });

    it('does not test the same failed value again (each Bluesky test is a sign-in)', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Bluesky__AppPassword'] = 'wrong';
        const fetchMock = respond({ ok: true, json: { ok: false, message: 'no' } });

        await ctx.commitField('bluesky', 'Bluesky__AppPassword');
        await ctx.commitField('bluesky', 'Bluesky__AppPassword');

        expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    it('Save anyway skips the test', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Bluesky__AppPassword'] = 'wrong';
        respond({ ok: true, json: { ok: false, message: 'rate limited' } });
        await ctx.commitField('bluesky', 'Bluesky__AppPassword');

        const fetchMock = respond({ ok: true });
        await ctx.commitField('bluesky', 'Bluesky__AppPassword', true);

        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(fetchMock.mock.calls[0][0]).toBe('/api/config');
        expect(ctx.fieldStatus['Bluesky__AppPassword'].state).toBe('saved');
    });

    it('does nothing when the value is unchanged or the password is empty', async () => {
        const ctx = makeCtx();
        const fetchMock = respond();

        await ctx.commitField('bluesky', 'Bluesky__Handle');
        await ctx.commitField('bluesky', 'Bluesky__AppPassword');

        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('saves a number without testing the provider', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Reddit__MaxThreads'] = '5';
        const fetchMock = respond({ ok: true });

        await ctx.commitField('reddit', 'Reddit__MaxThreads');

        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ 'Reddit__MaxThreads': '5' });
        expect(ctx.configData.integrations.reddit.fields[0].value).toBe('5');
        expect(ctx.configData.integrations.reddit.fields[0].isOverridden).toBe(true);
    });

    it('rejects a number that is not a positive whole number', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Reddit__MaxThreads'] = '0';
        const fetchMock = respond();

        await ctx.commitField('reddit', 'Reddit__MaxThreads');

        expect(fetchMock).not.toHaveBeenCalled();
        expect(ctx.fieldStatus['Reddit__MaxThreads']).toMatchObject({ state: 'error', message: 'Settings_NumberInvalid' });
    });

    it('saves a cleared text field without testing', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Bluesky__Handle'] = '';
        const fetchMock = respond({ ok: true });

        await ctx.commitField('bluesky', 'Bluesky__Handle');

        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ 'Bluesky__Handle': '' });
    });

    it('ignores a second commit while the first is still testing', async () => {
        const ctx = makeCtx();
        ctx.fieldStatus = { 'Bluesky__AppPassword': { state: 'testing' } };
        ctx.configEdits['Bluesky__AppPassword'] = 'new';
        const fetchMock = respond();

        await ctx.commitField('bluesky', 'Bluesky__AppPassword');

        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('clears the Saved note after a moment', async () => {
        const ctx = makeCtx();
        ctx.configEdits['Reddit__MaxThreads'] = '4';
        respond({ ok: true });

        await ctx.commitField('reddit', 'Reddit__MaxThreads');
        expect(ctx.fieldStatus['Reddit__MaxThreads'].state).toBe('saved');
        vi.advanceTimersByTime(3000);
        expect(ctx.fieldStatus['Reddit__MaxThreads']).toBeUndefined();
    });
});

describe('useEnvValue', () => {
    it('deletes the saved value and shows the environment value again', async () => {
        const ctx = makeCtx();
        const fetchMock = respond({ ok: true });

        await ctx.useEnvValue('jellyfin', 'Jellyfin__BaseUrl');

        expect(fetchMock.mock.calls[0][1].method).toBe('DELETE');
        expect(bodyOf(fetchMock.mock.calls[0])).toEqual(['Jellyfin__BaseUrl']);
        const field = ctx.configData.integrations.jellyfin.fields[0];
        expect(field.isOverridden).toBe(false);
        expect(field.value).toBe('http://env');
        expect(ctx.configEdits['Jellyfin__BaseUrl']).toBe('http://env');
    });
});

describe('preferences', () => {
    it('chooseWatchProvider saves the choice at once', async () => {
        const ctx = makeCtx();
        const fetchMock = respond({ ok: true });

        await ctx.chooseWatchProvider('trakt');

        expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ 'WatchBack__WatchProvider': 'trakt' });
        expect(ctx.configData.preferences.watchProvider).toBe('trakt');
        expect(ctx.configData.preferences.overrides['WatchBack__WatchProvider']).toBe(true);
    });

    it('chooseWatchProvider puts the old choice back when the save fails', async () => {
        const ctx = makeCtx();
        respond({ ok: false });

        await ctx.chooseWatchProvider('trakt');

        expect(ctx.configData.preferences.watchProvider).toBe('jellyfin');
        expect(ctx.prefEdits.watchProvider).toBe('jellyfin');
        expect(ctx.fieldStatus['WatchBack__WatchProvider'].state).toBe('error');
    });

    it('commitTimeMachine saves a changed number and skips an unchanged one', async () => {
        const ctx = makeCtx();
        const fetchMock = respond({ ok: true });

        await ctx.commitTimeMachine();
        expect(fetchMock).not.toHaveBeenCalled();

        ctx.prefEdits.timeMachineDays = 30;
        await ctx.commitTimeMachine();
        expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ 'WatchBack__TimeMachineDays': '30' });
        expect(ctx.configData.preferences.timeMachineDays).toBe(30);
    });

    it('commitTimeMachine rejects days outside 1 to 365', async () => {
        const ctx = makeCtx();
        const fetchMock = respond();
        ctx.prefEdits.timeMachineDays = 400;

        await ctx.commitTimeMachine();

        expect(fetchMock).not.toHaveBeenCalled();
        expect(ctx.fieldStatus['WatchBack__TimeMachineDays'].state).toBe('error');
    });

    it('setPreferenceSwitch saves the switch and reverts it on failure', async () => {
        const ctx = makeCtx();
        ctx.prefEdits.enableSentimentAnalysis = false;
        respond({ ok: false });

        await ctx.setPreferenceSwitch('enableSentimentAnalysis', true);

        expect(ctx.prefEdits.enableSentimentAnalysis).toBe(false);
    });
});

describe('rowStatus', () => {
    it('reports off, failed, connected, ready and needs setup', () => {
        const ctx = makeCtx();
        expect(ctx.rowStatus('bluesky')).toEqual({ kind: 'warn', label: 'Settings_StatusNeedsSetup' });
        expect(ctx.rowStatus('reddit')).toEqual({ kind: 'ok', label: 'Settings_StatusReady' });

        ctx.lastTestResults = { reddit: { status: 'ok' }, bluesky: { status: 'error' } };
        expect(ctx.rowStatus('reddit').label).toBe('Config_Connected');
        expect(ctx.rowStatus('bluesky').kind).toBe('err');

        ctx.configData.integrations.reddit.disabled = true;
        expect(ctx.rowStatus('reddit')).toEqual({ kind: 'off', label: 'Settings_StatusOff' });
    });

    it('ignores the off switch for a watch source', () => {
        const ctx = makeCtx();
        ctx.configData.integrations.jellyfin.disabled = true;
        expect(ctx.rowStatus('jellyfin', true).kind).toBe('ok');
    });
});

describe('rowSummary', () => {
    it('shows non-secret text values without the URL scheme', () => {
        const ctx = makeCtx();
        expect(ctx.rowSummary('jellyfin')).toBe('saved');
        expect(ctx.rowSummary('bluesky')).toBe('me.bsky.social');
    });

    it('never shows a credential, and falls back to the description', () => {
        const ctx = makeCtx();
        ctx.configData.integrations.bluesky.fields = [{ key: 'Trakt__ClientId', type: 'text', value: 'abc123', secret: true }];
        expect(ctx.rowSummary('bluesky')).toBe('Posts about the episode');
    });
});

describe('toggleRow', () => {
    it('keeps one row open at a time', () => {
        const ctx = makeCtx();
        ctx.toggleRow('reddit');
        expect(ctx.openRow).toBe('reddit');
        ctx.toggleRow('bluesky');
        expect(ctx.openRow).toBe('bluesky');
        ctx.toggleRow('bluesky');
        expect(ctx.openRow).toBeNull();
    });
});

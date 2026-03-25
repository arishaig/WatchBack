import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import authMethods from '../../src/modules/auth';

const methods = authMethods as Record<string, Function>;

function makeCtx(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        loginLoading: false,
        loginUsername: 'testuser',
        loginPassword: 'pass',
        loginError: null,
        setupLoading: false,
        setupUsername: 'newuser',
        setupPassword: 'newpass',
        setupError: null,
        changePwNew: '',
        changePwConfirm: '',
        changePwError: null,
        changePwLoading: false,
        passwordStrength: null,
        authState: 'login',
        currentUser: null,
        resetPasswordStatus: null,
        forwardAuthEnabled: false,
        forwardAuthHeaderEdit: 'X-Remote-User',
        forwardAuthTrustedHostEdit: '',
        forwardAuthSaveStatus: null,
        data: null,
        configData: null,
        t: (key: string) => key,
        initApp: vi.fn(),
        ...overrides,
    };
}

// ── login ─────────────────────────────────────────────────────────────────────

describe('login', () => {
    afterEach(() => { vi.useRealTimers(); });

    it('does nothing if already loading', async () => {
        const fetchMock = vi.fn();
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({ loginLoading: true });
        await methods.login.call(ctx);
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('calls initApp on successful login without flags', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: true }),
        }));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect((ctx.initApp as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
        expect(ctx.loginLoading).toBe(false);
        expect(ctx.loginError).toBeNull();
    });

    it('sets authState to "onboarding" when needsOnboarding flag is set', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: true, needsOnboarding: true }),
        }));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect(ctx.authState).toBe('onboarding');
    });

    it('sets authState to "changePassword" when needsPasswordChange flag is set', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: true, needsPasswordChange: true }),
        }));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect(ctx.authState).toBe('changePassword');
    });

    it('sets loginError from server message on failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: false, message: 'Bad credentials' }),
        }));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect(ctx.loginError).toBe('Bad credentials');
    });

    it('falls back to t("Auth_InvalidCredentials") when no message', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: false }),
        }));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect(ctx.loginError).toBe('Auth_InvalidCredentials');
    });

    it('sets loginError from t("Auth_ConnectionFailed") on network error', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Network')));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect(ctx.loginError).toBe('Auth_ConnectionFailed');
    });

    it('resets loginLoading to false after error', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Network')));
        const ctx = makeCtx();
        await methods.login.call(ctx);
        expect(ctx.loginLoading).toBe(false);
    });

    it('POSTs username and password to /api/auth/login', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ json: async () => ({ ok: true }) });
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({ loginUsername: 'alice', loginPassword: 'secret' });
        await methods.login.call(ctx);
        const [url, opts] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(url).toBe('/api/auth/login');
        expect(JSON.parse(opts.body as string)).toEqual({ username: 'alice', password: 'secret' });
    });
});

// ── setupAccount ──────────────────────────────────────────────────────────────

describe('setupAccount', () => {
    it('does nothing if already loading', async () => {
        const fetchMock = vi.fn();
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({ setupLoading: true });
        await methods.setupAccount.call(ctx);
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('calls initApp and sets currentUser on success', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: true }),
        }));
        const ctx = makeCtx({ setupUsername: 'alice' });
        await methods.setupAccount.call(ctx);
        expect((ctx.initApp as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
        expect((ctx.currentUser as Record<string, unknown>)?.username).toBe('alice');
    });

    it('sets setupError on failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: false, message: 'Username taken' }),
        }));
        const ctx = makeCtx();
        await methods.setupAccount.call(ctx);
        expect(ctx.setupError).toBe('Username taken');
    });

    it('resets setupLoading to false after completion', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('err')));
        const ctx = makeCtx();
        await methods.setupAccount.call(ctx);
        expect(ctx.setupLoading).toBe(false);
    });
});

// ── changePassword ────────────────────────────────────────────────────────────

describe('changePassword', () => {
    it('sets error when changePwNew is empty', async () => {
        const ctx = makeCtx({ changePwNew: '', changePwConfirm: '' });
        await methods.changePassword.call(ctx);
        expect(ctx.changePwError).toBe('Auth_PasswordRequired');
    });

    it('sets error when passwords do not match', async () => {
        const ctx = makeCtx({ changePwNew: 'abc', changePwConfirm: 'xyz' });
        await methods.changePassword.call(ctx);
        expect(ctx.changePwError).toBe('Auth_PasswordsDoNotMatch');
    });

    it('does nothing if already loading', async () => {
        const fetchMock = vi.fn();
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({ changePwLoading: true, changePwNew: 'abc', changePwConfirm: 'abc' });
        await methods.changePassword.call(ctx);
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('calls initApp on success', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: true }),
        }));
        const ctx = makeCtx({ changePwNew: 'newpass', changePwConfirm: 'newpass' });
        await methods.changePassword.call(ctx);
        expect((ctx.initApp as ReturnType<typeof vi.fn>)).toHaveBeenCalled();
    });

    it('sets changePwError on failed response', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: false, message: 'Too short' }),
        }));
        const ctx = makeCtx({ changePwNew: 'abc', changePwConfirm: 'abc' });
        await methods.changePassword.call(ctx);
        expect(ctx.changePwError).toBe('Too short');
    });
});

// ── logout ────────────────────────────────────────────────────────────────────

describe('logout', () => {
    it('resets all auth state', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({}));
        const ctx = makeCtx({
            authState: 'app',
            currentUser: { username: 'alice' },
            loginUsername: 'alice',
            loginPassword: 'pass',
            data: { status: 'Watching' },
            configData: { integrations: {} },
        });
        await methods.logout.call(ctx);
        expect(ctx.authState).toBe('login');
        expect(ctx.currentUser).toBeNull();
        expect(ctx.loginUsername).toBe('');
        expect(ctx.loginPassword).toBe('');
        expect(ctx.loginError).toBeNull();
        expect(ctx.data).toBeNull();
        expect(ctx.configData).toBeNull();
    });

    it('POSTs to /api/auth/logout', async () => {
        const fetchMock = vi.fn().mockResolvedValue({});
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx();
        await methods.logout.call(ctx);
        const [url, opts] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(url).toBe('/api/auth/logout');
        expect(opts.method).toBe('POST');
    });
});

// ── resetPassword ─────────────────────────────────────────────────────────────

describe('resetPassword', () => {
    beforeEach(() => { vi.useFakeTimers(); });
    afterEach(() => { vi.useRealTimers(); });

    it('sets resetPasswordStatus to "ok" on success', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: true }),
        }));
        const ctx = makeCtx();
        await methods.resetPassword.call(ctx);
        expect(ctx.resetPasswordStatus).toBe('ok');
    });

    it('sets resetPasswordStatus to "error" on failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
            json: async () => ({ ok: false }),
        }));
        const ctx = makeCtx();
        await methods.resetPassword.call(ctx);
        expect(ctx.resetPasswordStatus).toBe('error');
    });

    it('sets resetPasswordStatus to "error" on network error', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error()));
        const ctx = makeCtx();
        await methods.resetPassword.call(ctx);
        expect(ctx.resetPasswordStatus).toBe('error');
    });

    it('auto-clears status after 5 seconds', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ json: async () => ({ ok: true }) }));
        const ctx = makeCtx();
        await methods.resetPassword.call(ctx);
        expect(ctx.resetPasswordStatus).toBe('ok');
        vi.advanceTimersByTime(5000);
        expect(ctx.resetPasswordStatus).toBeNull();
    });
});

// ── saveForwardAuth ───────────────────────────────────────────────────────────

describe('saveForwardAuth', () => {
    beforeEach(() => { vi.useFakeTimers(); });
    afterEach(() => { vi.useRealTimers(); });

    it('sends empty header when forwardAuthEnabled is false', async () => {
        let capturedBody: Record<string, string> = {};
        vi.stubGlobal('fetch', vi.fn().mockImplementation((_url: string, opts: RequestInit) => {
            capturedBody = JSON.parse(opts.body as string);
            return Promise.resolve({ json: async () => ({ ok: true }) });
        }));
        const ctx = makeCtx({ forwardAuthEnabled: false, forwardAuthHeaderEdit: 'X-Custom' });
        await methods.saveForwardAuth.call(ctx);
        expect(capturedBody.header).toBe('');
    });

    it('sends custom header when forwardAuthEnabled is true and header is set', async () => {
        let capturedBody: Record<string, string> = {};
        vi.stubGlobal('fetch', vi.fn().mockImplementation((_url: string, opts: RequestInit) => {
            capturedBody = JSON.parse(opts.body as string);
            return Promise.resolve({ json: async () => ({ ok: true }) });
        }));
        const ctx = makeCtx({ forwardAuthEnabled: true, forwardAuthHeaderEdit: 'X-Remote-User' });
        await methods.saveForwardAuth.call(ctx);
        expect(capturedBody.header).toBe('X-Remote-User');
    });

    it('falls back to "X-Remote-User" when header is empty', async () => {
        let capturedBody: Record<string, string> = {};
        vi.stubGlobal('fetch', vi.fn().mockImplementation((_url: string, opts: RequestInit) => {
            capturedBody = JSON.parse(opts.body as string);
            return Promise.resolve({ json: async () => ({ ok: true }) });
        }));
        const ctx = makeCtx({ forwardAuthEnabled: true, forwardAuthHeaderEdit: '' });
        await methods.saveForwardAuth.call(ctx);
        expect(capturedBody.header).toBe('X-Remote-User');
    });

    it('sets forwardAuthSaveStatus to "saved" on success', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ json: async () => ({ ok: true }) }));
        const ctx = makeCtx();
        await methods.saveForwardAuth.call(ctx);
        expect(ctx.forwardAuthSaveStatus).toBe('saved');
    });

    it('sets forwardAuthSaveStatus to "error" on failure', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ json: async () => ({ ok: false }) }));
        const ctx = makeCtx();
        await methods.saveForwardAuth.call(ctx);
        expect(ctx.forwardAuthSaveStatus).toBe('error');
    });

    it('auto-clears status after 3 seconds', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ json: async () => ({ ok: true }) }));
        const ctx = makeCtx();
        await methods.saveForwardAuth.call(ctx);
        vi.advanceTimersByTime(3000);
        expect(ctx.forwardAuthSaveStatus).toBeNull();
    });
});

import type { AppData } from '../types';

const authMethods: Record<string, unknown> & ThisType<AppData> = {
    async login() {
        if (this.loginLoading) return;
        this.loginLoading = true;
        this.loginError = null;
        try {
            const res = await fetch('/api/auth/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ username: this.loginUsername, password: this.loginPassword }),
            });
            const data = await res.json() as Record<string, unknown>;
            if (data['ok']) {
                this.currentUser = { username: this.loginUsername, authMethod: 'cookie' };
                if (data['needsOnboarding']) {
                    this.authState = 'onboarding';
                } else if (data['needsPasswordChange']) {
                    this.authState = 'changePassword';
                } else {
                    await this.initApp();
                }
            } else {
                this.loginError = (data['message'] as string) || this.t('Auth_InvalidCredentials');
            }
        } catch {
            this.loginError = this.t('Auth_ConnectionFailed');
        }
        this.loginLoading = false;
    },

    async setupAccount() {
        if (this.setupLoading) return;
        this.setupLoading = true;
        this.setupError = null;
        try {
            const res = await fetch('/api/auth/setup', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newUsername: this.setupUsername, newPassword: this.setupPassword }),
            });
            const data = await res.json() as Record<string, unknown>;
            if (data['ok']) {
                this.currentUser = { username: this.setupUsername, authMethod: 'cookie' };
                await this.initApp();
            } else {
                this.setupError = (data['message'] as string) || this.t('Auth_SetupFailed');
            }
        } catch {
            this.setupError = this.t('Auth_ConnectionFailed');
        }
        this.setupLoading = false;
    },

    async changePassword() {
        if (this.changePwLoading) return;
        if (!this.changePwCurrent) {
            this.changePwError = this.t('Auth_PasswordRequired');
            return;
        }
        if (!this.changePwNew) {
            this.changePwError = this.t('Auth_PasswordRequired');
            return;
        }
        if (this.changePwNew !== this.changePwConfirm) {
            this.changePwError = this.t('Auth_PasswordsDoNotMatch');
            return;
        }
        this.changePwLoading = true;
        this.changePwError = null;
        try {
            const res = await fetch('/api/auth/change-password', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ currentPassword: this.changePwCurrent, newPassword: this.changePwNew }),
            });
            const data = await res.json() as Record<string, unknown>;
            if (data['ok']) {
                await this.initApp();
            } else {
                this.changePwError = (data['message'] as string) || this.t('Auth_PasswordChangeFailed');
            }
        } catch {
            this.changePwError = this.t('Auth_ConnectionFailed');
        }
        this.changePwLoading = false;
    },

    async evaluatePasswordStrength(password: string) {
        if (!password) { this.passwordStrength = null; return; }
        // Lazy-load zxcvbn on first use
        if (typeof (window as Window & { zxcvbn?: unknown }).zxcvbn === 'undefined') {
            await new Promise<void>((resolve, reject) => {
                const s = document.createElement('script');
                s.src = '/js/zxcvbn.min.js';
                s.onload = () => resolve();
                s.onerror = reject;
                document.head.appendChild(s);
            });
        }
        const zxcvbn = (window as unknown as Window & { zxcvbn: (pw: string, userInputs?: string[]) => { score: number; feedback: { warning: string; suggestions: string[] } } }).zxcvbn;
        const result = zxcvbn(password, [this.loginUsername, this.setupUsername, 'watchback'].filter(Boolean));
        const labels = [this.t('Setup_PasswordVeryWeak'), this.t('Setup_PasswordWeak'), this.t('Setup_PasswordFair'), this.t('Setup_PasswordStrong'), this.t('Setup_PasswordVeryStrong')];
        const colors = ['#ef4444', '#f97316', '#eab308', '#22c55e', '#16a34a'];
        this.passwordStrength = {
            score: result.score,
            label: labels[result.score],
            color: colors[result.score],
            width: `${Math.max((result.score + 1) * 20, 10)}%`,
            warning: result.feedback.warning || null,
            suggestions: result.feedback.suggestions || [],
        };
    },

    async logout() {
        await fetch('/api/auth/logout', { method: 'POST' });
        this.authState = 'login';
        this.currentUser = null;
        this.loginUsername = '';
        this.loginPassword = '';
        this.loginError = null;
        this.passwordStrength = null;
        this.data = null;
        this.configData = null;
    },

    async resetPassword() {
        this.resetPasswordStatus = 'loading';
        try {
            const res = await fetch('/api/auth/reset-password', { method: 'POST' });
            const data = await res.json() as Record<string, unknown>;
            this.resetPasswordStatus = data['ok'] ? 'ok' : 'error';
        } catch {
            this.resetPasswordStatus = 'error';
        }
        setTimeout(() => { this.resetPasswordStatus = null; }, 5000);
    },

    async saveForwardAuth() {
        this.forwardAuthSaveStatus = 'saving';
        try {
            const header = this.forwardAuthEnabled ? (this.forwardAuthHeaderEdit.trim() || 'X-Remote-User') : '';
            const trustedHost = this.forwardAuthEnabled ? this.forwardAuthTrustedHostEdit.trim() : '';
            const res = await fetch('/api/auth/forward-auth', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ header, trustedHost }),
            });
            const data = await res.json() as Record<string, unknown>;
            this.forwardAuthSaveStatus = data['ok'] ? 'saved' : 'error';
        } catch {
            this.forwardAuthSaveStatus = 'error';
        }
        setTimeout(() => { this.forwardAuthSaveStatus = null; }, 3000);
    },
};

export default authMethods;

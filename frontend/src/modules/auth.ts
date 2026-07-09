import type { AppData } from '../types';
import { uiLog } from '../utils/uiLogger';

const authMethods: Record<string, unknown> & ThisType<AppData> = {
    async login() {
        if (this.loginLoading) return;
        uiLog("auth.login", "Login attempt", undefined, "Information");
        this.loginLoading = true;
        this.loginError = null;
        try {
            const res = await fetch('/api/auth/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ username: this.loginUsername, password: this.loginPassword }),
            });
            uiLog("auth.login.response", "Login response", { status: res.status });
            const data = await res.json() as Record<string, unknown>;
            if (data['ok']) {
                uiLog("auth.login.success", "Login succeeded", {
                    needsOnboarding: data['needsOnboarding'],
                    needsPasswordChange: data['needsPasswordChange'],
                }, "Information");
                this.currentUser = { authenticated: true, username: this.loginUsername, authMethod: 'cookie' };
                if (data['needsOnboarding']) {
                    this.authState = 'onboarding';
                } else if (data['needsPasswordChange']) {
                    this.authState = 'changePassword';
                } else {
                    await this.initApp();
                }
            } else {
                uiLog("auth.login.failed", "Login rejected", { message: data['message'] }, "Warning");
                this.loginError = (data['message'] as string) || this.t('Auth_InvalidCredentials');
            }
        } catch (e) {
            uiLog("auth.login.error", "Login request failed", { error: String(e) }, "Error");
            this.loginError = this.t('Auth_ConnectionFailed');
        }
        this.loginLoading = false;
    },

    async setupAccount() {
        if (this.setupLoading) return;
        uiLog("auth.setup", "Account setup attempt", undefined, "Information");
        this.setupLoading = true;
        this.setupError = null;
        try {
            const res = await fetch('/api/auth/setup', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newUsername: this.setupUsername, newPassword: this.setupPassword }),
            });
            uiLog("auth.setup.response", "Setup response", { status: res.status });
            const data = await res.json() as Record<string, unknown>;
            if (data['ok']) {
                uiLog("auth.setup.success", "Account setup succeeded", undefined, "Information");
                this.currentUser = { authenticated: true, username: this.setupUsername, authMethod: 'cookie' };
                localStorage.removeItem('wb_wizardCompleted');
                localStorage.removeItem('wb_checklistCompleted');
                await this.initApp();
            } else {
                uiLog("auth.setup.failed", "Account setup rejected", { message: data['message'] }, "Warning");
                this.setupError = (data['message'] as string) || this.t('Auth_SetupFailed');
            }
        } catch (e) {
            uiLog("auth.setup.error", "Account setup request failed", { error: String(e) }, "Error");
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
        uiLog("auth.changePassword", "Password change attempt", undefined, "Information");
        this.changePwLoading = true;
        this.changePwError = null;
        try {
            const res = await fetch('/api/auth/change-password', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ currentPassword: this.changePwCurrent, newPassword: this.changePwNew }),
            });
            uiLog("auth.changePassword.response", "Change-password response", { status: res.status });
            const data = await res.json() as Record<string, unknown>;
            if (data['ok']) {
                uiLog("auth.changePassword.success", "Password changed successfully", undefined, "Information");
                await this.initApp();
            } else {
                uiLog("auth.changePassword.failed", "Password change rejected", { message: data['message'] }, "Warning");
                this.changePwError = (data['message'] as string) || this.t('Auth_PasswordChangeFailed');
            }
        } catch (e) {
            uiLog("auth.changePassword.error", "Password change request failed", { error: String(e) }, "Error");
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
        type ZxcvbnFn = (pw: string, userInputs?: string[]) => { score: number; feedback: { warning: string; suggestions: string[] } };
        const zxcvbn = (window as Window & { zxcvbn?: ZxcvbnFn }).zxcvbn;
        if (typeof zxcvbn !== 'function') {
            this.passwordStrength = null;
            return;
        }
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
        uiLog("auth.logout", "Logout triggered", undefined, "Information");
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
            if (data['ok'] && data['restarting']) {
                // The trusted-proxy network is only resolved at startup, so a change
                // to it can't take effect until the process restarts.
                this.forwardAuthSaveStatus = 'restarting';
                await this.waitForServerRestart();
                return;
            }
            this.forwardAuthSaveStatus = data['ok'] ? 'saved' : 'error';
        } catch {
            this.forwardAuthSaveStatus = 'error';
        }
        setTimeout(() => { this.forwardAuthSaveStatus = null; }, 3000);
    },
};

export default authMethods;

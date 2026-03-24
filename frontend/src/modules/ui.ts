import type { AppData } from '../types';
import { interpolate } from '../strings';

const uiMethods: Record<string, unknown> & ThisType<AppData> = {
    t(key: string, ...args: unknown[]): string {
        void this._stringsReady;
        const bundle = window._allStrings[this.locale] ?? window._allStrings['en'] ?? {};
        const str = bundle[key];
        if (!str) return key;
        return interpolate(str, args);
    },

    async init() {
        console.log("[WatchBack] Initializing");
        this.applyTheme(this.theme);
        this.$watch('showConfig', (v: unknown) => { if (!v) this.closeLogStream(); });

        await window.loadAllStrings();
        this.supportedLocales = window._supportedLocales;
        if (!window._allStrings[this.locale]) this.locale = 'en';
        window._currentLocale = this.locale;
        this._stringsReady = true;

        this.$watch('locale', (val: unknown) => {
            window._currentLocale = val as string;
            localStorage.setItem('wb_locale', val as string);
            document.documentElement.lang = val as string;
        });

        // Auto-dismiss checklist when all items complete
        this.$watch('checklistAllComplete', (v: unknown) => {
            if (v && this.authState === 'app' && !localStorage.getItem('wb_checklistCompleted')) {
                this.checklistAutoComplete = true;
                setTimeout(() => {
                    this.checklistAutoComplete = false;
                    localStorage.setItem('wb_checklistCompleted', 'true');
                }, 2500);
            }
        });

        await this.fetchThemes();
        const me = await this.checkAuth();
        if (!me['authenticated']) {
            this.authState = 'login';
            this.initialized = true;
            return;
        }
        this.currentUser = me;
        if (me['needsOnboarding']) {
            this.authState = 'onboarding';
            this.initialized = true;
            return;
        }
        await this.initApp();
    },

    async fetchThemes() {
        try {
            const res = await fetch('/api/themes');
            if (res.ok) {
                const data = await res.json() as unknown;
                if (Array.isArray(data) && data.length > 0) this.themes = data as { id: string; label: string }[];
            }
        } catch {
            // keep default fallback list
        }
    },

    async checkAuth(): Promise<Record<string, unknown>> {
        try {
            const res = await fetch('/api/auth/me');
            const me = await res.json() as Record<string, unknown>;
            this.needsOnboarding = (me['needsOnboarding'] as boolean | undefined) ?? false;
            if (me['authenticated']) {
                this.forwardAuthEnabled = !!(me['forwardAuthHeader'] as string | undefined);
                this.forwardAuthHeaderEdit = (me['forwardAuthHeader'] as string | undefined) || 'X-Remote-User';
            }
            return me;
        } catch {
            return { authenticated: false };
        }
    },

    async initApp() {
        this.authState = 'app';
        try {
            const cRes = await fetch('/api/config');
            if (cRes.ok) {
                this.configData = await cRes.json() as Record<string, unknown>;
                this._initConfigEdits(this.configData);
            }
        } catch (e) {
            console.warn("[WatchBack] Config load failed:", e);
        }
        this.initialized = true;
        this.setupSSE();
        void this.sync();

        // Launch wizard for first-time users
        if (!localStorage.getItem('wb_wizardCompleted') && !localStorage.getItem('wb_checklistCompleted')) {
            this.wizardActive = true;
        }
    },

    showError(msg: string) {
        this.error = msg;
        clearTimeout(this.errorTimer ?? undefined);
        this.errorTimer = setTimeout(() => { this.error = null; }, 8000);
    },

    applyTheme(mode: string) {
        const themeMode = mode || (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
        document.documentElement.setAttribute('data-theme', themeMode);
    },

    handleThreadToggle() {
        this.groupByThread = !this.groupByThread;
        if (this.groupByThread) {
            localStorage.setItem('wb_groupByThread', 'true');
        } else {
            localStorage.removeItem('wb_groupByThread');
        }
    },

    toggleSource(src: string) {
        if (this.sourceFilter.has(src)) {
            this.sourceFilter.delete(src);
        } else {
            this.sourceFilter.add(src);
        }
        this.sourceFilter = new Set(this.sourceFilter);
    },

    sourceActive(src: string): boolean {
        return this.sourceFilter.size === 0 || this.sourceFilter.has(src);
    },

    sourceCount(src: string): number {
        if (!this.data) return 0;
        const list = this.mode === 'time'
            ? ((this.data['timeMachineThoughts'] as { source: string }[]) ?? [])
            : ((this.data['allThoughts'] as { source: string }[]) ?? []);
        return list.filter(c => c.source === src).length;
    },

    toggleSection(key: string) {
        this.collapsedSections[key] = !this.collapsedSections[key];
        localStorage.setItem('wb_collapsed_sections', JSON.stringify(this.collapsedSections));
    },

    toggleAlwaysShowSearch() {
        this.alwaysShowSearch = !this.alwaysShowSearch;
        if (this.alwaysShowSearch) {
            localStorage.setItem('wb_alwaysShowSearch', 'true');
        } else {
            localStorage.removeItem('wb_alwaysShowSearch');
        }
    },

    formatDate(iso: string): string {
        if (!iso) return '';
        try {
            return new Date(iso).toLocaleDateString();
        } catch {
            return iso;
        }
    },

    formatScore(n: number | null): string {
        if (n == null) return '';
        if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
        return String(n);
    },

    countAllReplies(c: Record<string, unknown>): number {
        const replies = c['replies'] as Record<string, unknown>[] | undefined;
        if (!replies || !Array.isArray(replies)) return 0;
        return replies.reduce((n: number, r: Record<string, unknown>) => n + 1 + this.countAllReplies(r), 0);
    },

    formatLogTime(iso: string): string {
        try { return new Date(iso).toLocaleTimeString('en-US', { hour12: false }); }
        catch { return iso; }
    },

    formatRelativeTime(iso: string): string {
        if (!iso) return '';
        const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
        if (secs < 60) return this.t('Time_SecondsAgo', secs);
        const mins = Math.floor(secs / 60);
        if (mins < 60) return this.t('Time_MinutesAgo', mins);
        return this.t('Time_HoursAgo', Math.floor(mins / 60));
    },

    logLevelClass(level: string): string {
        return ({
            Trace: 'wb-text-muted', Debug: 'wb-text-muted',
            Information: 'wb-accent-text', Warning: 'wb-warn-text',
            Error: 'wb-error-text', Critical: 'wb-error-text',
        } as Record<string, string>)[level] || 'wb-text-muted';
    },

    logLevelAbbr(level: string): string {
        return ({ Trace: 'TRC', Debug: 'DBG', Information: 'INF', Warning: 'WRN', Error: 'ERR', Critical: 'CRT' } as Record<string, string>)[level]
            || (level || '').slice(0, 3).toUpperCase();
    },
};

export default uiMethods;

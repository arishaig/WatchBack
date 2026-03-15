/**
 * Lightweight Reddit-flavored markdown → HTML renderer.
 * Handles: blockquotes, bold, italic, strikethrough, links, inline code,
 * code blocks, superscript, headings, spoiler tags, and unordered lists.
 * Output is sanitised — no raw HTML passes through.
 */
function renderMarkdown(src) {
    if (!src) return '';

    // Escape HTML entities first — prevents XSS while preserving literal &, <, > display
    let t = src.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

    // Fenced code blocks (```...```)
    t = t.replace(/```([\s\S]*?)```/g, (_, code) =>
        '<pre class="wb-md-codeblock">' + code.trim() + '</pre>');

    // Process line-based features (blockquotes, headings, lists)
    const lines = t.split('\n');
    const out = [];
    let inQuote = false;
    for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        const quoteMatch = line.match(/^&gt;(?!!)\s?(.*)/);
        if (quoteMatch) {
            if (!inQuote) { out.push('<blockquote class="wb-md-quote">'); inQuote = true; }
            out.push(quoteMatch[1]);
        } else {
            if (inQuote) { out.push('</blockquote>'); inQuote = false; }
            // Headings (# to ####)
            const hMatch = line.match(/^(#{1,4})\s+(.*)/);
            if (hMatch) {
                const level = Math.min(hMatch[1].length + 2, 6); // render as h3-h6 to stay small
                out.push(`<h${level} class="wb-md-heading">${hMatch[2]}</h${level}>`);
            } else if (line.match(/^\s*[-*]\s+/)) {
                // Unordered list item
                out.push('<li class="wb-md-li">' + line.replace(/^\s*[-*]\s+/, '') + '</li>');
            } else {
                out.push(line);
            }
        }
    }
    if (inQuote) out.push('</blockquote>');
    t = out.join('\n');

    // Wrap consecutive <li> elements in <ul>
    t = t.replace(/((?:<li class="wb-md-li">.*<\/li>\n?)+)/g,
        (m) => '<ul class="wb-md-ul">' + m + '</ul>');

    // Inline code (backticks)
    t = t.replace(/`([^`\n]+)`/g, '<code class="wb-md-code">$1</code>');
    // Bold+italic
    t = t.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
    // Bold
    t = t.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    // Italic
    t = t.replace(/(?<!\w)\*(.+?)\*(?!\w)/g, '<em>$1</em>');
    // Strikethrough
    t = t.replace(/~~(.+?)~~/g, '<del>$1</del>');
    // Superscript (Reddit ^word or ^(phrase))
    t = t.replace(/\^(\([^)]+\)|\S+)/g, (_, content) =>
        '<sup>' + content.replace(/^\(|\)$/g, '') + '</sup>');
    // Reddit spoiler tags >!text!<
    t = t.replace(/&gt;!(.+?)!&lt;/g,
        '<span class="wb-md-spoiler" tabindex="0" role="button" aria-label="Reveal spoiler" onclick="this.classList.add(\'revealed\')" onkeydown="if(event.key===\'Enter\')this.classList.add(\'revealed\')">$1</span>');
    // Links [text](url)
    t = t.replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g,
        '<a href="$2" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>');
    // Bare URLs (not already inside href="...")
    t = t.replace(/(?<!="|'>)(https?:\/\/[^\s<)]+)/g,
        '<a href="$1" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>');
    // Horizontal rules (--- or ***)
    t = t.replace(/^(-{3,}|\*{3,})$/gm, '<hr class="wb-md-hr">');

    return t;
}

document.addEventListener('alpine:init', () => {
    Alpine.data('app', () => ({
        // App state
        initialized: false,
        data: null,
        error: null,
        errorTimer: null,
        isLoading: false,
        mode: 'all',
        sourceFilter: new Set(),
        showConfig: false,
        configData: null,
        configEdits: {},   // flat dict keyed by field.key ("Section__Key")
        saveStatus: {},    // per-integration save state (legacy, kept for compat)
        saveAllStatus: null, // global save state: saving | saved | error | null
        prefEdits: {},     // preference field edits
        prefSaveStatus: null,
        lightboxImg: null,
        groupByThread: localStorage.getItem('wb_groupByThread') === 'true',
        theme: localStorage.getItem('wb_theme') || (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'),
        testResults: {},
        lastTestResults: {},
        testAllStatus: null,
        authState: 'checking', // checking | login | onboarding | app
        loginUsername: '',
        loginPassword: '',
        loginError: null,
        loginLoading: false,
        setupUsername: '',
        setupPassword: '',
        setupError: null,
        setupLoading: false,
        currentUser: null,
        resetPasswordStatus: null,
        syncProgress: null,
        clearCacheStatus: null,
        forwardAuthEnabled: false,
        forwardAuthHeaderEdit: '',
        forwardAuthSaveStatus: null,
        needsOnboarding: false,
        themes: [
            { id: 'dark', label: 'Dark' },
            { id: 'light', label: 'Light' },
            { id: 'solarized-dark', label: 'Solarized Dark' },
            { id: 'solarized-light', label: 'Solarized Light' },
            { id: 'monokai', label: 'Monokai' },
        ],

        async init() {
            console.log("[WatchBack] Initializing");
            this.applyTheme(this.theme);
            await this.fetchThemes();
            const me = await this.checkAuth();
            if (!me.authenticated) {
                this.authState = 'login';
                this.initialized = true;
                return;
            }
            this.currentUser = me;
            if (me.needsOnboarding) {
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
                    const data = await res.json();
                    if (Array.isArray(data) && data.length > 0) this.themes = data;
                }
            } catch {
                // keep default fallback list
            }
        },

        async checkAuth() {
            try {
                const res = await fetch('/api/auth/me');
                const me = await res.json();
                this.needsOnboarding = me.needsOnboarding ?? false;
                if (me.authenticated) {
                    this.forwardAuthEnabled = !!me.forwardAuthHeader;
                    this.forwardAuthHeaderEdit = me.forwardAuthHeader || 'X-Remote-User';
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
                    this.configData = await cRes.json();
                    this._initConfigEdits(this.configData);
                }
            } catch (e) {
                console.warn("[WatchBack] Config load failed:", e);
            }
            await this.sync();
            this.initialized = true;
            this.setupSSE();
        },

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
                const data = await res.json();
                if (data.ok) {
                    this.currentUser = { username: this.loginUsername, authMethod: 'cookie' };
                    if (data.needsOnboarding) {
                        this.authState = 'onboarding';
                    } else {
                        await this.initApp();
                    }
                } else {
                    this.loginError = data.message || 'Invalid credentials';
                }
            } catch {
                this.loginError = 'Connection failed';
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
                const data = await res.json();
                if (data.ok) {
                    this.currentUser = { username: this.setupUsername, authMethod: 'cookie' };
                    await this.initApp();
                } else {
                    this.setupError = data.message || 'Setup failed';
                }
            } catch {
                this.setupError = 'Connection failed';
            }
            this.setupLoading = false;
        },

        async logout() {
            await fetch('/api/auth/logout', { method: 'POST' });
            this.authState = 'login';
            this.currentUser = null;
            this.loginUsername = '';
            this.loginPassword = '';
            this.loginError = null;
            this.initialized = false;
            this.data = null;
            this.configData = null;
            this.initialized = true;
        },

        async resetPassword() {
            this.resetPasswordStatus = 'loading';
            try {
                const res = await fetch('/api/auth/reset-password', { method: 'POST' });
                const data = await res.json();
                this.resetPasswordStatus = data.ok ? 'ok' : 'error';
            } catch {
                this.resetPasswordStatus = 'error';
            }
            setTimeout(() => { this.resetPasswordStatus = null; }, 5000);
        },

        async saveForwardAuth() {
            this.forwardAuthSaveStatus = 'saving';
            try {
                const header = this.forwardAuthEnabled ? (this.forwardAuthHeaderEdit.trim() || 'X-Remote-User') : '';
                const res = await fetch('/api/auth/forward-auth', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ header }),
                });
                const data = await res.json();
                this.forwardAuthSaveStatus = data.ok ? 'saved' : 'error';
            } catch {
                this.forwardAuthSaveStatus = 'error';
            }
            setTimeout(() => { this.forwardAuthSaveStatus = null; }, 3000);
        },

        async clearCache() {
            this.clearCacheStatus = 'loading';
            try {
                const res = await fetch('/api/system/clear-cache', { method: 'POST' });
                const data = await res.json();
                this.clearCacheStatus = data.ok ? 'ok' : 'error';
            } catch {
                this.clearCacheStatus = 'error';
            }
            setTimeout(() => { this.clearCacheStatus = null; }, 3000);
        },

        async restart() {
            this.showConfig = false;
            this.restartStatus = 'loading';
            try {
                await fetch('/api/system/restart', { method: 'POST' });
            } catch {
                // Network error is expected as the server shuts down — continue polling
            }
            // Show the full-screen loading spinner while waiting for the server to come back
            this.initialized = false;
            this.authState = 'checking';
            // Poll /api/auth/me until the server responds, then re-initialize
            while (true) {
                await new Promise(r => setTimeout(r, 1000));
                try {
                    const res = await fetch('/api/auth/me');
                    if (res.ok) { await this.init(); return; }
                } catch {
                    // still down — keep polling
                }
            }
        },

        setupSSE() {
            const es = new ReconnectingEventSource('/api/sync/stream', { max_retry_time: 60000 });
            es.onmessage = (e) => {
                if (e.data) {
                    try {
                        const data = JSON.parse(e.data.replace(/^data: /, ''));
                        if (data.completed !== undefined) {
                            this.syncProgress = { completed: data.completed, total: data.total };
                            return;
                        }
                        // Only accept real sync responses (must have a status field)
                        if (!data?.status) return;
                        this.syncProgress = null;
                        console.debug("[WatchBack] SSE update:", data);
                        this.data = data;
                    } catch (err) {
                        console.debug("[WatchBack] SSE parse:", err);
                    }
                }
            };
            es.onerror = () => {
                console.warn("[WatchBack] SSE connection lost");
            };
        },

        applyTheme(mode) {
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

        toggleSource(src) {
            if (this.sourceFilter.has(src)) {
                this.sourceFilter.delete(src);
            } else {
                this.sourceFilter.add(src);
            }
            // Force Alpine reactivity by replacing the Set
            this.sourceFilter = new Set(this.sourceFilter);
        },

        sourceActive(src) {
            return this.sourceFilter.size === 0 || this.sourceFilter.has(src);
        },

        get showTimeMachine() {
            if (!this.data) return false;
            const tm = this.data.timeMachineThoughts || [];
            if (tm.length === 0) return false;
            // Hide if the premiere window is still open
            const premiere = this.data.metadata?.releaseDate;
            if (premiere) {
                const cutoff = new Date(premiere);
                cutoff.setDate(cutoff.getDate() + (this.data.timeMachineDays || 14));
                if (cutoff >= new Date()) return false;
            }
            return true;
        },

        get activeThoughts() {
            if (!this.data) return [];
            const list = this.mode === 'time' ? (this.data.timeMachineThoughts || []) : (this.data.allThoughts || []);
            if (this.sourceFilter.size === 0) return list;
            return list.filter(c => this.sourceFilter.has(c.source));
        },

        get hasThreadGroups() {
            return this.activeThoughts.some(c => c.postTitle);
        },

        get timeMachineCount() {
            if (!this.data) return 0;
            const list = this.data.timeMachineThoughts || [];
            if (this.sourceFilter.size === 0) return list.length;
            return list.filter(c => this.sourceFilter.has(c.source)).length;
        },

        get allThoughtsCount() {
            if (!this.data) return 0;
            const list = this.data.allThoughts || [];
            if (this.sourceFilter.size === 0) return list.length;
            return list.filter(c => this.sourceFilter.has(c.source)).length;
        },

        get threadCount() {
            return this.groupedThoughts.filter(g => g.title).length;
        },

        sourceCount(src) {
            if (!this.data) return 0;
            const list = this.mode === 'time' ? (this.data.timeMachineThoughts || []) : (this.data.allThoughts || []);
            return list.filter(c => c.source === src).length;
        },

        get availableSources() {
            if (!this.data) return [];
            const sources = new Set();
            const list = this.data.allThoughts || [];
            list.forEach(t => { if (t.source) sources.add(t.source); });
            return [...sources];
        },

        get renderGroups() {
            if (!this.groupByThread) {
                return this.activeThoughts.map(c => ({ title: null, url: null, thoughts: [c] }));
            }
            return this.groupedThoughts;
        },

        get groupedThoughts() {
            const thoughts = this.activeThoughts;
            const groups = [];
            const map = new Map();
            for (const c of thoughts) {
                const key = c.postTitle || '';
                if (!map.has(key)) {
                    const g = { title: c.postTitle || null, url: c.postUrl || null, body: c.postBody || null, thoughts: [] };
                    map.set(key, g);
                    groups.push(g);
                }
                map.get(key).thoughts.push(c);
            }
            return groups;
        },

        showError(msg) {
            this.error = msg;
            clearTimeout(this.errorTimer);
            this.errorTimer = setTimeout(() => { this.error = null; }, 8000);
        },

        async sync() {
            if (this.isLoading) return;
            console.debug("[WatchBack] Syncing data...");
            this.isLoading = true;
            try {
                const res = await fetch('/api/sync?t=' + Date.now());
                const newData = await res.json();

                if (newData?.status === 'Watching') {
                    this.error = null;
                    console.info(`[WatchBack] Synced: ${newData.title}`, {
                        timeMachine: newData.timeMachineThoughts?.length || 0,
                        allThoughts: newData.allThoughts?.length || 0
                    });
                } else if (newData?.status === 'Idle') {
                    console.debug("[WatchBack] No active session");
                } else if (newData?.status === 'Error') {
                    this.showError('Sync failed');
                } else {
                    console.warn(`[WatchBack] Sync status: ${newData?.status}`);
                }

                this.data = newData;
                // Default to time machine when meaningful
                if (this.data?.status === 'Watching') {
                    this.mode = this.showTimeMachine ? 'time' : 'all';
                }
            } catch (e) {
                console.error("[WatchBack] Sync failed:", e);
                this.showError('Connection failed');
            }
            finally { this.isLoading = false; }
        },

        _initConfigEdits(configData) {
            const edits = {};
            for (const integration of Object.values(configData?.integrations || {})) {
                for (const field of integration.fields) {
                    // Pre-fill text fields with current value; password fields start blank
                    edits[field.key] = field.type === 'password' ? '' : (field.value ?? '');
                }
            }
            this.configEdits = edits;
            this.prefEdits = {
                timeMachineDays: configData?.preferences?.timeMachineDays ?? 14,
                watchProvider: configData?.preferences?.watchProvider ?? 'jellyfin',
                searchEngine: configData?.preferences?.searchEngine ?? 'google',
                customSearchUrl: configData?.preferences?.customSearchUrl ?? '',
            };
        },

        async saveConfig(integrationKey) {
            const integration = this.configData?.integrations?.[integrationKey];
            if (!integration) return;

            const payload = {};
            for (const field of integration.fields) {
                const val = this.configEdits[field.key];
                if (field.type === 'password') {
                    // Only send if user typed something; blank = keep existing
                    if (val && val.trim() !== '') payload[field.key] = val;
                } else {
                    payload[field.key] = val ?? '';
                }
            }

            this.saveStatus = { ...this.saveStatus, [integrationKey]: 'saving' };
            try {
                const res = await fetch('/api/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload),
                });
                if (res.ok) {
                    this.saveStatus = { ...this.saveStatus, [integrationKey]: 'saved' };
                    // Refresh config to get updated hasValue flags
                    const cRes = await fetch('/api/config');
                    if (cRes.ok) {
                        this.configData = await cRes.json();
                        // Re-init edits but preserve blank password fields
                        for (const field of this.configData.integrations[integrationKey]?.fields ?? []) {
                            if (field.type !== 'password')
                                this.configEdits[field.key] = field.value ?? '';
                        }
                    }
                } else {
                    this.saveStatus = { ...this.saveStatus, [integrationKey]: 'error' };
                }
            } catch {
                this.saveStatus = { ...this.saveStatus, [integrationKey]: 'error' };
            }
            setTimeout(() => {
                const { [integrationKey]: _, ...rest } = this.saveStatus;
                this.saveStatus = rest;
            }, 3000);
        },

        async saveAllConfig() {
            const payload = {};
            for (const [, integration] of Object.entries(this.configData?.integrations || {})) {
                for (const field of integration.fields) {
                    const val = this.configEdits[field.key];
                    if (field.type === 'password') {
                        if (val && val.trim() !== '') payload[field.key] = val;
                    } else {
                        payload[field.key] = val ?? '';
                    }
                }
            }
            this.saveAllStatus = 'saving';
            try {
                const res = await fetch('/api/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload),
                });
                if (res.ok) {
                    this.saveAllStatus = 'saved';
                    const cRes = await fetch('/api/config');
                    if (cRes.ok) {
                        this.configData = await cRes.json();
                        for (const [, integration] of Object.entries(this.configData.integrations || {})) {
                            for (const field of integration.fields) {
                                if (field.type !== 'password')
                                    this.configEdits[field.key] = field.value ?? '';
                            }
                        }
                    }
                } else {
                    this.saveAllStatus = 'error';
                }
            } catch {
                this.saveAllStatus = 'error';
            }
            setTimeout(() => { this.saveAllStatus = null; }, 3000);
        },

        redditSearchUrl(title, season, episode) {
            const query = encodeURIComponent(
                title + ' S' + String(season).padStart(2, '0') + 'E' + String(episode).padStart(2, '0') + ' reddit'
            );
            const engine = this.prefEdits?.searchEngine ?? this.configData?.preferences?.searchEngine ?? 'google';
            const custom = this.prefEdits?.customSearchUrl ?? this.configData?.preferences?.customSearchUrl ?? '';
            const bases = {
                google: 'https://www.google.com/search?q=',
                duckduckgo: 'https://duckduckgo.com/?q=',
                bing: 'https://www.bing.com/search?q=',
            };
            const base = engine === 'custom' ? (custom || bases.google) : (bases[engine] ?? bases.google);
            return base + query;
        },

        async resetConfigKeys(keys) {
            await fetch('/api/config', {
                method: 'DELETE',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(keys),
            });
            const res = await fetch('/api/config');
            if (res.ok) {
                this.configData = await res.json();
                this._initConfigEdits(this.configData);
            }
        },

        async savePreferences() {
            const payload = {};
            if (this.prefEdits.timeMachineDays != null)
                payload['WatchBack__TimeMachineDays'] = String(this.prefEdits.timeMachineDays);
            if (this.prefEdits.watchProvider)
                payload['WatchBack__WatchProvider'] = this.prefEdits.watchProvider;
            if (this.prefEdits.searchEngine)
                payload['WatchBack__SearchEngine'] = this.prefEdits.searchEngine;
            payload['WatchBack__CustomSearchUrl'] = this.prefEdits.customSearchUrl ?? '';

            this.prefSaveStatus = 'saving';
            try {
                const res = await fetch('/api/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload),
                });
                this.prefSaveStatus = res.ok ? 'saved' : 'error';
                if (res.ok) {
                    const cRes = await fetch('/api/config');
                    if (cRes.ok) this.configData = await cRes.json();
                }
            } catch {
                this.prefSaveStatus = 'error';
            }
            setTimeout(() => { this.prefSaveStatus = null; }, 3000);
        },

        testIcon(service) {
            const r = this.lastTestResults[service];
            if (!r) {
                const configured = this.configData?.integrations?.[service]?.configured;
                if (configured) return { icon: '\u2713', cls: 'wb-accent-text', label: 'configured' };
                return { icon: '\u2717', cls: 'wb-text-faint', label: 'not configured' };
            }
            if (r.status === 'ok') return { icon: '\u2713', cls: 'wb-success-text', label: 'connected' };
            return { icon: '\u2717', cls: 'wb-error-text', label: 'connection failed' };
        },

        async testService(service) {
            this.testResults = { ...this.testResults, [service]: { status: 'testing' } };

            // Build payload from current edits — send credentials directly so the server
            // tests what's in the form right now, not whatever's in the saved config.
            const integration = this.configData?.integrations?.[service];
            const payload = {};
            for (const field of integration?.fields ?? []) {
                const val = this.configEdits[field.key];
                // For password fields, prefer the typed value; fall back to the stored
                // sentinel so the server knows the field has a value even if unchanged.
                if (field.type === 'password') {
                    payload[field.key] = (val && val.trim()) ? val : (field.hasValue ? '__EXISTING__' : '');
                } else {
                    payload[field.key] = val ?? '';
                }
            }

            try {
                const res = await fetch(`/api/test/${service}`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload),
                });
                const data = await res.json();
                const result = { status: data.ok ? 'ok' : 'error', message: data.message };
                this.testResults = { ...this.testResults, [service]: result };
                this.lastTestResults = { ...this.lastTestResults, [service]: result };
            } catch (e) {
                const result = { status: 'error', message: 'Request failed' };
                this.testResults = { ...this.testResults, [service]: result };
                this.lastTestResults = { ...this.lastTestResults, [service]: result };
            }
            setTimeout(() => {
                const { [service]: _, ...rest } = this.testResults;
                this.testResults = rest;
            }, 4000);
        },

        async testAll() {
            this.testAllStatus = 'testing';
            const services = Object.keys(this.configData?.integrations || {}).concat('reddit');
            await Promise.all(services.map(s => this.testService(s)));
            const results = services.map(s => this.lastTestResults[s]);
            const anyResult = results.some(r => r);
            const allOk = anyResult && results.every(r => r?.status === 'ok');
            const allFailed = anyResult && results.every(r => r?.status === 'error');
            this.testAllStatus = allOk ? 'ok' : allFailed ? 'error' : 'warn';
            setTimeout(() => { this.testAllStatus = null; }, 5000);
        },

        formatDate(iso) {
            if (!iso) return '';
            try {
                return new Date(iso).toLocaleDateString();
            } catch {
                return iso;
            }
        },

        countAllReplies(c) {
            if (!c.replies || !Array.isArray(c.replies)) return 0;
            return c.replies.reduce((n, r) => n + 1 + this.countAllReplies(r), 0);
        },

        formatScore(n) {
            if (n == null) return '';
            if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
            return String(n);
        }
    }))
})

/**
 * Lightweight Reddit-flavored markdown → HTML renderer.
 * Handles: blockquotes, bold, italic, strikethrough, links, inline code,
 * code blocks, superscript, headings, spoiler tags, and unordered lists.
 * Output is sanitised — no raw HTML passes through.
 */
function renderMarkdown(src) {
    if (!src) return '';
    // Escape HTML entities first (sanitise)
    // Was displaying escaped strings instead of
    // let t = src.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    // Fenced code blocks (```...```)
    let t = src.replace(/```([\s\S]*?)```/g, (_, code) =>
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
        // Auth state
        authState: 'loading', // 'loading' | 'login' | 'setup' | 'ready'
        forwardAuthEnabled: false,
        authUser: null,
        loginUsername: '',
        loginPassword: '',
        loginError: '',
        loginLoading: false,
        setupUsername: '',
        setupEmail: '',
        setupPassword: '',
        setupConfirm: '',
        setupError: '',
        setupLoading: false,

        // App state
        data: null, error: null, errorTimer: null, isLoading: false, isRestarting: false, restartSuccess: false, mode: 'all', sourceFilter: new Set(),
        status: null, showConfig: false, configData: null, configDraft: {}, lightboxImg: null,
        groupByThread: localStorage.getItem('wb_groupByThread') === 'true',
        prefSaveStatus: '',
        _prefSaveTimer: null,
        testResults: {},
        lastTestResults: {},
        // Forward auth warning modal
        fwdAuthWarning: false,
        // Admin user management
        adminUsers: null,
        showCreateUser: false,
        newUser: { username: '', email: '', is_admin: false },
        createUserError: '',
        createUserSuccess: false,
        createUserTempPass: '',
        createUserLoading: false,
        async init() {
            console.log("[WatchBack] Initializing application");
            // Probe auth config (unauthenticated) to know if forward auth is active
            try {
                const hRes = await fetch('/api/health');
                if (hRes.ok) {
                    const h = await hRes.json();
                    this.forwardAuthEnabled = h.forward_auth_enabled || false;
                }
            } catch (e) { /* non-fatal */ }
            // Check auth
            try {
                const res = await fetch('/api/auth/me');
                if (res.ok) {
                    this.authUser = await res.json();
                    if (this.authUser.must_change_password) {
                        this.authState = 'setup';
                        return;
                    }
                    this.authState = 'ready';
                } else {
                    this.authState = 'login';
                    return;
                }
            } catch (e) {
                this.authState = 'login';
                return;
            }
            await this._initApp();
        },
        async _initApp() {
            try {
                const [sRes, cRes] = await Promise.all([fetch('/api/status'), fetch('/api/config')]);
                if (sRes.status === 401 || cRes.status === 401) {
                    this.authState = 'login';
                    return;
                }
                this.status = await sRes.json();
                this.configData = await cRes.json();
                this.forwardAuthEnabled = this.status.forward_auth_enabled || false;
                this.configDraft = this.buildDraft();
                this.applyTheme();
                console.debug("[WatchBack] Configuration loaded", { jellyfin: this.status.jellyfin_configured, trakt: this.status.trakt_configured });
            } catch (e) {
                console.error("[WatchBack] Failed to initialize:", e);
            }
            this.sync();
            this.setupSSE();
        },
        async login() {
            this.loginError = '';
            this.loginLoading = true;
            try {
                const res = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: new URLSearchParams({ username: this.loginUsername, password: this.loginPassword }),
                });
                if (res.status === 204 || res.ok) {
                    const meRes = await fetch('/api/auth/me');
                    if (meRes.ok) {
                        this.authUser = await meRes.json();
                        if (this.authUser.must_change_password) {
                            this.authState = 'setup';
                        } else {
                            this.authState = 'ready';
                            this.$nextTick(() => this._initApp());
                        }
                    }
                } else {
                    this.loginError = 'Invalid username or password';
                }
            } catch (e) {
                this.loginError = 'Connection failed';
            }
            this.loginLoading = false;
        },
        async submitSetup() {
            this.setupError = '';
            if (!this.setupUsername || !this.setupEmail || !this.setupPassword) {
                this.setupError = 'All fields are required';
                return;
            }
            if (this.setupPassword !== this.setupConfirm) {
                this.setupError = 'Passwords do not match';
                return;
            }
            if (this.setupPassword.length < 8) {
                this.setupError = 'Password must be at least 8 characters';
                return;
            }
            this.setupLoading = true;
            try {
                const res = await fetch('/api/auth/setup', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        username: this.setupUsername,
                        email: this.setupEmail,
                        password: this.setupPassword,
                    }),
                });
                if (res.ok) {
                    this.authUser.must_change_password = false;
                    this.authUser.username = this.setupUsername;
                    this.authState = 'ready';
                    this.$nextTick(() => this._initApp());
                } else {
                    const data = await res.json();
                    this.setupError = data.detail || 'Setup failed';
                }
            } catch (e) {
                this.setupError = 'Connection failed';
            }
            this.setupLoading = false;
        },
        async logout() {
            await fetch('/api/auth/logout', { method: 'POST' });
            this.authState = 'login';
            this.authUser = null;
            this.data = null;
            this.loginUsername = '';
            this.loginPassword = '';
        },
        setupSSE() {
            const es = new ReconnectingEventSource('/api/stream', { max_retry_time: 60000 });
            es.onmessage = (e) => {
                if (e.data === 'refresh') this.sync();
            };
        },
        applyTheme(mode) {
            const themeMode = mode || this.configData?.theme_mode?.effective_value || 'dark';
            document.documentElement.setAttribute('data-theme', themeMode);
        },
        buildDraft() {
            const draft = {};
            if (!this.configData) return {};
            // Fields that use <select> need a value (not empty string) to display correctly
            const selectFields = new Set(['theme_mode']);
            Object.entries(this.configData).forEach(([key, meta]) => {
                if (meta.is_secret) {
                    draft[key] = '';
                } else if (selectFields.has(key)) {
                    draft[key] = meta.effective_value || meta.stored_value || '';
                } else {
                    draft[key] = meta.source === 'stored' ? meta.stored_value : '';
                }
            });
            return draft;
        },
        async resetField(key) {
            console.debug(`[WatchBack] Resetting field: ${key}`);
            try {
                const payload = {};
                payload[key] = ''; // Sending empty string triggers removal of override in backend
                await fetch('/api/config', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
                const cRes = await fetch('/api/config');
                this.configData = await cRes.json();
                this.configDraft = this.buildDraft();
                console.info(`[WatchBack] Field reset: ${key}`);
                this.sync();
            } catch (e) {
                console.error(`[WatchBack] Failed to reset field ${key}:`, e);
            }
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
            const tm = this.data.time_machine || [];
            if (tm.length === 0) return false;
            // Hide if the premiere window is still open — the filter has no meaning yet
            const premiere = this.data.metadata?.premiere;
            if (premiere) {
                const cutoff = new Date(premiere);
                cutoff.setDate(cutoff.getDate() + (this.data.time_machine_days || 14));
                if (cutoff >= new Date()) return false;
            }
            return true;
        },
        get activeComments() {
            if (!this.data) return [];
            const list = this.mode === 'time' ? this.data.time_machine : this.data.all_comments;
            if (this.sourceFilter.size === 0) return list;
            return list.filter(c => this.sourceFilter.has(c.source || 'trakt'));
        },
        get hasThreadGroups() {
            return this.activeComments.some(c => c.thread_title);
        },
        get timeMachineCount() {
            if (!this.data) return 0;
            const list = this.data.time_machine || [];
            if (this.sourceFilter.size === 0) return list.length;
            return list.filter(c => this.sourceFilter.has(c.source || 'trakt')).length;
        },
        get allCommentsCount() {
            if (!this.data) return 0;
            const list = this.data.all_comments || [];
            if (this.sourceFilter.size === 0) return list.length;
            return list.filter(c => this.sourceFilter.has(c.source || 'trakt')).length;
        },
        get threadCount() {
            return this.groupedComments.length;
        },
        sourceCount(src) {
            if (!this.data) return 0;
            const list = this.mode === 'time' ? (this.data.time_machine || []) : (this.data.all_comments || []);
            return list.filter(c => (c.source || 'trakt') === src).length;
        },
        get availableSources() {
            return ['trakt', 'bluesky', 'reddit'].filter(s => this.sourceCount(s) > 0);
        },
        get renderGroups() {
            if (!this.groupByThread) {
                return this.activeComments.map(c => ({ title: null, url: null, comments: [c] }));
            }
            return this.groupedComments;
        },
        get groupedComments() {
            const comments = this.activeComments;
            const groups = [];
            const map = new Map();
            for (const c of comments) {
                const key = c.thread_title || '';
                if (!map.has(key)) {
                    const g = { title: c.thread_title || null, url: c.thread_url || null, comments: [] };
                    map.set(key, g);
                    groups.push(g);
                }
                map.get(key).comments.push(c);
            }
            return groups;
        },
        showError(msg) {
            this.error = msg;
            clearTimeout(this.errorTimer);
            this.errorTimer = setTimeout(() => { this.error = null; }, 8000);
        },
        async sync() {
            if (this.authState !== 'ready') return;
            console.debug("[WatchBack] Syncing data...");
            this.isLoading = true;
            try {
                const res = await fetch('/api/sync?t=' + Date.now());
                if (res.status === 401) { this.authState = 'login'; this.isLoading = false; return; }
                const newData = await res.json();

                if (newData?.status === 'success') {
                    this.error = null;
                    console.info(`[WatchBack] Synced: ${newData.title}`, { timeMachine: newData.time_machine?.length || 0, allComments: newData.all_comments?.length || 0 });
                } else if (newData?.status === 'idle') {
                    console.debug("[WatchBack] No active session");
                } else if (newData?.status === 'error') {
                    this.showError(newData.message || 'Sync failed');
                } else {
                    console.warn(`[WatchBack] Sync status: ${newData?.status}`);
                }

                this.data = newData;
                // Default to time machine when it has a meaningful subset, otherwise all
                if (this.data?.status === 'success') {
                    this.mode = this.showTimeMachine ? 'time' : 'all';
                }
            } catch (e) {
                console.error("[WatchBack] Sync failed:", e);
                this.showError('Connection failed');
            }
            finally { this.isLoading = false; }
        },
        async saveConfig() {
            console.log("[WatchBack] Saving configuration...");
            try {
                // Only send fields with non-empty values to avoid clearing untouched fields
                const payload = {};
                Object.entries(this.configDraft).forEach(([key, value]) => {
                    if (value) payload[key] = value;
                });
                const saveRes = await fetch('/api/config', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
                if (!saveRes.ok) throw new Error(`HTTP ${saveRes.status}`);
                const cRes = await fetch('/api/config');
                this.configData = await cRes.json();
                this.configDraft = this.buildDraft();
                this.applyTheme();
                console.info("[WatchBack] Configuration saved successfully");
                this.sync(); // Immediate refresh after saving
            } catch (e) {
                console.error("[WatchBack] Failed to save configuration:", e);
            }
        },
        toggleForwardAuth(enabled) {
            if (enabled) {
                this.fwdAuthWarning = true;
            } else {
                this.resetField('forward_auth_enabled').then(() => {
                    this.forwardAuthEnabled = false;
                });
            }
        },
        async confirmEnableForwardAuth() {
            this.configDraft.forward_auth_enabled = '1';
            this.fwdAuthWarning = false;
            await this.saveConfig();
            this.forwardAuthEnabled = true;
        },
        cancelEnableForwardAuth() {
            this.fwdAuthWarning = false;
        },
        async clearCache() {
            console.warn("[WatchBack] Clearing cache...");
            try {
                const res = await fetch('/api/cache/clear', { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                console.info("[WatchBack] Cache cleared");
                this.sync();
            } catch (e) {
                console.error("[WatchBack] Cache clear failed:", e);
            }
        },
        async restartServer() {
            console.warn("[WatchBack] Restarting server...");
            this.isRestarting = true;
            this.restartSuccess = false;
            try {
                const res = await fetch('/api/restart', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ confirm: true }) });
                const result = await res.json();
                if (result.status === 'restarting') {
                    console.info("[WatchBack] Server restart initiated, waiting for recovery...");
                    await this._waitForServer();
                } else {
                    console.error("[WatchBack] Restart failed:", result);
                }
            } catch (e) {
                // Request may fail if server exits before responding — still wait for recovery
                console.info("[WatchBack] Server went down, waiting for recovery...");
                await this._waitForServer();
            }
            // Show success state briefly, then clear
            this.restartSuccess = true;
            await new Promise(r => setTimeout(r, 1200));
            this.isRestarting = false;
            this.restartSuccess = false;
        },
        async _waitForServer(maxWait = 30000) {
            const start = Date.now();
            while (Date.now() - start < maxWait) {
                await new Promise(r => setTimeout(r, 1000));
                try {
                    const [sRes, cRes] = await Promise.all([fetch('/api/status'), fetch('/api/config')]);
                    if (sRes.ok && cRes.ok) {
                        console.info("[WatchBack] Server is back, re-initializing...");
                        this.status = await sRes.json();
                        this.configData = await cRes.json();
                        this.configDraft = this.buildDraft();
                        this.applyTheme();
                        await this.sync();
                        return;
                    }
                } catch { }
            }
            console.warn("[WatchBack] Server did not recover within timeout");
        },
        async autoSavePref(key, value) {
            try {
                const payload = { [key]: value };
                const res = await fetch('/api/config', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const cRes = await fetch('/api/config');
                this.configData = await cRes.json();
                this.prefSaveStatus = 'Saved';
                setTimeout(() => { this.prefSaveStatus = ''; }, 2000);
            } catch (e) {
                console.error("[WatchBack] Failed to save preference:", e);
                this.prefSaveStatus = 'Error';
                setTimeout(() => { this.prefSaveStatus = ''; }, 2000);
            }
        },
        testIcon(service) {
            const r = this.lastTestResults[service];
            if (!r) return { icon: '\u2713', cls: 'wb-accent-text', label: 'configured' };
            if (r.status === 'ok') return { icon: '\u2713', cls: 'wb-success-text', label: 'connected' };
            return { icon: '\u2717', cls: 'wb-error-text', label: 'connection failed' };
        },
        async testService(service) {
            // Save current draft values first so the test uses the latest input
            try {
                const payload = {};
                Object.entries(this.configDraft).forEach(([key, value]) => { if (value) payload[key] = value; });
                await fetch('/api/config', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
                const cRes = await fetch('/api/config');
                this.configData = await cRes.json();
                this.configDraft = this.buildDraft();
            } catch (e) {
                console.warn("[WatchBack] Could not save before test:", e);
            }
            this.testResults = { ...this.testResults, [service]: { status: 'testing' } };
            try {
                const res = await fetch(`/api/test/${service}`);
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
            const services = ['jellyfin', 'trakt', 'trakt-watch', 'bluesky', 'reddit'];
            await Promise.all(services.map(s => this.testService(s)));
        },
        async loadUsers() {
            try {
                const res = await fetch('/api/admin/users');
                if (res.ok) this.adminUsers = await res.json();
            } catch (e) { console.error('[WatchBack] Failed to load users:', e); }
        },
        async createUser() {
            this.createUserError = '';
            this.createUserSuccess = false;
            if (!this.newUser.username || !this.newUser.email) {
                this.createUserError = 'Username and email are required';
                return;
            }
            this.createUserLoading = true;
            try {
                const res = await fetch('/api/admin/users', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(this.newUser),
                });
                const data = await res.json();
                if (res.ok) {
                    this.createUserTempPass = data.temporary_password;
                    this.createUserSuccess = true;
                    this.newUser = { username: '', email: '', is_admin: false };
                    await this.loadUsers();
                } else {
                    this.createUserError = data.detail || 'Failed to create user';
                }
            } catch (e) {
                this.createUserError = 'Connection failed';
            }
            this.createUserLoading = false;
        },
        async resetUserPassword(user) {
            if (!confirm(`Reset password for ${user.username}? The temporary password will appear in container logs.`)) return;
            try {
                const res = await fetch(`/api/admin/users/${user.id}/reset-password`, { method: 'POST' });
                if (res.ok) {
                    await this.loadUsers();
                } else {
                    const data = await res.json();
                    alert(data.detail || 'Failed to reset password');
                }
            } catch (e) { alert('Connection failed'); }
        },
        async deleteUser(user) {
            if (!confirm(`Delete user "${user.username}"? This cannot be undone.`)) return;
            try {
                const res = await fetch(`/api/admin/users/${user.id}`, { method: 'DELETE' });
                if (res.ok) {
                    await this.loadUsers();
                } else {
                    const data = await res.json();
                    alert(data.detail || 'Failed to delete user');
                }
            } catch (e) { alert('Connection failed'); }
        },
        formatDate(iso) { return iso ? new Date(iso).toLocaleDateString() : ''; },
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

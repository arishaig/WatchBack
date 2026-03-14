/**
 * Lightweight Reddit-flavored markdown → HTML renderer.
 * Handles: blockquotes, bold, italic, strikethrough, links, inline code,
 * code blocks, superscript, headings, spoiler tags, and unordered lists.
 * Output is sanitised — no raw HTML passes through.
 */
function renderMarkdown(src) {
    if (!src) return '';

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
        theme: localStorage.getItem('wb_theme') || 'dark',
        testResults: {},
        lastTestResults: {},
        testAllStatus: null,

        async init() {
            console.log("[WatchBack] Initializing application");
            this.applyTheme(this.theme);

            // Load config
            try {
                const cRes = await fetch('/api/config');
                if (cRes.ok) {
                    this.configData = await cRes.json();
                    this._initConfigEdits(this.configData);
                }
            } catch (e) {
                console.warn("[WatchBack] Config load failed:", e);
            }

            // Initial sync
            await this.sync();
            this.initialized = true;
            this.setupSSE();
        },

        setupSSE() {
            const es = new ReconnectingEventSource('/api/sync/stream', { max_retry_time: 60000 });
            es.onmessage = (e) => {
                if (e.data) {
                    try {
                        const data = JSON.parse(e.data.replace(/^data: /, ''));
                        // Only accept real sync responses (must have a status field)
                        if (!data?.status) return;
                        console.debug("[WatchBack] SSE update:", data);
                        if (data.status === 'Watching') this._annotateThoughts(data);
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
            const themeMode = mode || 'dark';
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
            return this.activeThoughts.some(c => c._threadTitle);
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
            return this.groupedThoughts.length;
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
                const key = c._threadTitle || '';
                if (!map.has(key)) {
                    const g = { title: c._threadTitle || null, url: c._threadUrl || null, thoughts: [] };
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
                    // Annotate thoughts with thread info from sourceResults
                    this._annotateThoughts(newData);
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

        _annotateThoughts(data) {
            // Build a map from thought ID → sourceResult info (thread title/url)
            if (!data.sourceResults) return;
            const threadMap = new Map();
            for (const sr of data.sourceResults) {
                if (sr.thoughts) {
                    for (const t of sr.thoughts) {
                        threadMap.set(t.id, { title: sr.postTitle, url: sr.postUrl });
                    }
                }
            }
            // Annotate allThoughts and timeMachineThoughts
            const annotate = (list) => {
                if (!list) return;
                for (const t of list) {
                    const info = threadMap.get(t.id);
                    if (info) {
                        t._threadTitle = info.title;
                        t._threadUrl = info.url;
                    }
                }
            };
            annotate(data.allThoughts);
            annotate(data.timeMachineThoughts);
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

        async savePreferences() {
            const payload = {};
            if (this.prefEdits.timeMachineDays != null)
                payload['WatchBack__TimeMachineDays'] = String(this.prefEdits.timeMachineDays);
            if (this.prefEdits.watchProvider)
                payload['WatchBack__WatchProvider'] = this.prefEdits.watchProvider;

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

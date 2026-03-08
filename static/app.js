document.addEventListener('alpine:init', () => {
    Alpine.data('app', () => ({
        data: null, error: null, isLoading: false, isRestarting: false, restartSuccess: false, mode: 'all', sourceFilter: new Set(),
        status: null, showConfig: false, configData: null, configDraft: {}, lightboxImg: null,
        groupByThread: localStorage.getItem('wb_groupByThread') === 'true',
        prefSaveStatus: '',
        _prefSaveTimer: null,
        testResults: {},
        lastTestResults: {},
        async init() {
            console.log("[WatchBack] Initializing application");
            try {
                const [sRes, cRes] = await Promise.all([fetch('/api/status'), fetch('/api/config')]);
                this.status = await sRes.json();
                this.configData = await cRes.json();
                this.configDraft = this.buildDraft();
                this.applyTheme();
                console.debug("[WatchBack] Configuration loaded", { jellyfin: this.status.jellyfin_configured, trakt: this.status.trakt_configured });
            } catch (e) {
                console.error("[WatchBack] Failed to initialize:", e);
            }
            this.sync();
            this.setupSSE();
        },
        setupSSE() {
            const es = new EventSource('/api/stream');
            es.onmessage = (e) => {
                if (e.data === 'refresh') this.sync();
            };
            es.onerror = () => {
                es.close();
                setTimeout(() => this.setupSSE(), 5000);
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
        async sync() {
            console.debug("[WatchBack] Syncing data...");
            this.isLoading = true;
            try {
                const res = await fetch('/api/sync?t=' + Date.now());
                const newData = await res.json();

                if (newData?.status === 'success') {
                    console.info(`[WatchBack] Synced: ${newData.title}`, { timeMachine: newData.time_machine?.length || 0, allComments: newData.all_comments?.length || 0 });
                } else if (newData?.status === 'idle') {
                    console.debug("[WatchBack] No active session");
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
                this.error = "Connection failed";
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
                    const res = await fetch('/api/status');
                    if (res.ok) {
                        console.info("[WatchBack] Server is back, re-initializing...");
                        const [sRes, cRes] = await Promise.all([fetch('/api/status'), fetch('/api/config')]);
                        this.status = await sRes.json();
                        this.configData = await cRes.json();
                        this.configDraft = this.buildDraft();
                        this.applyTheme();
                        await this.sync();
                        return;
                    }
                } catch {}
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

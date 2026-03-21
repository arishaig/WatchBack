import type { AppData } from '../types';

const systemMethods: Record<string, unknown> & ThisType<AppData> = {
    setupSSE() {
        const es = new (window as Window & { ReconnectingEventSource: new (url: string, opts: Record<string, unknown>) => EventSource }).ReconnectingEventSource(
            '/api/sync/stream', { max_retry_time: 60000 }
        );
        es.onmessage = (e: MessageEvent) => {
            if (e.data) {
                try {
                    const data = JSON.parse((e.data as string).replace(/^data: /, '')) as Record<string, unknown>;
                    if (data['completed'] !== undefined) {
                        console.debug("[WatchBack] SSE progress:", data['completed'], "/", data['total']);
                        this._progressTickCount++;
                        if (this._progressTickCount >= 5) this.showSyncBar = true;
                        if ((data['completed'] as number) >= (data['total'] as number)) return;
                        this.syncProgress = { completed: data['completed'] as number, total: data['total'] as number };
                        if (data['providers']) this.syncSegments = data['providers'] as unknown[];
                        return;
                    }
                    if (!(data as Record<string, unknown>)?.['status']) return;
                    setTimeout(() => {
                        this.syncProgress = null;
                        this.syncSegments = [];
                        this.showSyncBar = false;
                        this._progressTickCount = 0;
                    }, 500);
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

    async clearCache() {
        this.clearCacheStatus = 'loading';
        try {
            const res = await fetch('/api/system/clear-cache', { method: 'POST' });
            const data = await res.json() as Record<string, unknown>;
            this.clearCacheStatus = data['ok'] ? 'ok' : 'error';
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
        this.initialized = false;
        this.authState = 'checking';
        const maxRetries = 60;
        for (let i = 0; i < maxRetries; i++) {
            await new Promise(r => setTimeout(r, 1000));
            try {
                const res = await fetch('/api/auth/me');
                if (res.ok) { await this.init(); return; }
            } catch {
                // still down — keep polling
            }
        }
        this.restartStatus = 'error';
        this.initialized = true;
        this.authState = 'login';
    },

    switchConfigTab(tab: string) {
        this.configTab = tab;
        if (tab === 'diagnostics') void this.loadDiagnostics();
        else this.closeLogStream();
    },

    async loadDiagnostics() {
        try {
            const res = await fetch('/api/diagnostics/logs?limit=200');
            if (res.ok) {
                this.logEntries = await res.json() as unknown[];
                this.$nextTick(() => {
                    const el = document.getElementById('log-container');
                    if (el) el.scrollTop = el.scrollHeight;
                });
            }
        } catch { /* ignore */ }
        try {
            const res = await fetch('/api/diagnostics/status');
            if (res.ok) {
                const data = await res.json() as Record<string, unknown>;
                this.appVersion = (data['version'] as string | undefined) ?? null;
                this.syncHistory = (data['lastSync'] as Record<string, unknown> | undefined) ?? null;
            }
        } catch { /* ignore */ }
        this.openLogStream();
    },

    openLogStream() {
        this.closeLogStream();
        const es = new (window as Window & { ReconnectingEventSource: new (url: string, opts: Record<string, unknown>) => EventSource }).ReconnectingEventSource(
            '/api/diagnostics/logs/stream', { max_retry_time: 60000 }
        );
        es.onmessage = (e: MessageEvent) => {
            try {
                const entry = JSON.parse(e.data as string) as unknown;
                const el = document.getElementById('log-container');
                const nearBottom = !el || (el.scrollHeight - el.scrollTop - el.clientHeight < 80);
                this.logEntries = [...this.logEntries.slice(-499), entry];
                if (nearBottom) {
                    this.$nextTick(() => {
                        const el2 = document.getElementById('log-container');
                        if (el2) el2.scrollTop = el2.scrollHeight;
                    });
                }
            } catch { /* ignore */ }
        };
        es.onerror = () => { console.warn("[WatchBack] Log SSE error"); };
        this.logSse = es as unknown as { close(): void };
    },

    closeLogStream() {
        if (this.logSse) { this.logSse.close(); this.logSse = null; }
    },

    async clearLogs() {
        await fetch('/api/diagnostics/logs', { method: 'DELETE' });
        this.logEntries = [];
    },

    async copyLogs() {
        const entries = (this.filteredLogs as unknown[]).slice(-200);
        const ver = this.appVersion ? ` v${this.appVersion}` : '';

        const meta = [
            `Version:   ${this.appVersion ?? 'unknown'}`,
            `${this.t('Diagnostics_Captured')} ${new Date().toUTCString()}`,
            `${this.t('Diagnostics_Browser')} ${navigator.userAgent}`,
            `Theme:     ${this.theme}`,
            `${this.t('Diagnostics_Filter')} ${this.logLevel} (${this.t('Diagnostics_Entries', entries.length, this.logEntries.length)})`,
        ];
        if (this.syncHistory) {
            meta.push(`${this.t('Diagnostics_Sync')} ${this.syncHistory['status'] as string}${this.syncHistory['title'] ? ` — ${this.syncHistory['title'] as string}` : ''}`);
            const sources = (this.syncHistory['sources'] as { source: string; thoughtCount: number }[] | undefined) ?? [];
            if (sources.length > 0) {
                const providerSummary = sources.map(s => `${s.source} (${s.thoughtCount})`).join(', ');
                meta.push(`${this.t('Diagnostics_Providers')} ${providerSummary}`);
            }
        }

        const header = `=== WatchBack${ver} ${this.t('Diagnostics_DiagnosticLog')} ===`;
        const separator = '='.repeat(header.length);
        const logLines = (entries as { timestamp: string; level: string; category?: string; message: string; exceptionText?: string }[]).map(e => {
            const time = this.formatLogTime(e.timestamp);
            const lvl  = this.logLevelAbbr(e.level).padEnd(3);
            const cat  = (e.category || '').padEnd(24);
            const line = `${time} ${lvl} ${cat} ${e.message}`;
            return e.exceptionText ? `${line}\n              ${e.exceptionText}` : line;
        });

        const sections = [
            header,
            meta.join('\n'),
            separator,
            ...(logLines.length > 0 ? logLines : [this.t('Diagnostics_NoEntriesMatchingFilter')]),
        ];
        const text = sections.join('\n');
        try {
            await navigator.clipboard.writeText(text);
            this.copyLogsStatus = 'copied';
        } catch {
            this.copyLogsStatus = 'error';
        }
        setTimeout(() => { this.copyLogsStatus = null; }, 2000);
    },
};

export default systemMethods;

import type { AppData, SyncData, LogEntry, SyncHistoryStatus, SyncHistoryEntry } from '../types';
import { uiLog } from '../utils/uiLogger';

const systemMethods: Record<string, unknown> & ThisType<AppData> = {
    setupSSE() {
        // Bar visibility is driven by intermediate-progress events: the backend
        // emits 0% at the start and 100% at the end of every cycle, but it only
        // emits intermediate (0 < completed < total) events when providers
        // actually do work. Cached cycles report zero ticks, so they skip the
        // intermediates and never arm the show timer — regardless of wall-time.
        const SHOW_DELAY_MS = 250;
        const HIDE_DELAY_MS = 300;
        const PULSE_DURATION_MS = 600;
        let showBarTimer: ReturnType<typeof setTimeout> | null = null;
        let clearTimer: ReturnType<typeof setTimeout> | null = null;

        const episodeKey = (d: SyncData | null | undefined): string => {
            const m = d?.metadata;
            if (!m) return d?.title ?? '';
            return `${m.title ?? d?.title ?? ''}|${m.seasonNumber ?? ''}|${m.episodeNumber ?? ''}`;
        };

        const resetBarState = () => {
            this.syncProgress = null;
            this.syncSegments = [];
            this.showSyncBar = false;
        };

        const cancelShowTimer = () => {
            if (showBarTimer !== null) { clearTimeout(showBarTimer); showBarTimer = null; }
        };
        const cancelClearTimer = () => {
            if (clearTimer !== null) { clearTimeout(clearTimer); clearTimer = null; }
        };

        uiLog("sse.open", "Sync SSE connecting", undefined, "Information");
        const es = new (window as unknown as Window & { ReconnectingEventSource: new (url: string, opts: Record<string, unknown>) => EventSource }).ReconnectingEventSource(
            '/api/sync/stream', { max_retry_time: 60000 }
        );
        es.onmessage = (e: MessageEvent) => {
            if (!e.data) return;
            try {
                const data = JSON.parse((e.data as string).replace(/^data: /, '')) as Record<string, unknown>;
                if (data['completed'] !== undefined) {
                    const completed = data['completed'] as number;
                    const total = data['total'] as number;
                    // Cancel a pending clear from the prior cycle so a quick
                    // follow-on sync is treated as fresh.
                    const wasClearPending = clearTimer !== null;
                    cancelClearTimer();
                    if (wasClearPending && !this.showSyncBar) {
                        this.syncProgress = null;
                    }
                    // Only arm the show timer for intermediate progress —
                    // cached cycles skip straight from 0% to 100% with no ticks
                    // between and therefore never flash the bar.
                    const isIntermediate = completed > 0 && completed < total;
                    uiLog("sse.progress", "Sync progress event", { completed, total, isIntermediate, showBarTimerArmed: showBarTimer !== null, showSyncBar: this.showSyncBar });
                    if (isIntermediate && showBarTimer === null && !this.showSyncBar) {
                        uiLog("sse.bar.arm", "Progress bar show timer armed", { delayMs: SHOW_DELAY_MS });
                        showBarTimer = setTimeout(() => {
                            uiLog("sse.bar.show", "Progress bar shown", undefined, "Information");
                            this.showSyncBar = true;
                            showBarTimer = null;
                        }, SHOW_DELAY_MS);
                    }
                    this.syncProgress = { completed, total };
                    if (data['providers']) this.syncSegments = data['providers'] as unknown[];
                    return;
                }
                if (!data['status']) return;

                const newData = data as unknown as SyncData;
                const prevKey = episodeKey(this.data);
                const newKey = episodeKey(newData);
                const episodeChanged = this.data !== null && prevKey !== newKey;
                const barWasVisible = this.showSyncBar;

                console.debug("[WatchBack] SSE update:", data);
                uiLog("sse.status", "Sync status received", {
                    status: newData.status,
                    title: newData.title,
                    episodeChanged,
                    barWasVisible,
                    showBarTimerArmed: showBarTimer !== null,
                }, "Information");
                this.data = newData;

                // The cycle is done — cancel any pending show timer so late
                // cached cycles don't flash the bar after status arrives.
                cancelShowTimer();
                cancelClearTimer();

                if (episodeChanged && !barWasVisible) {
                    // Episode reloaded without a visible loading indicator — pulse
                    // the bar briefly so the user sees that something changed.
                    uiLog("sse.bar.pulse", "Pulsing bar for silent episode change", { durationMs: PULSE_DURATION_MS });
                    this.syncProgress = this.syncProgress ?? { completed: 1, total: 1 };
                    this.showSyncBar = true;
                    clearTimer = setTimeout(() => {
                        resetBarState();
                        clearTimer = null;
                    }, PULSE_DURATION_MS);
                } else {
                    uiLog("sse.bar.hide", "Progress bar hide timer armed", { delayMs: HIDE_DELAY_MS, barWasVisible });
                    clearTimer = setTimeout(() => {
                        resetBarState();
                        clearTimer = null;
                    }, HIDE_DELAY_MS);
                }
            } catch (err) {
                console.debug("[WatchBack] SSE parse:", err);
                uiLog("sse.parseError", "SSE message parse failed", { error: String(err) }, "Warning");
            }
        };
        es.onerror = () => {
            console.warn("[WatchBack] SSE connection lost");
            uiLog("sse.error", "Sync SSE connection lost or reconnecting", undefined, "Warning");
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
        uiLog("system.restart", "Server restart triggered", undefined, "Information");
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
                if (res.ok) {
                    uiLog("system.restart.online", "Server back online after restart", { attempt: i + 1 }, "Information");
                    await this.init();
                    return;
                }
            } catch {
                // still down — keep polling
            }
        }
        uiLog("system.restart.timeout", "Server did not come back online after restart", { maxRetries }, "Error");
        this.restartStatus = 'error';
        this.initialized = true;
        this.authState = 'login';
    },

    switchConfigTab(tab: string) {
        this.configTab = tab;
        if (tab === 'diagnostics') void this.loadDiagnostics();
        else if (tab === 'mappings') void this.fetchMappings();
        else if (tab === 'apikeys') void this.loadApiKeys();
        else this.closeLogStream();
    },

    async loadDiagnostics() {
        try {
            const res = await fetch('/api/diagnostics/logs?limit=500');
            if (res.ok) {
                this.logEntries = await res.json() as LogEntry[];
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
                this.syncHistory = (data['lastSync'] as SyncHistoryStatus | undefined) ?? null;
            }
        } catch { /* ignore */ }
        this.openLogStream();
        void this.loadSyncHistory();
    },

    openLogStream() {
        this.closeLogStream();
        const es = new (window as unknown as Window & { ReconnectingEventSource: new (url: string, opts: Record<string, unknown>) => EventSource }).ReconnectingEventSource(
            '/api/diagnostics/logs/stream', { max_retry_time: 60000 }
        );
        es.onmessage = (e: MessageEvent) => {
            try {
                const entry = JSON.parse(e.data as string) as unknown;
                const el = document.getElementById('log-container');
                const nearBottom = !el || (el.scrollHeight - el.scrollTop - el.clientHeight < 80);
                this.logEntries = [...this.logEntries.slice(-499), entry as LogEntry];
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

    async loadSyncHistory() {
        try {
            const res = await fetch('/api/diagnostics/sync-history?limit=100');
            if (res.ok) this.syncHistoryEntries = await res.json() as SyncHistoryEntry[];
        } catch { /* ignore */ }
    },

    async clearSyncHistory() {
        await fetch('/api/diagnostics/sync-history', { method: 'DELETE' });
        this.syncHistoryEntries = [];
    },

    async copyLogs() {
        const ver = this.appVersion ? ` v${this.appVersion}` : '';
        const meta = [
            `Version:   ${this.appVersion ?? 'unknown'}`,
            `${this.t('Diagnostics_Captured')} ${new Date().toUTCString()}`,
            `${this.t('Diagnostics_Browser')} ${navigator.userAgent}`,
            `Theme:     ${this.theme}`,
        ];
        if (this.syncHistory) {
            meta.push(`${this.t('Diagnostics_Sync')} ${this.syncHistory.status}${this.syncHistory.title ? ` — ${this.syncHistory.title}` : ''}`);
            const sources = this.syncHistory.sources ?? [];
            if (sources.length > 0) {
                const providerSummary = sources.map(s => `${s.source} (${s.thoughtCount})`).join(', ');
                meta.push(`${this.t('Diagnostics_Providers')} ${providerSummary}`);
            }
        }
        const header = `=== WatchBack${ver} ${this.t('Diagnostics_DiagnosticLog')} ===`;
        const separator = '='.repeat(header.length);
        try {
            const res = await fetch('/api/diagnostics/logs/raw');
            const logText = res.ok ? await res.text() : '';
            const text = [header, meta.join('\n'), separator, logText].join('\n');
            await navigator.clipboard.writeText(text);
            this.copyLogsStatus = 'copied';
        } catch {
            this.copyLogsStatus = 'error';
        }
        setTimeout(() => { this.copyLogsStatus = null; }, 2000);
    },
};

export default systemMethods;

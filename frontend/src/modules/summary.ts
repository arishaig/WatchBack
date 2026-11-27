import type { AppData, ConfigData } from '../types';

/** How long to wait before asking again for a summary the server is still writing. */
const PENDING_RETRY_MS = 3000;
/** The server pauses this long after a failure; asking sooner would only be told "failed" again. */
const FAILED_RETRY_MS = 30000;

type SummaryMode = 'hidden' | 'collapsed' | 'open';

const summaryMethods: Record<string, unknown> & ThisType<AppData> = {
    /** The mode in effect. The server resolves an unset one: collapsed once Ollama is set up, hidden before. */
    summaryMode(): SummaryMode {
        const mode = (this.configData as ConfigData | null)?.preferences?.summaryMode;
        return mode === 'collapsed' || mode === 'open' ? mode : 'hidden';
    },

    /** Whether the summary block is on the page at all. */
    summaryShown(): boolean {
        if (this.summaryMode() === 'hidden' || !this.data || this.data['status'] !== 'Watching') return false;
        const status = this.summary?.status;
        return !!status && status !== 'hidden' && status !== 'empty';
    },

    summaryText(): string {
        const summary = this.summary;
        if (!summary) return '';
        switch (summary.status) {
            case 'ready': return summary.text ?? '';
            case 'pending': return this.t('Summary_Writing');
            case 'unavailable': return this.t('Summary_NeedsModel');
            case 'failed': return this.t('Summary_Failed');
            default: return '';
        }
    },

    /** The quiet note on the header while it is folded: that one is being written, or what it is based on. */
    summaryHint(): string {
        const summary = this.summary;
        if (!summary) return '';
        if (summary.status === 'pending') return this.t('Summary_Writing');
        if (summary.status === 'ready') {
            return this.t(summary.compared ? 'Summary_BasisCompared' : 'Summary_Basis', summary.commentCount);
        }
        return '';
    },

    toggleSummary() {
        this.summaryExpanded = !this.summaryExpanded;
    },

    /**
     * Asks the server for the summary of what is on screen. Quiet when nothing changed since the last answer,
     * and never while hidden: the server only writes one for a viewer who can see it.
     */
    async refreshSummary(force = false) {
        if (this._summaryTimer) {
            clearTimeout(this._summaryTimer);
            this._summaryTimer = null;
        }
        if (this.summaryMode() === 'hidden' || !this.data || this.data['status'] !== 'Watching') {
            this.summary = null;
            this._summaryKey = '';
            return;
        }

        const thoughts = ((this.mode === 'time' ? this.data['timeMachineThoughts'] : this.data['allThoughts']) ?? []) as
            unknown as { id: string }[];
        // New comments change the summary; the 5-second poll with nothing new must not ask again.
        const key = [this.data['title'], this.mode, thoughts.length, thoughts[0]?.id, thoughts[thoughts.length - 1]?.id].join('|');
        const status = this.summary?.status;
        if (!force && key === this._summaryKey) {
            if (status === 'ready' || status === 'unavailable') return;
            if (status === 'failed' && Date.now() - this._summaryAt < FAILED_RETRY_MS) return;
        }

        const ticket = ++this._summaryTicket;
        this._summaryKey = key;
        this._summaryAt = Date.now();
        try {
            const query = this.viewerQuery();
            const url = '/api/summary' + (query ? query + '&' : '?') + 'timeMachine=' + (this.mode === 'time');
            const res = await fetch(url);
            if (ticket !== this._summaryTicket) return;
            if (!res.ok) throw new Error(String(res.status));
            this.summary = await res.json() as AppData['summary'];
            if (ticket !== this._summaryTicket) return;
            if (this.summary?.status === 'pending') {
                this._summaryTimer = setTimeout(() => void this.refreshSummary(true), PENDING_RETRY_MS);
            }
        } catch {
            if (ticket !== this._summaryTicket) return;
            this.summary = { status: 'failed', text: null, commentCount: 0 };
        }
    },

    /** Saves the mode, folds or unfolds to match it, and fetches the summary if it is now wanted. */
    async setSummaryMode(mode: SummaryMode) {
        const prefs = (this.configData as ConfigData | null)?.preferences;
        const previous = prefs?.summaryMode;
        this.prefEdits = { ...this.prefEdits, summaryMode: mode };
        if (!await this.setPreference('WatchBack__SummaryMode', mode)) {
            this.prefEdits = { ...this.prefEdits, summaryMode: previous };
            return;
        }
        if (prefs) prefs.summaryMode = mode;
        this.summaryExpanded = mode === 'open';
        this._summaryKey = '';
        await this.refreshSummary(true);
    },

    /** Whether the language model that writes summaries is set up (the Ollama connection). */
    summaryModelConfigured(): boolean {
        const integrations = (this.configData as ConfigData | null)?.integrations ?? {};
        return !!integrations['ollama']?.configured;
    },
};

export default summaryMethods;

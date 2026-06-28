import type { AppData, MediaSearchResult, SeasonSummary, EpisodeResult } from '../types';
import { uiLog } from '../utils/uiLogger';

const syncMethods: Record<string, unknown> & ThisType<AppData> = {
    async sync() {
        console.debug("[WatchBack] Triggering sync...");
        uiLog("sync.trigger", "Sync trigger sent", undefined, "Information");
        this.data = null;
        try {
            const res = await fetch('/api/sync/trigger', { method: 'POST' });
            uiLog("sync.trigger.response", "Sync trigger response", { status: res.status });
        } catch (e) {
            console.error("[WatchBack] Trigger failed:", e);
            uiLog("sync.trigger.error", "Sync trigger failed", { error: String(e) }, "Error");
            this.showError(this.t('Auth_ConnectionFailed'));
        }
    },

    async searchMedia() {
        const q = this.searchQuery.trim();
        if (!q) return;
        uiLog("search.media", "Media search started", { query: q });
        this.searchLoading = true;
        this.searchError = null;
        this.searchResults = [];
        this.searchDrilldown = null;
        this.drilldownSeason = null;
        this.drilldownEpisodes = [];
        try {
            const res = await fetch('/api/search?q=' + encodeURIComponent(q));
            if (res.ok) {
                this.searchResults = await res.json() as MediaSearchResult[];
                uiLog("search.media.result", "Media search complete", { count: this.searchResults.length });
            } else if (res.status === 503) {
                uiLog("search.media.notConfigured", "Search not configured (503)", undefined, "Warning");
                this.searchError = this.t('Dashboard_OmdbNotConfigured');
            } else {
                uiLog("search.media.error", "Media search error", { status: res.status }, "Warning");
                this.searchError = this.t('Dashboard_SearchFailed');
            }
        } catch (e) {
            uiLog("search.media.networkError", "Media search network error", { error: String(e) }, "Error");
            this.searchError = this.t('Dashboard_ConnectionFailed');
        }
        this.searchLoading = false;
    },

    async selectSearchResult(result: MediaSearchResult) {
        if (result.type === 'episode') {
            const epMatch = result.title.match(/^(.+?) — (.+?) \(S(\d+)E(\d+)\)$/);
            if (epMatch) {
                await this.setManualWatchState({
                    title: epMatch[1],
                    episodeTitle: epMatch[2],
                    seasonNumber: parseInt(epMatch[3], 10),
                    episodeNumber: parseInt(epMatch[4], 10),
                    releaseDate: result.releaseDate ?? null,
                    externalIds: { imdb: result.imdbId },
                });
                return;
            }
        }
        if (result.type === 'movie') {
            await this.setManualWatchState({
                title: result.title,
                releaseDate: result.releaseDate ?? (result.year ? result.year + '-01-01T00:00:00Z' : null),
                externalIds: { imdb: result.imdbId },
            });
            return;
        }
        this.searchDrilldown = { imdbId: result.imdbId, title: result.title, poster: result.posterUrl, seasons: [] };
        this.drilldownSeason = null;
        this.drilldownEpisodes = [];
        this.drilldownLoading = true;
        try {
            const res = await fetch('/api/search/show/' + encodeURIComponent(result.imdbId) + '/seasons');
            if (res.ok && this.searchDrilldown) this.searchDrilldown.seasons = await res.json() as SeasonSummary[];
        } catch {
            this.searchError = this.t('Dashboard_LoadSeasonsFailed');
        }
        this.drilldownLoading = false;
    },

    async selectSeason(season: SeasonSummary) {
        this.drilldownSeason = season;
        this.drilldownEpisodes = [];
        this.drilldownLoading = true;
        try {
            const res = await fetch(
                '/api/search/show/' + encodeURIComponent(this.searchDrilldown?.imdbId ?? '') +
                '/season/' + String(season.seasonNumber) + '/episodes');
            if (res.ok) this.drilldownEpisodes = await res.json() as EpisodeResult[];
        } catch {
            this.searchError = this.t('Dashboard_LoadEpisodesFailed');
        }
        this.drilldownLoading = false;
    },

    async selectEpisode(ep: EpisodeResult) {
        await this.setManualWatchState({
            title: this.searchDrilldown?.title,
            episodeTitle: ep.title,
            seasonNumber: ep.seasonNumber,
            episodeNumber: ep.episodeNumber,
            releaseDate: ep.airDate,
            externalIds: Object.assign(
                { imdb: this.searchDrilldown?.imdbId },
                ep.imdbId ? { imdbEpisode: ep.imdbId } : {}
            ),
        });
    },

    async setManualWatchState(context: Record<string, unknown>) {
        uiLog("sync.manualState.set", "Setting manual watch state", { title: context['title'] }, "Information");
        try {
            const res = await fetch('/api/watchstate/manual', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    title: context['title'],
                    releaseDate: context['releaseDate'] ?? null,
                    episodeTitle: context['episodeTitle'] ?? null,
                    seasonNumber: context['seasonNumber'] ?? null,
                    episodeNumber: context['episodeNumber'] ?? null,
                    externalIds: context['externalIds'] ?? null,
                }),
            });
            if (res.ok) {
                uiLog("sync.manualState.ok", "Manual watch state set", { title: context['title'] }, "Information");
                this.searchQuery = '';
                this.searchResults = [];
                this.searchDrilldown = null;
                this.drilldownSeason = null;
                this.drilldownEpisodes = [];
                await this.sync();
            } else {
                uiLog("sync.manualState.error", "Set manual watch state failed", { status: res.status }, "Warning");
                this.searchError = this.t('Dashboard_SetWatchStateFailed');
            }
        } catch (e) {
            uiLog("sync.manualState.networkError", "Set manual watch state network error", { error: String(e) }, "Error");
            this.searchError = this.t('Dashboard_ConnectionFailed');
        }
    },

    async clearManualWatchState() {
        uiLog("sync.manualState.clear", "Clearing manual watch state", undefined, "Information");
        try {
            await fetch('/api/watchstate/manual', { method: 'DELETE' });
            await this.sync();
        } catch (e) {
            uiLog("sync.manualState.clearError", "Clear manual watch state failed", { error: String(e) }, "Error");
            this.showError(this.t('Dashboard_ClearWatchStateFailed'));
        }
    },
};

export default syncMethods;

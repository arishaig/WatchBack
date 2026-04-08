import type { AppData } from '../types';

const syncMethods: Record<string, unknown> & ThisType<AppData> = {
    async sync() {
        console.debug("[WatchBack] Triggering sync...");
        this.data = null;
        try {
            await fetch('/api/sync/trigger', { method: 'POST' });
        } catch (e) {
            console.error("[WatchBack] Trigger failed:", e);
            this.showError(this.t('Auth_ConnectionFailed'));
        }
    },

    async searchMedia() {
        const q = this.searchQuery.trim();
        if (!q) return;
        this.searchLoading = true;
        this.searchError = null;
        this.searchResults = [];
        this.searchDrilldown = null;
        this.drilldownSeason = null;
        this.drilldownEpisodes = [];
        try {
            const res = await fetch('/api/search?q=' + encodeURIComponent(q));
            if (res.ok) {
                this.searchResults = await res.json() as unknown[];
            } else if (res.status === 503) {
                this.searchError = this.t('Dashboard_OmdbNotConfigured');
            } else {
                this.searchError = this.t('Dashboard_SearchFailed');
            }
        } catch {
            this.searchError = this.t('Dashboard_ConnectionFailed');
        }
        this.searchLoading = false;
    },

    async selectSearchResult(result: Record<string, unknown>) {
        if (result['type'] === 'episode') {
            const epMatch = (result['title'] as string).match(/^(.+?) — (.+?) \(S(\d+)E(\d+)\)$/);
            if (epMatch) {
                await this.setManualWatchState({
                    title: epMatch[1],
                    episodeTitle: epMatch[2],
                    seasonNumber: parseInt(epMatch[3], 10),
                    episodeNumber: parseInt(epMatch[4], 10),
                    releaseDate: result['releaseDate'] ?? null,
                    externalIds: { imdb: result['imdbId'] },
                });
                return;
            }
        }
        if (result['type'] === 'movie') {
            await this.setManualWatchState({
                title: result['title'],
                releaseDate: result['releaseDate'] ?? (result['year'] ? result['year'] + '-01-01T00:00:00Z' : null),
                externalIds: { imdb: result['imdbId'] },
            });
            return;
        }
        this.searchDrilldown = { imdbId: result['imdbId'], title: result['title'], poster: result['posterUrl'], seasons: [] };
        this.drilldownSeason = null;
        this.drilldownEpisodes = [];
        this.drilldownLoading = true;
        try {
            const res = await fetch('/api/search/show/' + encodeURIComponent(result['imdbId'] as string) + '/seasons');
            if (res.ok) this.searchDrilldown['seasons'] = await res.json() as unknown[];
        } catch {
            this.searchError = this.t('Dashboard_LoadSeasonsFailed');
        }
        this.drilldownLoading = false;
    },

    async selectSeason(season: Record<string, unknown>) {
        this.drilldownSeason = season;
        this.drilldownEpisodes = [];
        this.drilldownLoading = true;
        try {
            const res = await fetch(
                '/api/search/show/' + encodeURIComponent(this.searchDrilldown?.['imdbId'] as string) +
                '/season/' + String(season['seasonNumber']) + '/episodes');
            if (res.ok) this.drilldownEpisodes = await res.json() as unknown[];
        } catch {
            this.searchError = this.t('Dashboard_LoadEpisodesFailed');
        }
        this.drilldownLoading = false;
    },

    async selectEpisode(ep: Record<string, unknown>) {
        await this.setManualWatchState({
            title: this.searchDrilldown?.['title'],
            episodeTitle: ep['title'],
            seasonNumber: ep['seasonNumber'],
            episodeNumber: ep['episodeNumber'],
            releaseDate: ep['airDate'],
            externalIds: Object.assign(
                { imdb: this.searchDrilldown?.['imdbId'] },
                ep['imdbId'] ? { imdbEpisode: ep['imdbId'] } : {}
            ),
        });
    },

    async setManualWatchState(context: Record<string, unknown>) {
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
                this.searchQuery = '';
                this.searchResults = [];
                this.searchDrilldown = null;
                this.drilldownSeason = null;
                this.drilldownEpisodes = [];
                await this.sync();
            } else {
                this.searchError = this.t('Dashboard_SetWatchStateFailed');
            }
        } catch {
            this.searchError = this.t('Dashboard_ConnectionFailed');
        }
    },

    async clearManualWatchState() {
        try {
            await fetch('/api/watchstate/manual', { method: 'DELETE' });
            await this.sync();
        } catch {
            this.showError(this.t('Dashboard_ClearWatchStateFailed'));
        }
    },
};

export default syncMethods;

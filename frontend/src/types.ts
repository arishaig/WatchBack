export type SaveStatus = 'saving' | 'saved' | 'error' | null;
export type TestStatus = 'testing' | 'ok' | 'error' | 'warn' | null;

// ── API response shapes ────────────────────────────────────────────────────

export interface Thought {
    source: string;
    brandColor?: string;
    brandLogoSvg?: string;
    postTitle?: string;
    postUrl?: string;
    postBody?: string;
    sentiment?: number | null;
}

export interface SyncMetadata {
    title?: string | null;
    releaseDate?: string | null;
    episodeTitle?: string | null;
    seasonNumber?: number | null;
    episodeNumber?: number | null;
}

export interface ProviderRating {
    source: string;
    value: string;
    logoSvg?: string;
    brandColor?: string;
}

export interface SyncData {
    status: string;
    title?: string | null;
    allThoughts: Thought[];
    timeMachineThoughts: Thought[];
    timeMachineDays?: number;
    metadata?: SyncMetadata;
    watchProvider?: string | null;
    suppressedProvider?: string | null;
    suppressedTitle?: string | null;
    ratings?: ProviderRating[] | null;
    ratingsProvider?: string | null;
    sourceResults?: unknown[];
}

export interface WatchProviderOption {
    value: string;
    label: string;
    requiresManualInput?: boolean;
}

export interface ConfigPreferences {
    timeMachineDays: number;
    watchProvider: string;
    watchProviders: WatchProviderOption[];
    searchConfigured: boolean;
    searchEngine: string;
    customSearchUrl: string;
    segmentedProgressBar: boolean;
    enableSentimentAnalysis: boolean;
    envValues: Record<string, string>;
    overrides: Record<string, boolean>;
}

export interface ConfigIntegration {
    name: string;
    logoSvg?: string;
    brandColor?: string;
    fields: { key: string; type: string; value?: string; hasValue?: boolean }[];
    configured: boolean;
    providerTypes: string[];
    disabled: boolean;
}

export interface ConfigData {
    integrations: Record<string, ConfigIntegration>;
    preferences: ConfigPreferences;
}

export interface SyncHistorySource {
    source: string;
    thoughtCount: number;
}

export interface SyncHistoryEntry {
    status: string;
    title: string | null;
    timestamp: string;
    thoughtCount: number;
    durationMs: number | null;
}

export interface SyncHistoryStatus {
    status: string;
    title?: string;
    sources: SyncHistorySource[];
}

export interface LogEntry {
    timestamp: string;
    level: string;
    category?: string;
    message: string;
    exceptionText?: string;
}

export interface MediaSearchResult {
    type: 'movie' | 'series' | 'episode';
    title: string;
    year?: string;
    imdbId: string;
    posterUrl?: string;
    releaseDate?: string;
}

export interface SeasonSummary {
    seasonNumber: number;
}

export interface ShowDrilldown {
    imdbId: string;
    title: string;
    poster?: string;
    seasons: SeasonSummary[];
}

export interface EpisodeResult {
    title: string;
    seasonNumber: number;
    episodeNumber: number;
    airDate?: string;
    imdbId?: string;
}

export interface AuthMeResponse {
    authenticated: boolean;
    needsOnboarding?: boolean;
    forwardAuthHeader?: string;
    forwardAuthTrustedHost?: string;
    username?: string;
    authMethod?: string;
}

/**
 * Full shape of the Alpine.js application component.
 * Used as the ThisType<AppData> context for all module method objects.
 */
export interface AppData {
    // ── State ──────────────────────────────────────────────────────────────
    initialized: boolean;
    data: SyncData | null;
    error: string | null;
    errorTimer: ReturnType<typeof setTimeout> | null;
    isLoading: boolean;
    mode: string;
    sourceFilter: Set<string>;
    showConfig: boolean;
    configData: ConfigData | null;
    configEdits: Record<string, string>;
    saveStatus: Record<string, string>;
    saveAllStatus: SaveStatus;
    prefEdits: Record<string, unknown>;
    prefSaveStatus: SaveStatus;
    lightboxImg: string | null;
    groupByThread: boolean;
    theme: string;
    collapsedSections: Record<string, boolean>;
    testResults: Record<string, unknown>;
    lastTestResults: Record<string, unknown>;
    testAllStatus: TestStatus;
    authState: string;
    loginUsername: string;
    loginPassword: string;
    loginError: string | null;
    loginLoading: boolean;
    setupUsername: string;
    setupPassword: string;
    setupError: string | null;
    setupLoading: boolean;
    changePwCurrent: string;
    changePwNew: string;
    changePwConfirm: string;
    changePwError: string | null;
    changePwLoading: boolean;
    passwordStrength: Record<string, unknown> | null;
    currentUser: AuthMeResponse | null;
    restartStatus: string | null;
    resetPasswordStatus: string | null;
    syncProgress: { completed: number; total: number } | null;
    showSyncBar: boolean;
    syncSegments: unknown[];
    _progressTickCount: number;
    clearCacheStatus: 'loading' | 'ok' | 'error' | null;
    forwardAuthEnabled: boolean;
    forwardAuthHeaderEdit: string;
    forwardAuthTrustedHostEdit: string;
    forwardAuthSaveStatus: string | null;
    needsOnboarding: boolean;
    configTab: string;
    logEntries: LogEntry[];
    logLevel: string;
    logSse: { close(): void } | null;
    syncHistory: SyncHistoryStatus | null;
    syncHistoryEntries: SyncHistoryEntry[];
    appVersion: string | null;
    copyLogsStatus: 'copied' | 'error' | null;
    alwaysShowSearch: boolean;
    // Wizard & checklist
    wizardStep: number;
    wizardActive: boolean;
    wizardSelectedWatch: string | null;
    wizardSelectedComments: Set<string>;
    wizardSaving: boolean;
    newProviderKeys: string[];
    newProvidersActive: boolean;
    newProviderSelected: Set<string>;
    newProviderSaving: boolean;
    checklistDismissed: boolean;
    checklistAutoComplete: boolean;
    searchQuery: string;
    searchResults: MediaSearchResult[];
    searchLoading: boolean;
    searchError: string | null;
    searchDrilldown: ShowDrilldown | null;
    drilldownSeason: SeasonSummary | null;
    drilldownEpisodes: EpisodeResult[];
    drilldownLoading: boolean;
    locale: string;
    supportedLocales: string[];
    _stringsReady: boolean;
    themes: { id: string; label: string }[];
    mappingSources: { id: string; name: string; isBuiltIn: boolean; entries: { index: number; title: string | null; subreddits: string[] }[] }[];
    newMappingTitle: string;
    newMappingSubreddits: string;
    newMappingImdbId: string;
    mappingSearchResults: unknown[];
    mappingSearchLoading: boolean;
    mappingSaveStatus: SaveStatus;
    mappingImportJson: string;
    mappingImportName: string;
    mappingImportStatus: SaveStatus;
    mappingDropActive: boolean;
    mappingShareCopied: string;
    sentimentCategory: string;

    // ── Alpine internals ───────────────────────────────────────────────────
    $watch(prop: string, cb: (val: unknown) => void): void;
    $nextTick(cb: () => void): void;

    // ── Methods ────────────────────────────────────────────────────────────
    init(): Promise<void>;
    t(key: string, ...args: unknown[]): string;
    fetchThemes(): Promise<void>;
    checkAuth(): Promise<AuthMeResponse>;
    initApp(): Promise<void>;

    // Auth
    login(): Promise<void>;
    setupAccount(): Promise<void>;
    changePassword(): Promise<void>;
    evaluatePasswordStrength(password: string): Promise<void>;
    logout(): Promise<void>;
    resetPassword(): Promise<void>;
    saveForwardAuth(): Promise<void>;

    // Config
    _initConfigEdits(configData: ConfigData): void;
    saveConfig(integrationKey: string): Promise<void>;
    saveAllConfig(): Promise<void>;
    resetConfigKeys(keys: string[], refetch?: boolean): Promise<void>;
    toggleDisabled(key: string): Promise<void>;
    savePreferences(): Promise<void>;
    testIcon(service: string): { icon: string; cls: string; label: string };
    testService(service: string): Promise<void>;
    testAll(): Promise<void>;
    redditSearchUrl(title: string, season: number, episode: number): string;

    // Sync / search
    sync(): Promise<void>;
    searchMedia(): Promise<void>;
    selectSearchResult(result: MediaSearchResult): Promise<void>;
    selectSeason(season: SeasonSummary): Promise<void>;
    selectEpisode(ep: EpisodeResult): Promise<void>;
    setManualWatchState(context: Record<string, unknown>): Promise<void>;
    clearManualWatchState(): Promise<void>;

    // System
    setupSSE(): void;
    clearCache(): Promise<void>;
    restart(): Promise<void>;
    switchConfigTab(tab: string): void;
    loadDiagnostics(): Promise<void>;
    openLogStream(): void;
    closeLogStream(): void;
    clearLogs(): Promise<void>;
    copyLogs(): Promise<void>;
    loadSyncHistory(): Promise<void>;
    clearSyncHistory(): Promise<void>;

    // Wizard
    wizardNext(): Promise<void>;
    wizardBack(): void;
    wizardSkip(): void;
    wizardComplete(): void;
    wizardSelectWatch(key: string): void;
    wizardToggleComment(key: string): void;
    wizardSaveAndContinue(): Promise<void>;
    dismissChecklist(): void;
    openConfigForItem(type: string): void;
    newProviderToggle(key: string): void;
    dismissNewProviders(): void;
    saveAndDismissNewProviders(): Promise<void>;

    // Computed (getters)
    readonly hasWatchProvider: boolean;
    readonly hasCommentSource: boolean;
    readonly hasCompletedSync: boolean;
    readonly checklistAllComplete: boolean;
    readonly showChecklist: boolean;
    readonly filteredLogs: unknown[];
    readonly collapsedSyncHistory: { timestamp: string; status: string; title: string | null; count: number; thoughtCount: number; avgDurationMs: number | null }[];
    readonly showTimeMachine: boolean;
    readonly activeThoughts: unknown[];
    readonly hasThreadGroups: boolean;
    readonly timeMachineCount: number;
    readonly allThoughtsCount: number;
    readonly threadCount: number;
    readonly availableSources: { name: string; brandColor: string; brandLogoSvg: string }[];
    readonly renderGroups: unknown[];
    readonly groupedThoughts: unknown[];
    readonly showSearchBox: boolean;

    // Subreddit mappings
    handleMappingFileDrop(e: DragEvent): void;
    shareMappingSource(id: string, name: string): Promise<void>;
    fetchMappings(): Promise<void>;
    searchForMapping(): Promise<void>;
    selectMappingSearchResult(result: Record<string, unknown>): void;
    addLocalMapping(): Promise<void>;
    deleteLocalMapping(title: string): Promise<void>;
    importMappingSource(): Promise<void>;
    deleteMappingSource(id: string): Promise<void>;
    promoteMappingEntry(sourceId: string, index: number): Promise<void>;
    exportMappingSource(id: string): Promise<void>;

    // UI helpers
    showError(msg: string): void;
    applyTheme(mode: string): void;
    handleThreadToggle(): void;
    toggleSource(src: string): void;
    sourceActive(src: string): boolean;
    sourceCount(src: string): number;
    toggleSection(key: string): void;
    toggleAlwaysShowSearch(): void;
    sanitizeSvg(raw: string): string;
    renderMarkdown(src: string, spoilerLabel?: string): string;
    formatDate(iso: string): string;
    formatScore(n: number | null): string;
    countAllReplies(c: Record<string, unknown>): number;
    formatLogTime(iso: string): string;
    formatRelativeTime(iso: string): string;
    logLevelClass(level: string): string;
    logLevelAbbr(level: string): string;
}

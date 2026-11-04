export type SaveStatus = 'saving' | 'saved' | 'error' | null;
export type TestStatus = 'testing' | 'ok' | 'error' | 'warn' | null;

export interface WatchUser {
    id: string;
    name: string;
}

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
    liveChannel?: string | null;
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

export interface ConfigField {
    key: string;
    label?: string;
    type: string;
    placeholder?: string;
    value?: string;
    hasValue?: boolean;
    envValue?: string;
    isOverridden?: boolean;
    /** A credential, even when shown as plain text (Trakt's client ID). */
    secret?: boolean;
}

export interface ConfigIntegration {
    name: string;
    description?: string;
    logoSvg?: string;
    brandColor?: string;
    fields: ConfigField[];
    configured: boolean;
    providerTypes: string[];
    disabled: boolean;
}

/** Auto-save state of one setting. `tried` is the value that failed, so it isn't tested again unchanged. */
export interface FieldStatus {
    state: 'testing' | 'saving' | 'saved' | 'error';
    message?: string;
    canForce?: boolean;
    tried?: string;
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

export interface ApiKeyEntry {
    id: number;
    name: string;
    prefix: string;
    createdAt: string;
}

/** Who may change settings (GET /api/access). */
export interface Access {
    restricted: boolean;
    isAdmin: boolean;
    admins: string[];
    /** "group" when the sign-in proxy's groups decide, "watchUser" when the picked user does. */
    decidedBy: string;
    adminGroup: string | null;
}

export interface AuthMeResponse {
    authenticated: boolean;
    needsOnboarding?: boolean;
    containerName?: string;
    forwardAuthHeader?: string;
    forwardAuthTrustedHost?: string;
    forwardAuthGroupsHeader?: string;
    forwardAuthAdminGroup?: string;
    username?: string;
    authMethod?: string;
}

/**
 * Full shape of the Alpine.js application component.
 * Used as the ThisType<AppData> context for all module method objects.
 */
export interface AppData {
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
    prefEdits: Record<string, unknown>;
    prefSaveStatus: SaveStatus;
    fieldStatus: Record<string, FieldStatus>;
    openRow: string | null;
    lightboxImg: string | null;
    watchUsers: WatchUser[];
    watchUserId: string;
    access: Access;
    adminSaveError: string;
    clientId: string;
    showUserPicker: boolean;
    groupByThread: boolean;
    theme: string;
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
    // Memoization caches for expensive computed properties
    _cAtSrc: unknown;
    _cAtMode: string;
    _cAtFilter: Set<string>;
    _cAtSentCat: string;
    _cAtSentEnabled: boolean | undefined;
    _cAtResult: unknown[] | null;
    _cGtInput: unknown[] | null;
    _cGtResult: unknown[] | null;
    clearCacheStatus: 'loading' | 'ok' | 'error' | null;
    forwardAuthEnabled: boolean;
    forwardAuthHeaderEdit: string;
    forwardAuthTrustedHostEdit: string;
    forwardAuthGroupsHeaderEdit: string;
    forwardAuthAdminGroupEdit: string;
    forwardAuthError: string;
    forwardAuthSaveStatus: string | null;
    needsOnboarding: boolean;
    containerName: string;
    configTab: string;
    logEntries: LogEntry[];
    logLevel: string;
    logSse: { close(): void } | null;
    syncHistory: SyncHistoryStatus | null;
    syncHistoryEntries: SyncHistoryEntry[];
    appVersion: string | null;
    copyLogsStatus: 'copied' | 'error' | null;
    alwaysShowSearch: boolean;
    wizardStep: number;
    wizardActive: boolean;
    wizardSelectedWatch: string | null;
    wizardSelectedComments: Set<string>;
    wizardSaving: boolean;
    wizardAiMode: boolean;
    wizardAiKey: string | null;
    wizardAiStatus: 'idle' | 'creating' | 'error';
    wizardAiCopied: string | null;
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
    mappingFilter: string;
    sentimentCategory: string;
    apiKeys: ApiKeyEntry[];
    newApiKeyName: string;
    apiKeySaveStatus: SaveStatus;
    newlyGeneratedKey: string | null;
    apiKeyCopied: boolean;

    $watch(prop: string, cb: (val: unknown) => void): void;
    $nextTick(cb: () => void): void;

    init(): Promise<void>;
    t(key: string, ...args: unknown[]): string;
    fetchThemes(): Promise<void>;
    checkAuth(): Promise<AuthMeResponse>;
    initApp(): Promise<void>;

    login(): Promise<void>;
    setupAccount(): Promise<void>;
    changePassword(): Promise<void>;
    evaluatePasswordStrength(password: string): Promise<void>;
    logout(): Promise<void>;
    resetPassword(): Promise<void>;
    saveForwardAuth(): Promise<void>;

    _initConfigEdits(configData: ConfigData): void;
    saveConfig(integrationKey: string): Promise<void>;
    resetConfigKeys(keys: string[], refetch?: boolean): Promise<void>;
    toggleDisabled(key: string): Promise<void>;
    savePreferences(): Promise<void>;
    testIcon(service: string): { icon: string; cls: string; label: string };
    testService(service: string): Promise<void>;
    testAll(): Promise<void>;
    redditSearchUrl(title: string, season: number, episode: number): string;

    _integration(key: string): ConfigIntegration | undefined;
    _setFieldStatus(key: string, status: FieldStatus | null): void;
    _flashSaved(key: string): void;
    _postSettings(values: Record<string, string>): Promise<boolean>;
    commitField(integrationKey: string, fieldKey: string, force?: boolean): Promise<void>;
    useEnvValue(integrationKey: string, fieldKey: string): Promise<void>;
    setPreference(key: string, value: string): Promise<boolean>;
    activeWatchProvider(): string;
    chooseWatchProvider(value: string): Promise<void>;
    commitTimeMachine(): Promise<void>;
    setSearchEngine(engine: string): Promise<void>;
    commitCustomSearchUrl(): Promise<void>;
    setPreferenceSwitch(prefKey: 'segmentedProgressBar' | 'enableSentimentAnalysis', on: boolean): Promise<void>;
    rowStatus(key: string, asWatchSource?: boolean): { kind: 'ok' | 'warn' | 'err' | 'off'; label: string };
    rowSummary(key: string): string;
    toggleRow(id: string): void;
    fieldNote(field: ConfigField): string;
    prefNote(key: string): string;
    _statusNote(key: string): string;
    readonly settingsTabs: { id: string; label: string; sub: string }[];
    readonly settingsTab: string;
    readonly settingsGroups: { id: string; title: string; hint: string; rows: unknown[] }[];

    sync(): Promise<void>;
    searchMedia(): Promise<void>;
    selectSearchResult(result: MediaSearchResult): Promise<void>;
    selectSeason(season: SeasonSummary): Promise<void>;
    selectEpisode(ep: EpisodeResult): Promise<void>;
    setManualWatchState(context: Record<string, unknown>): Promise<void>;
    clearManualWatchState(): Promise<void>;

    setupSSE(): void;
    viewerQuery(): string;
    manualStateUrl(): string;
    loadWatchUsers(): Promise<void>;
    selectWatchUser(id: string): void;
    storeWatchUser(id: string): void;
    loadAccess(): Promise<void>;
    isAdminUser(id: string): boolean;
    toggleAdmin(id: string, on: boolean): Promise<void>;
    currentWatchUserName(): string;
    clearCache(): Promise<void>;
    restart(): Promise<void>;
    waitForServerRestart(): Promise<void>;
    switchConfigTab(tab: string): void;
    loadDiagnostics(): Promise<void>;
    openLogStream(): void;
    closeLogStream(): void;
    clearLogs(): Promise<void>;
    copyLogs(): Promise<void>;
    loadSyncHistory(): Promise<void>;
    clearSyncHistory(): Promise<void>;

    wizardNext(): Promise<void>;
    wizardBack(): void;
    wizardSkip(): void;
    wizardComplete(): void;
    wizardSelectWatch(key: string): void;
    wizardToggleComment(key: string): void;
    wizardSaveAndContinue(): Promise<void>;
    dismissChecklist(): void;
    openConfigForItem(type: string): void;
    openSettingsLink(key: string): boolean;
    wizardStartAi(): void;
    wizardCreateAiKey(): Promise<void>;
    wizardAiCopy(what: string, text: string): Promise<void>;
    wizardAiDone(): void;
    wizardAiBack(): void;
    readonly mcpUrl: string;
    readonly mcpClaudeCommand: string;
    handleSettingsHash(): void;
    newProviderToggle(key: string): void;
    dismissNewProviders(): void;
    saveAndDismissNewProviders(): Promise<void>;

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

    loadApiKeys(): Promise<void>;
    generateApiKey(): Promise<void>;
    revokeApiKey(id: number): Promise<void>;
    copyApiKey(key: string): Promise<void>;
    dismissGeneratedKey(): void;

    handleMappingFileDrop(e: DragEvent): void;
    localMappings(): AppData['mappingSources'][number]['entries'];
    otherMappingCount(): number;
    matchingMappings(): { source: AppData['mappingSources'][number]; entry: AppData['mappingSources'][number]['entries'][number] }[];
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

    showError(msg: string): void;
    applyTheme(mode: string): void;
    handleThreadToggle(): void;
    toggleSource(src: string): void;
    sourceActive(src: string): boolean;
    sourceCount(src: string): number;
    toggleAlwaysShowSearch(): void;
    sanitizeSvg(raw: string): string;
    renderMarkdown(src: string, spoilerLabel?: string): string;
    formatDate(iso: string): string;
    formatThoughtDate(iso: string): string;
    formatScore(n: number | null): string;
    countAllReplies(c: Record<string, unknown>): number;
    formatLogTime(iso: string): string;
    formatRelativeTime(iso: string): string;
    logLevelClass(level: string): string;
    logLevelAbbr(level: string): string;
}

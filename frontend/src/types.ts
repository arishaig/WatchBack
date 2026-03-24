export type SaveStatus = 'saving' | 'saved' | 'error' | null;
export type TestStatus = 'testing' | 'ok' | 'error' | 'warn' | null;

/**
 * Full shape of the Alpine.js application component.
 * Used as the ThisType<AppData> context for all module method objects.
 */
export interface AppData {
    // ── State ──────────────────────────────────────────────────────────────
    initialized: boolean;
    data: Record<string, unknown> | null;
    error: string | null;
    errorTimer: ReturnType<typeof setTimeout> | null;
    isLoading: boolean;
    mode: string;
    sourceFilter: Set<string>;
    showConfig: boolean;
    configData: Record<string, unknown> | null;
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
    changePwNew: string;
    changePwConfirm: string;
    changePwError: string | null;
    changePwLoading: boolean;
    passwordStrength: Record<string, unknown> | null;
    currentUser: Record<string, unknown> | null;
    restartStatus: string | null;
    resetPasswordStatus: string | null;
    syncProgress: { completed: number; total: number } | null;
    showSyncBar: boolean;
    syncSegments: unknown[];
    _progressTickCount: number;
    clearCacheStatus: 'loading' | 'ok' | 'error' | null;
    forwardAuthEnabled: boolean;
    forwardAuthHeaderEdit: string;
    forwardAuthSaveStatus: string | null;
    needsOnboarding: boolean;
    configTab: string;
    logEntries: unknown[];
    logLevel: string;
    logSse: { close(): void } | null;
    syncHistory: Record<string, unknown> | null;
    appVersion: string | null;
    copyLogsStatus: 'copied' | 'error' | null;
    alwaysShowSearch: boolean;
    // Wizard & checklist
    wizardStep: number;
    wizardActive: boolean;
    wizardSelectedWatch: string | null;
    wizardSelectedComments: Set<string>;
    wizardSaving: boolean;
    checklistDismissed: boolean;
    checklistAutoComplete: boolean;
    searchQuery: string;
    searchResults: unknown[];
    searchLoading: boolean;
    searchError: string | null;
    searchDrilldown: Record<string, unknown> | null;
    drilldownSeason: Record<string, unknown> | null;
    drilldownEpisodes: unknown[];
    drilldownLoading: boolean;
    locale: string;
    supportedLocales: string[];
    _stringsReady: boolean;
    themes: { id: string; label: string }[];

    // ── Alpine internals ───────────────────────────────────────────────────
    $watch(prop: string, cb: (val: unknown) => void): void;
    $nextTick(cb: () => void): void;

    // ── Methods ────────────────────────────────────────────────────────────
    init(): Promise<void>;
    t(key: string, ...args: unknown[]): string;
    fetchThemes(): Promise<void>;
    checkAuth(): Promise<Record<string, unknown>>;
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
    _initConfigEdits(configData: Record<string, unknown>): void;
    saveConfig(integrationKey: string): Promise<void>;
    saveAllConfig(): Promise<void>;
    resetConfigKeys(keys: string[]): Promise<void>;
    savePreferences(): Promise<void>;
    testIcon(service: string): { icon: string; cls: string; label: string };
    testService(service: string): Promise<void>;
    testAll(): Promise<void>;
    redditSearchUrl(title: string, season: number, episode: number): string;

    // Sync / search
    sync(): Promise<void>;
    searchMedia(): Promise<void>;
    selectSearchResult(result: Record<string, unknown>): Promise<void>;
    selectSeason(season: Record<string, unknown>): Promise<void>;
    selectEpisode(ep: Record<string, unknown>): Promise<void>;
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

    // Computed (getters)
    readonly hasWatchProvider: boolean;
    readonly hasCommentSource: boolean;
    readonly hasCompletedSync: boolean;
    readonly checklistAllComplete: boolean;
    readonly showChecklist: boolean;
    readonly filteredLogs: unknown[];
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

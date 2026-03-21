import Alpine from 'alpinejs';
import './strings';
import '../css/tw.css';
import '../css/app.css';

import type { AppData } from './types';
import authMethods from './modules/auth';
import configMethods from './modules/config';
import syncMethods from './modules/sync';
import systemMethods from './modules/system';
import computedDescriptors from './modules/computed';
import uiMethods from './modules/ui';

Alpine.data('app', (): AppData => {
    const data = {
        // ── State ──────────────────────────────────────────────────────────
        initialized: false,
        data: null,
        error: null,
        errorTimer: null,
        isLoading: false,
        mode: 'all',
        sourceFilter: new Set<string>(),
        showConfig: false,
        configData: null,
        configEdits: {},
        saveStatus: {},
        saveAllStatus: null,
        prefEdits: {},
        prefSaveStatus: null,
        lightboxImg: null,
        groupByThread: localStorage.getItem('wb_groupByThread') === 'true',
        theme: localStorage.getItem('wb_theme') || (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'),
        collapsedSections: (() => {
            try { return JSON.parse(localStorage.getItem('wb_collapsed_sections') || '{}') as Record<string, boolean>; }
            catch { return {} as Record<string, boolean>; }
        })(),
        testResults: {},
        lastTestResults: {},
        testAllStatus: null,
        authState: 'checking',
        loginUsername: '',
        loginPassword: '',
        loginError: null,
        loginLoading: false,
        setupUsername: '',
        setupPassword: '',
        setupError: null,
        setupLoading: false,
        changePwNew: '',
        changePwConfirm: '',
        changePwError: null,
        changePwLoading: false,
        passwordStrength: null,
        currentUser: null,
        restartStatus: null,
        resetPasswordStatus: null,
        syncProgress: null,
        showSyncBar: false,
        syncSegments: [],
        _progressTickCount: 0,
        clearCacheStatus: null,
        forwardAuthEnabled: false,
        forwardAuthHeaderEdit: '',
        forwardAuthSaveStatus: null,
        needsOnboarding: false,
        configTab: 'settings',
        logEntries: [],
        logLevel: 'Information',
        logSse: null,
        syncHistory: null,
        appVersion: null,
        copyLogsStatus: null,
        alwaysShowSearch: localStorage.getItem('wb_alwaysShowSearch') === 'true',
        searchQuery: '',
        searchResults: [],
        searchLoading: false,
        searchError: null,
        searchDrilldown: null,
        drilldownSeason: null,
        drilldownEpisodes: [],
        drilldownLoading: false,
        locale: localStorage.getItem('wb_locale') || (navigator.language || 'en').split('-')[0] || 'en',
        supportedLocales: [],
        _stringsReady: false,
        themes: [
            { id: 'dark', label: 'Dark' },
            { id: 'light', label: 'Light' },
            { id: 'solarized-dark', label: 'Solarized Dark' },
            { id: 'solarized-light', label: 'Solarized Light' },
            { id: 'monokai', label: 'Monokai' },
        ],

        // ── Methods from modules ───────────────────────────────────────────
        ...authMethods,
        ...configMethods,
        ...syncMethods,
        ...systemMethods,
        ...uiMethods,
    };

    // Merge computed getters preserving property descriptor semantics
    Object.defineProperties(data, computedDescriptors);

    return data as unknown as AppData;
});

(window as Window & { Alpine: typeof Alpine }).Alpine = Alpine;
Alpine.start();

import Alpine from 'alpinejs';
import './strings';
import '../css/tw.css';
import '../css/app.css';

import type { AppData } from './types';
import authMethods from './modules/auth';
import configMethods from './modules/config';
import syncMethods from './modules/sync';
import systemMethods from './modules/system';
import wizardMethods from './modules/wizard';
import computedDescriptors from './modules/computed';
import uiMethods from './modules/ui';
import { sanitizeSvg } from './utils/svg';
import { renderMarkdown } from './utils/markdown';

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
        wizardStep: 0,
        wizardActive: false,
        wizardSelectedWatch: null,
        wizardSelectedComments: new Set<string>(),
        wizardSaving: false,
        checklistDismissed: false,
        checklistAutoComplete: false,
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

        // ── Utility functions exposed to Alpine templates ──────────────────
        sanitizeSvg,
        renderMarkdown,

        // ── Methods from modules ───────────────────────────────────────────
        ...authMethods,
        ...configMethods,
        ...syncMethods,
        ...systemMethods,
        ...wizardMethods,
        ...uiMethods,
    };

    // Merge computed getters preserving property descriptor semantics
    Object.defineProperties(data, computedDescriptors);

    return data as unknown as AppData;
});

// Spoiler reveal — delegated from document so it works with dynamically rendered markdown
document.addEventListener('click', e => {
    const target = e.target as Element;
    const spoiler = target.closest('[data-wb-spoiler]');
    if (spoiler) spoiler.classList.add('revealed');
});
document.addEventListener('keydown', e => {
    const target = e.target as Element;
    if (e.key === 'Enter' && target.hasAttribute('data-wb-spoiler')) target.classList.add('revealed');
});

// ── Reusable provider-fields directive ────────────────────────────────────────
// Clones the <template id="provider-fields"> fragment and initialises Alpine
// directives on the clone so the same field+test-button markup can be used in
// the config panel and both wizard steps.
Alpine.directive('providerfields', (el, { expression }, { evaluate, effect, cleanup }) => {
    const opts = evaluate(expression) as { mode: string; prefix: string };
    const tpl = document.getElementById('provider-fields') as HTMLTemplateElement | null;
    if (!tpl) return;

    const clone = tpl.content.cloneNode(true) as DocumentFragment;
    const children = Array.from(clone.children);

    Alpine.addScopeToNode(el, {
        _pfMode: opts.mode,
        _pfPrefix: opts.prefix,
        get _pfTestDisabled() {
            const app = (Alpine as any).$data(el);
            const key = app.key;
            if (app.testResults[key]?.status === 'testing') return true;
            if (opts.mode === 'config') {
                return !app.integration.configured &&
                    !app.integration.fields.some((f: { key: string }) => app.configEdits[f.key]);
            }
            return false;
        },
    });

    el.append(clone);
    children.forEach(child => Alpine.initTree(child));

    cleanup(() => { children.forEach(c => c.remove()); });
});

(window as unknown as Window & { Alpine: typeof Alpine }).Alpine = Alpine;
Alpine.start();

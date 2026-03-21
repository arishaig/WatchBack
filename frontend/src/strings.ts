/**
 * Frontend localization — sets up window.t(), window.loadAllStrings(),
 * and the locale state globals used by the Alpine component.
 */

declare global {
    interface Window {
        _allStrings: Record<string, Record<string, string>>;
        _currentLocale: string;
        _supportedLocales: string[];
        loadAllStrings: () => Promise<void>;
        t: (key: string, ...args: unknown[]) => string;
    }
}

window._allStrings = {};
window._currentLocale = 'en';
window._supportedLocales = [];

window.loadAllStrings = async function () {
    try {
        const res = await fetch('/api/strings/all');
        if (!res.ok) throw new Error('Failed to load strings: ' + res.statusText);
        const data = await res.json() as { strings?: Record<string, Record<string, string>>; supportedLocales?: string[] };
        window._allStrings = data.strings ?? {};
        window._supportedLocales = data.supportedLocales ?? [];
    } catch (err) {
        console.error('Error loading strings:', err);
        window._allStrings = {};
        window._supportedLocales = [];
    }
};

window.t = function (key: string, ...args: unknown[]): string {
    const bundle = window._allStrings[window._currentLocale] ?? window._allStrings['en'] ?? {};
    const str = bundle[key];
    if (!str) {
        if (Object.keys(window._allStrings).length > 0) {
            console.warn('String not found: ' + key);
        }
        return key;
    }
    if (args.length === 0) return str;
    return str.replace(/{(\d+)}/g, (_m, i: string) => {
        const idx = parseInt(i, 10);
        return args[idx] !== undefined ? String(args[idx]) : `{${i}}`;
    });
};

export {};

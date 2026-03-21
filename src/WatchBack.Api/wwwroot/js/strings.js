/**
 * Frontend localization — plain IIFE (not an ES module).
 * Exposes window.t(), window.loadAllStrings(), and locale state globals.
 */
(function () {
    'use strict';

    window._allStrings = {};
    window._currentLocale = 'en';
    window._supportedLocales = [];

    /**
     * Fetch all locale bundles from the API in a single request.
     */
    window.loadAllStrings = async function () {
        try {
            const res = await fetch('/api/strings/all');
            if (!res.ok) throw new Error('Failed to load strings: ' + res.statusText);
            const data = await res.json();
            window._allStrings = data.strings || {};
            window._supportedLocales = data.supportedLocales || [];
        } catch (err) {
            console.error('Error loading strings:', err);
            window._allStrings = {};
            window._supportedLocales = [];
        }
    };

    /**
     * Look up a localised string by key, using the current global locale.
     * Supports {0}, {1}, … placeholder interpolation.
     * @param {string} key
     * @param {...*} args
     * @returns {string}
     */
    window.t = function (key) {
        var args = Array.prototype.slice.call(arguments, 1);
        var bundle = window._allStrings[window._currentLocale] || window._allStrings['en'] || {};
        var str = bundle[key];
        if (!str) {
            if (Object.keys(window._allStrings).length > 0) {
                console.warn('String not found: ' + key);
            }
            return key;
        }
        if (args.length === 0) return str;
        return str.replace(/{(\d+)}/g, function (m, i) {
            var idx = parseInt(i, 10);
            return args[idx] !== undefined ? String(args[idx]) : m;
        });
    };
})();

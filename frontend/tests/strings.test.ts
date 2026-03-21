import { describe, it, expect, beforeEach, vi } from 'vitest';
import '../src/strings';

// After importing strings.ts, window globals are set up
declare global {
    interface Window {
        _allStrings: Record<string, Record<string, string>>;
        _currentLocale: string;
        _supportedLocales: string[];
        loadAllStrings: () => Promise<void>;
        t: (key: string, ...args: unknown[]) => string;
    }
}

beforeEach(() => {
    window._allStrings = {};
    window._currentLocale = 'en';
    window._supportedLocales = [];
});

describe('window.t', () => {
    it('returns the key when no strings are loaded', () => {
        window._allStrings = {};
        expect(window.t('SomeKey')).toBe('SomeKey');
    });

    it('returns the translated string for the current locale', () => {
        window._allStrings = { en: { Hello: 'Hello, World!' } };
        window._currentLocale = 'en';
        expect(window.t('Hello')).toBe('Hello, World!');
    });

    it('falls back to en bundle when current locale is missing', () => {
        window._allStrings = { en: { Greeting: 'Hi there' } };
        window._currentLocale = 'de';
        expect(window.t('Greeting')).toBe('Hi there');
    });

    it('uses the current locale bundle when available', () => {
        window._allStrings = {
            en: { Greeting: 'Hello' },
            es: { Greeting: 'Hola' },
        };
        window._currentLocale = 'es';
        expect(window.t('Greeting')).toBe('Hola');
    });

    it('returns key when string not found in any bundle', () => {
        window._allStrings = { en: { Other: 'Other' } };
        window._currentLocale = 'en';
        expect(window.t('Missing')).toBe('Missing');
    });

    it('interpolates {0} argument', () => {
        window._allStrings = { en: { Msg: 'Hello {0}!' } };
        window._currentLocale = 'en';
        expect(window.t('Msg', 'Alice')).toBe('Hello Alice!');
    });

    it('interpolates multiple {0} {1} arguments', () => {
        window._allStrings = { en: { Msg: '{0} and {1}' } };
        window._currentLocale = 'en';
        expect(window.t('Msg', 'foo', 'bar')).toBe('foo and bar');
    });

    it('leaves placeholder as-is when arg index is missing', () => {
        window._allStrings = { en: { Msg: 'Value: {0}' } };
        window._currentLocale = 'en';
        expect(window.t('Msg')).toBe('Value: {0}');
    });

    it('returns raw string unchanged when no args and no placeholders', () => {
        window._allStrings = { en: { Static: 'No placeholders here' } };
        window._currentLocale = 'en';
        expect(window.t('Static')).toBe('No placeholders here');
    });
});

describe('window.loadAllStrings', () => {
    it('populates _allStrings and _supportedLocales on success', async () => {
        const mockData = {
            strings: { en: { Key: 'Value' }, es: { Key: 'Valor' } },
            supportedLocales: ['en', 'es'],
        };
        global.fetch = vi.fn().mockResolvedValue({
            ok: true,
            json: async () => mockData,
        } as unknown as Response);

        await window.loadAllStrings();

        expect(window._allStrings).toEqual(mockData.strings);
        expect(window._supportedLocales).toEqual(['en', 'es']);
    });

    it('resets to empty on fetch failure', async () => {
        window._allStrings = { en: { Key: 'OldValue' } };
        global.fetch = vi.fn().mockResolvedValue({
            ok: false,
            statusText: 'Internal Server Error',
        } as unknown as Response);

        await window.loadAllStrings();

        expect(window._allStrings).toEqual({});
        expect(window._supportedLocales).toEqual([]);
    });

    it('resets to empty on network error', async () => {
        window._allStrings = { en: { Key: 'OldValue' } };
        global.fetch = vi.fn().mockRejectedValue(new Error('Network error'));

        await window.loadAllStrings();

        expect(window._allStrings).toEqual({});
        expect(window._supportedLocales).toEqual([]);
    });

    it('handles missing strings/supportedLocales in response', async () => {
        global.fetch = vi.fn().mockResolvedValue({
            ok: true,
            json: async () => ({}),
        } as unknown as Response);

        await window.loadAllStrings();

        expect(window._allStrings).toEqual({});
        expect(window._supportedLocales).toEqual([]);
    });
});

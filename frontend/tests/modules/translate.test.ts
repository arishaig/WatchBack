import { describe, it, expect, vi, afterEach } from 'vitest';
import translateMethods from '../../src/modules/translate';

type Ctx = Record<string, any>;

function makeCtx(prefs: Ctx = {}, overrides: Ctx = {}): Ctx {
    const ctx: Ctx = {
        locale: 'en',
        configData: { preferences: { translateTargetLanguage: 'en', autoTranslate: false, ...prefs } },
        showOriginal: {},
        manualTranslations: {},
        translateState: {},
        t: (key: string, ...args: unknown[]) => (args.length ? `${key}:${args.join(',')}` : key),
        ...overrides,
    };
    for (const [name, fn] of Object.entries(translateMethods)) {
        if (typeof fn === 'function') ctx[name] = (fn as Function).bind(ctx);
    }
    return ctx;
}

const serverTranslated = { id: 'a', content: 'Hello', translatedFrom: 'fr', originalContent: 'Bonjour' };
const plain = { id: 'b', content: 'Hi there' };

afterEach(() => vi.unstubAllGlobals());

describe('translationOf / thoughtText', () => {
    it('shows the translation of a thought the server translated, and the original on request', () => {
        const ctx = makeCtx();
        expect(ctx.translationOf(serverTranslated)).toEqual({ from: 'fr', text: 'Hello', original: 'Bonjour' });
        expect(ctx.thoughtText(serverTranslated)).toBe('Hello');

        ctx.toggleOriginal(serverTranslated);
        expect(ctx.thoughtText(serverTranslated)).toBe('Bonjour');

        ctx.toggleOriginal(serverTranslated);
        expect(ctx.thoughtText(serverTranslated)).toBe('Hello');
    });

    it('leaves an untranslated thought alone', () => {
        const ctx = makeCtx();
        expect(ctx.translationOf(plain)).toBeNull();
        expect(ctx.thoughtText(plain)).toBe('Hi there');
        expect(ctx.translationBadge(plain)).toBe('');
    });

    it('shows an on-demand translation with the thought content as the original', () => {
        const ctx = makeCtx({}, { manualTranslations: { b: { text: 'Salut', translatedFrom: 'es' } } });
        expect(ctx.translationOf(plain)).toEqual({ from: 'es', text: 'Salut', original: 'Hi there' });
        expect(ctx.thoughtText(plain)).toBe('Salut');
    });

    it('does not tag a thought that was already in the target language', () => {
        const ctx = makeCtx({}, { manualTranslations: { b: { text: 'Hi there', translatedFrom: null } } });
        expect(ctx.translationOf(plain)).toBeNull();
        expect(ctx.thoughtText(plain)).toBe('Hi there');
    });
});

describe('translationBadge', () => {
    it('names the source language, and switches to the original label while the original shows', () => {
        const ctx = makeCtx();
        expect(ctx.translationBadge(serverTranslated)).toBe('Comments_TranslatedFrom:French');
        expect(ctx.translationToggleTitle(serverTranslated)).toBe('Comments_ShowOriginal');

        ctx.toggleOriginal(serverTranslated);
        expect(ctx.translationBadge(serverTranslated)).toBe('Comments_ShowingOriginal:French');
        expect(ctx.translationToggleTitle(serverTranslated)).toBe('Comments_ShowTranslation');
    });

    it('falls back to the language code when the browser cannot name it', () => {
        const ctx = makeCtx();
        expect(ctx.languageName('not-a-language-code-!!')).toBe('not-a-language-code-!!');
    });
});

describe('translateLanguageOptions', () => {
    it('lists languages by name in the viewer\'s language, in order', () => {
        const names = makeCtx().translateLanguageOptions().map((o: Ctx) => o.name);
        expect(names).toEqual([...names].sort((a, b) => a.localeCompare(b, 'en')));
        expect(names).toContain('French');
    });

    it('keeps a saved language that is not in the list selectable', () => {
        const codes = makeCtx({ translateTargetLanguage: 'fi' }).translateLanguageOptions().map((o: Ctx) => o.code);
        expect(codes).toContain('fi');
    });
});

describe('canTranslate', () => {
    it('offers the button only with a target language, auto-translate off and nothing translated yet', () => {
        expect(makeCtx().canTranslate(plain)).toBe(true);
        expect(makeCtx({ translateTargetLanguage: '' }).canTranslate(plain)).toBe(false);
        expect(makeCtx({ autoTranslate: true }).canTranslate(plain)).toBe(false);
        expect(makeCtx().canTranslate(serverTranslated)).toBe(false);
        expect(makeCtx({}, { manualTranslations: { b: { text: 'x', translatedFrom: null } } }).canTranslate(plain)).toBe(false);
    });
});

describe('translateThought', () => {
    it('posts the text and stores the translation', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ text: 'Salut', translatedFrom: 'es' }) });
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx();

        await ctx.translateThought(plain);

        expect(fetchMock.mock.calls[0][0]).toBe('/api/translate');
        expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ text: 'Hi there' });
        expect(ctx.manualTranslations.b).toEqual({ text: 'Salut', translatedFrom: 'es' });
        expect(ctx.translateState.b).toBeUndefined();
    });

    it('marks the thought failed, keeping the button for a retry, when the server errors', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 503 }));
        const ctx = makeCtx();

        await ctx.translateThought(plain);

        expect(ctx.translateState.b).toBe('failed');
        expect(ctx.translateButtonLabel(plain)).toBe('Comments_TranslateFailed');
        expect(ctx.canTranslate(plain)).toBe(true);
    });

    it('marks the thought failed when the request itself fails', async () => {
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')));
        const ctx = makeCtx();

        await ctx.translateThought(plain);

        expect(ctx.translateState.b).toBe('failed');
    });

    it('ignores a second click while one is in flight', async () => {
        const fetchMock = vi.fn();
        vi.stubGlobal('fetch', fetchMock);
        const ctx = makeCtx({}, { translateState: { b: 'busy' } });

        await ctx.translateThought(plain);

        expect(fetchMock).not.toHaveBeenCalled();
        expect(ctx.translateButtonLabel(plain)).toBe('Comments_Translating');
    });
});

import type { AppData, ConfigData } from '../types';
import { postJson } from '../utils/api';

/** The parts of a thought (or reply) the translation UI reads. */
interface TranslatableThought {
    id: string;
    content: string;
    translatedFrom?: string | null;
    originalContent?: string | null;
}

interface TranslationView {
    from: string;
    text: string;
    original: string;
}

const translateMethods: Record<string, unknown> & ThisType<AppData> = {
    /** The language comments are translated into; empty when translation is off. */
    translationTarget(): string {
        return (this.configData as ConfigData | null)?.preferences?.translateTargetLanguage ?? '';
    },

    /** A language's name in the viewer's language ("French", "francés"); the code if the browser can't say. */
    languageName(code: string): string {
        try {
            return new Intl.DisplayNames([this.locale], { type: 'language' }).of(code) ?? code;
        } catch {
            return code;
        }
    },

    /** Both texts of a translated thought: translated by the server (auto) or on demand (the button). */
    translationOf(thought: TranslatableThought): TranslationView | null {
        if (thought.translatedFrom && thought.originalContent) {
            return { from: thought.translatedFrom, text: thought.content, original: thought.originalContent };
        }
        const manual = this.manualTranslations[thought.id];
        if (manual?.translatedFrom) {
            return { from: manual.translatedFrom, text: manual.text, original: thought.content };
        }
        return null;
    },

    /** What the comment body shows: the translation, or the original once the viewer asks for it. */
    thoughtText(thought: TranslatableThought): string {
        const translation = this.translationOf(thought);
        if (!translation) return thought.content;
        return this.showOriginal[thought.id] ? translation.original : translation.text;
    },

    /** The tag on a translated comment. It becomes the way back while the original is showing. */
    translationBadge(thought: TranslatableThought): string {
        const translation = this.translationOf(thought);
        if (!translation) return '';
        const language = this.languageName(translation.from);
        return this.showOriginal[thought.id]
            ? this.t('Comments_ShowingOriginal', language)
            : this.t('Comments_TranslatedFrom', language);
    },

    translationToggleTitle(thought: TranslatableThought): string {
        return this.t(this.showOriginal[thought.id] ? 'Comments_ShowTranslation' : 'Comments_ShowOriginal');
    },

    toggleOriginal(thought: TranslatableThought) {
        this.showOriginal = { ...this.showOriginal, [thought.id]: !this.showOriginal[thought.id] };
    },

    /** The Translate button: only when a language is chosen, auto-translate is off, and it hasn't been asked yet. */
    canTranslate(thought: TranslatableThought): boolean {
        const prefs = (this.configData as ConfigData | null)?.preferences;
        if (!prefs?.translateTargetLanguage || prefs.autoTranslate) return false;
        if (!thought.content || thought.translatedFrom) return false;
        return !this.manualTranslations[thought.id];
    },

    translateButtonLabel(thought: TranslatableThought): string {
        const state = this.translateState[thought.id];
        if (state === 'busy') return this.t('Comments_Translating');
        if (state === 'failed') return this.t('Comments_TranslateFailed');
        return this.t('Comments_Translate');
    },

    async translateThought(thought: TranslatableThought) {
        if (this.translateState[thought.id] === 'busy') return;
        this.translateState = { ...this.translateState, [thought.id]: 'busy' };
        try {
            const res = await postJson('/api/translate', { text: thought.content });
            if (!res.ok) throw new Error(String(res.status));
            const body = await res.json() as { text: string; translatedFrom: string | null };
            // Remembered even when it was already in the target language, so the button goes away.
            this.manualTranslations = {
                ...this.manualTranslations,
                [thought.id]: { text: body.text, translatedFrom: body.translatedFrom },
            };
            const { [thought.id]: _done, ...rest } = this.translateState;
            this.translateState = rest;
        } catch {
            this.translateState = { ...this.translateState, [thought.id]: 'failed' };
        }
    },
};

export default translateMethods;

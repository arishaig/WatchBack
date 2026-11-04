import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Regression guard: status pills and callouts (Connected, Needs setup, warnings) are small text, so
// their colours need WCAG AA contrast (4.5:1). Solarized Dark's warning was 4.16:1 (yellow on a
// green-tinted background) and Light's success 2.96:1, and both were hard to read.

const themesDir = resolve(__dirname, '../css/themes');
const MIN_TEXT_CONTRAST = 4.5;

function variables(file: string): Record<string, string> {
    const css = readFileSync(resolve(themesDir, file), 'utf8');
    const vars: Record<string, string> = {};
    for (const m of css.matchAll(/(--wb-[a-z-]+):\s*(#[0-9a-fA-F]{6})\b/g)) {
        vars[m[1]] = m[2];
    }
    return vars;
}

function luminance(hex: string): number {
    const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
        .map(c => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
    const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
}

const themes = readdirSync(themesDir).filter(f => f.endsWith('.css'));

describe.each(themes)('%s', file => {
    const v = variables(file);

    it.each(['success', 'warn', 'error'])('%s text is readable on its own background', status => {
        expect(contrast(v[`--wb-${status}-text`], v[`--wb-${status}-bg`])).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST);
    });

    it.each(['success', 'warn', 'error'])('%s text is readable on the page and on panels', status => {
        expect(contrast(v[`--wb-${status}-text`], v['--wb-bg'])).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST);
        expect(contrast(v[`--wb-${status}-text`], v['--wb-surface'])).toBeGreaterThanOrEqual(MIN_TEXT_CONTRAST);
    });
});

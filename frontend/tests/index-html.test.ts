import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Regression guard: Alpine compiles each x-*, @*, :* attribute to a Function at
// runtime. If an attribute's JS expression is malformed (e.g. inner double-
// quotes written as `\"` instead of `&quot;`), Alpine throws a SyntaxError on
// startup and the app fails to boot. This scan catches it at test time.

const html = readFileSync(
    resolve(__dirname, '../../src/WatchBack.Api/wwwroot/index.html'),
    'utf8',
);

function decodeHtmlEntities(s: string): string {
    return s
        .replace(/&quot;/g, '"')
        .replace(/&apos;/g, "'")
        .replace(/&lt;/g, '<')
        .replace(/&gt;/g, '>')
        .replace(/&amp;/g, '&');
}

function extractAlpineAttrs(source: string): { name: string; value: string; line: number }[] {
    const pattern = /\s(x-[\w:.-]+|@[\w:.-]+|:[\w:.-]+)\s*=\s*"([^"]*)"/gs;
    const attrs: { name: string; value: string; line: number }[] = [];
    let m: RegExpExecArray | null;
    while ((m = pattern.exec(source)) !== null) {
        const line = source.slice(0, m.index).split('\n').length;
        attrs.push({ name: m[1], value: decodeHtmlEntities(m[2]), line });
    }
    return attrs;
}

function isAlpineExpressionParsable(expr: string): { ok: true } | { ok: false; error: string } {
    // Alpine wraps certain expression shapes; mirror the lenient check by also
    // accepting the wrapped forms it generates.
    const candidates = [
        expr,
        `(async()=>{ ${expr} })()`,
        `(()=>{ ${expr} })()`,
    ];
    let lastError = '';
    for (const body of candidates) {
        try {
            new Function('scope', `with (scope) { return (${body}) }`);
            return { ok: true };
        } catch (e) {
            lastError = (e as Error).message;
        }
    }
    return { ok: false, error: lastError };
}

describe('index.html Alpine attributes', () => {
    it('every x-/@/:-bound expression parses as valid JS', () => {
        const attrs = extractAlpineAttrs(html);
        const failures: string[] = [];
        for (const a of attrs) {
            // x-transition:* takes a class list, not a JS expression.
            if (a.name.startsWith('x-transition')) continue;
            const r = isAlpineExpressionParsable(a.value);
            if (!r.ok) {
                failures.push(`L${a.line} ${a.name}="${a.value.slice(0, 80)}" — ${r.error}`);
            }
        }
        expect(failures).toEqual([]);
    });
});
// Opening tags, read quote-aware: Alpine expressions carry `>` (arrow
// functions, comparisons) inside attribute values.
function openingTags(source: string): { text: string; line: number }[] {
    const tags: { text: string; line: number }[] = [];
    for (let i = source.indexOf('<'); i !== -1; i = source.indexOf('<', i + 1)) {
        if (!/[a-zA-Z]/.test(source[i + 1] ?? '')) continue;
        let quote: string | null = null;
        let j = i + 1;
        for (; j < source.length; j++) {
            const c = source[j];
            if (quote) { if (c === quote) quote = null; }
            else if (c === '"' || c === "'") quote = c;
            else if (c === '>') break;
        }
        tags.push({ text: source.slice(i, j + 1), line: source.slice(0, i).split('\n').length });
        i = j;
    }
    return tags;
}

describe('index.html x-show elements', () => {
    // Alpine applies a string-valued :style by replacing the whole style
    // attribute, which drops the `display: none` x-show wrote there. Whenever
    // the binding re-runs (e.g. once settings load) the element shows again
    // regardless of x-show: the sync bar flashed on every idle poll this way.
    // Object-valued :style sets single properties and leaves display alone.
    it('bind :style as an object, never a string', () => {
        const failures: string[] = [];
        for (const tag of openingTags(html)) {
            const attrs = extractAlpineAttrs(tag.text);
            const style = attrs.find(a => a.name === ':style');
            if (!attrs.some(a => a.name === 'x-show') || !style) continue;
            if (!style.value.trim().startsWith('{')) {
                failures.push(`L${tag.line} :style="${style.value.slice(0, 80)}"`);
            }
        }
        expect(failures).toEqual([]);
    });
});

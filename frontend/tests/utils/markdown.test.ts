import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderMarkdown } from '../../src/utils/markdown';

// Provide window.t so spoiler code path works
beforeEach(() => {
    (window as Window & { t?: (key: string) => string }).t = () => 'Reveal spoiler';
});

describe('renderMarkdown', () => {
    describe('edge cases', () => {
        it('returns empty string for empty input', () => {
            expect(renderMarkdown('')).toBe('');
        });

        it('returns empty string for null-ish values cast as string', () => {
            expect(renderMarkdown('' as string)).toBe('');
        });
    });

    describe('HTML escaping (XSS prevention)', () => {
        it('escapes ampersands', () => {
            expect(renderMarkdown('a & b')).toContain('a &amp; b');
        });

        it('escapes less-than signs', () => {
            expect(renderMarkdown('<script>')).toContain('&lt;script&gt;');
        });

        it('escapes double quotes', () => {
            expect(renderMarkdown('"hello"')).toContain('&quot;hello&quot;');
        });

        it('does not allow raw script tags through', () => {
            const result = renderMarkdown('<script>alert(1)</script>');
            expect(result).not.toContain('<script>');
            expect(result).toContain('&lt;script&gt;');
        });
    });

    describe('fenced code blocks', () => {
        it('renders triple backtick blocks as <pre>', () => {
            const result = renderMarkdown('```\nconst x = 1;\n```');
            expect(result).toContain('<pre class="wb-md-codeblock">');
            expect(result).toContain('const x = 1;');
            expect(result).toContain('</pre>');
        });

        it('trims whitespace from code blocks', () => {
            const result = renderMarkdown('```\n  trimmed  \n```');
            expect(result).toContain('trimmed');
        });

        it('handles inline code blocks on one line', () => {
            const result = renderMarkdown('```const x = 1;```');
            expect(result).toContain('<pre class="wb-md-codeblock">');
        });
    });

    describe('blockquotes', () => {
        it('renders > as blockquote', () => {
            const result = renderMarkdown('> quoted text');
            expect(result).toContain('<blockquote class="wb-md-quote">');
            expect(result).toContain('quoted text');
            expect(result).toContain('</blockquote>');
        });

        it('multiple > lines collapse into one blockquote', () => {
            const result = renderMarkdown('> line one\n> line two');
            const opens = (result.match(/<blockquote/g) ?? []).length;
            const closes = (result.match(/<\/blockquote>/g) ?? []).length;
            expect(opens).toBe(1);
            expect(closes).toBe(1);
        });

        it('does NOT treat spoiler >! as blockquote', () => {
            const result = renderMarkdown('>!spoiler!<');
            expect(result).not.toContain('<blockquote');
        });
    });

    describe('headings', () => {
        it('# renders as h3 (not h1)', () => {
            const result = renderMarkdown('# Heading One');
            expect(result).toContain('<h3 class="wb-md-heading">');
            expect(result).not.toContain('<h1');
            expect(result).not.toContain('<h2');
        });

        it('## renders as h4', () => {
            const result = renderMarkdown('## Heading Two');
            expect(result).toContain('<h4 class="wb-md-heading">');
        });

        it('### renders as h5', () => {
            const result = renderMarkdown('### Heading Three');
            expect(result).toContain('<h5 class="wb-md-heading">');
        });

        it('#### renders as h6', () => {
            const result = renderMarkdown('#### Heading Four');
            expect(result).toContain('<h6 class="wb-md-heading">');
        });

        it('##### (5 hashes) does not produce a heading', () => {
            const result = renderMarkdown('##### Too Deep');
            expect(result).not.toContain('<h');
        });
    });

    describe('unordered lists', () => {
        it('- item renders as <li> wrapped in <ul>', () => {
            const result = renderMarkdown('- item one');
            expect(result).toContain('<li class="wb-md-li">item one</li>');
            expect(result).toContain('<ul class="wb-md-ul">');
        });

        it('* item also renders as list', () => {
            const result = renderMarkdown('* item');
            expect(result).toContain('<li class="wb-md-li">');
        });

        it('consecutive list items share one <ul>', () => {
            const result = renderMarkdown('- a\n- b\n- c');
            const opens = (result.match(/<ul/g) ?? []).length;
            expect(opens).toBe(1);
            expect(result).toContain('<li class="wb-md-li">a</li>');
            expect(result).toContain('<li class="wb-md-li">b</li>');
            expect(result).toContain('<li class="wb-md-li">c</li>');
        });
    });

    describe('inline code', () => {
        it('backtick-wrapped text becomes <code>', () => {
            const result = renderMarkdown('use `console.log` here');
            expect(result).toContain('<code class="wb-md-code">console.log</code>');
        });

        it('does not span multiple lines', () => {
            const result = renderMarkdown('`no\nnewline`');
            expect(result).not.toContain('<code');
        });
    });

    describe('bold and italic', () => {
        it('***text*** renders bold+italic', () => {
            const result = renderMarkdown('***bold italic***');
            expect(result).toContain('<strong><em>bold italic</em></strong>');
        });

        it('**text** renders bold', () => {
            const result = renderMarkdown('**bold**');
            expect(result).toContain('<strong>bold</strong>');
        });

        it('*text* renders italic', () => {
            const result = renderMarkdown('*italic*');
            expect(result).toContain('<em>italic</em>');
        });

        it('*text* adjacent to word chars is not italic (word boundary)', () => {
            // e.g. "it's" should not trigger italic
            const result = renderMarkdown("it's fine");
            expect(result).not.toContain('<em>');
        });
    });

    describe('strikethrough', () => {
        it('~~text~~ renders as <del>', () => {
            const result = renderMarkdown('~~deleted~~');
            expect(result).toContain('<del>deleted</del>');
        });
    });

    describe('superscript', () => {
        it('^word renders as <sup>', () => {
            const result = renderMarkdown('E=mc^2');
            expect(result).toContain('<sup>2</sup>');
        });

        it('^(phrase) renders as <sup> without parens', () => {
            const result = renderMarkdown('note^(see below)');
            expect(result).toContain('<sup>see below</sup>');
        });
    });

    describe('spoiler tags', () => {
        it('>!text!< renders as spoiler button', () => {
            const result = renderMarkdown('>!spoiler text!<', 'Reveal');
            expect(result).toContain('<button');
            expect(result).toContain('type="button"');
            expect(result).toContain('class="wb-md-spoiler"');
            expect(result).toContain('data-wb-spoiler');
            expect(result).toContain('spoiler text');
            // tabindex and role="button" are no longer needed — native <button> provides them
            expect(result).not.toContain('role="button"');
            expect(result).not.toContain('tabindex=');
        });

        it('uses provided spoilerLabel as aria-label', () => {
            const result = renderMarkdown('>!text!<', 'Click to reveal');
            expect(result).toContain('aria-label="Click to reveal"');
        });

        it('falls back to window.t for aria-label when no spoilerLabel', () => {
            const result = renderMarkdown('>!text!<');
            expect(result).toContain('aria-label="Reveal spoiler"');
        });

        it('escapes quotes in spoilerLabel', () => {
            const result = renderMarkdown('>!text!<', 'Say "hello"');
            expect(result).toContain('aria-label="Say &quot;hello&quot;"');
        });

        it('<spoiler>text</spoiler> renders as spoiler button', () => {
            const result = renderMarkdown('<spoiler>hidden content</spoiler>', 'Reveal');
            expect(result).toContain('<button');
            expect(result).toContain('class="wb-md-spoiler"');
            expect(result).toContain('data-wb-spoiler');
            expect(result).toContain('hidden content');
        });

        it('<spoiler> tag uses provided spoilerLabel as aria-label', () => {
            const result = renderMarkdown('<spoiler>text</spoiler>', 'Click to reveal');
            expect(result).toContain('aria-label="Click to reveal"');
        });

        it('<spoiler> tag matching is case-insensitive', () => {
            const result = renderMarkdown('<SPOILER>text</SPOILER>', 'Reveal');
            expect(result).toContain('class="wb-md-spoiler"');
            expect(result).toContain('text');
        });

        it('<spoiler> tag does not pass raw HTML through', () => {
            const result = renderMarkdown('<spoiler>text</spoiler>');
            expect(result).not.toContain('<spoiler>');
            expect(result).not.toContain('</spoiler>');
        });
    });

    describe('links', () => {
        it('[text](url) renders as anchor', () => {
            const result = renderMarkdown('[click here](https://example.com)');
            expect(result).toContain('<a href="https://example.com"');
            expect(result).toContain('target="_blank"');
            expect(result).toContain('rel="noopener noreferrer"');
            expect(result).toContain('class="wb-md-link"');
            expect(result).toContain('>click here</a>');
        });

        it('bare https URL is auto-linked', () => {
            const result = renderMarkdown('visit https://example.com now');
            expect(result).toContain('<a href="https://example.com"');
        });

        it('bare URL inside href is not double-linked', () => {
            const result = renderMarkdown('[link](https://example.com)');
            const hrefCount = (result.match(/href=/g) ?? []).length;
            expect(hrefCount).toBe(1);
        });

        it('non-https URL is not auto-linked', () => {
            const result = renderMarkdown('ftp://example.com');
            expect(result).not.toContain('<a href="ftp://');
        });
    });

    describe('horizontal rules', () => {
        it('--- renders as <hr>', () => {
            const result = renderMarkdown('---');
            expect(result).toContain('<hr class="wb-md-hr">');
        });

        it('*** on its own is processed as italic (processing-order limitation)', () => {
            // The italic rule runs before the HR rule, so *** becomes <em>*</em>
            // rather than <hr>. Document this actual behavior.
            const result = renderMarkdown('***');
            expect(result).not.toContain('<hr');
        });

        it('---- (4+ dashes) also renders as <hr>', () => {
            const result = renderMarkdown('----');
            expect(result).toContain('<hr class="wb-md-hr">');
        });
    });

    describe('combined formatting', () => {
        it('markdown does not pass raw HTML through', () => {
            const result = renderMarkdown('<img src="x" onerror="alert(1)">');
            expect(result).not.toContain('<img');
            expect(result).toContain('&lt;img');
        });

        it('renders multiple elements in one string', () => {
            const result = renderMarkdown('**bold** and *italic* and ~~strike~~');
            expect(result).toContain('<strong>bold</strong>');
            expect(result).toContain('<em>italic</em>');
            expect(result).toContain('<del>strike</del>');
        });
    });
});

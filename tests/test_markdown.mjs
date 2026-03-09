/**
 * Tests for renderMarkdown — security sanitisation and basic rendering.
 * Run: node tests/test_markdown.mjs
 */
import { readFileSync } from 'fs';
import { strict as assert } from 'assert';

// Extract renderMarkdown from app.js (it's a top-level function before the Alpine block)
const src = readFileSync(new URL('../static/app.js', import.meta.url), 'utf8');
const fnMatch = src.match(/(function renderMarkdown\([\s\S]*?\n\})/);
if (!fnMatch) throw new Error('Could not extract renderMarkdown from app.js');
const renderMarkdown = new Function('return ' + fnMatch[1])();

let passed = 0;
let failed = 0;

function test(name, fn) {
    try {
        fn();
        passed++;
    } catch (e) {
        failed++;
        console.error(`FAIL: ${name}\n  ${e.message}`);
    }
}

// ── Security: XSS prevention ──

test('script tags are escaped', () => {
    const out = renderMarkdown('<script>alert("xss")</script>');
    assert(!out.includes('<script'), `script tag got through: ${out}`);
    assert(out.includes('&lt;script'));
});

test('img onerror XSS is escaped', () => {
    const out = renderMarkdown('<img src=x onerror=alert(1)>');
    assert(!out.includes('<img'), `img tag got through: ${out}`);
    assert(out.includes('&lt;img'));
});

test('event handler attributes are escaped', () => {
    const out = renderMarkdown('<div onmouseover="alert(1)">hover me</div>');
    assert(!out.includes('<div'), `div tag got through: ${out}`);
    assert(out.includes('&lt;div'));
});

test('javascript: URI in markdown link is rejected', () => {
    const out = renderMarkdown('[click me](javascript:alert(1))');
    // The link regex only matches https?:// so javascript: should not become an href
    assert(!out.includes('href="javascript:'), `javascript URI got through: ${out}`);
});

test('data: URI in markdown link is rejected', () => {
    const out = renderMarkdown('[click](data:text/html,<script>alert(1)</script>)');
    assert(!out.includes('href="data:'), `data URI got through: ${out}`);
});

test('nested HTML entities do not double-decode', () => {
    const out = renderMarkdown('&lt;script&gt;alert(1)&lt;/script&gt;');
    assert(!out.includes('<script'), `double-decode produced script tag: ${out}`);
});

test('iframe injection is escaped', () => {
    const out = renderMarkdown('<iframe src="https://evil.com"></iframe>');
    assert(!out.includes('<iframe'), `iframe got through: ${out}`);
});

test('SVG with onload is escaped', () => {
    const out = renderMarkdown('<svg onload=alert(1)>');
    assert(!out.includes('<svg'), `svg tag got through: ${out}`);
});

test('markdown link with encoded javascript URI', () => {
    const out = renderMarkdown('[x](&#106;avascript:alert(1))');
    assert(!out.includes('href="javascript:'), `encoded javascript URI got through: ${out}`);
    assert(!out.includes('href="&#106;avascript:'), `encoded javascript URI href got through: ${out}`);
});

test('style tag is escaped', () => {
    const out = renderMarkdown('<style>body{display:none}</style>');
    assert(!out.includes('<style'), `style tag got through: ${out}`);
});

// ── Basic rendering ──

test('bold text', () => {
    const out = renderMarkdown('**hello**');
    assert(out.includes('<strong>hello</strong>'));
});

test('italic text', () => {
    const out = renderMarkdown('*hello*');
    assert(out.includes('<em>hello</em>'));
});

test('strikethrough', () => {
    const out = renderMarkdown('~~removed~~');
    assert(out.includes('<del>removed</del>'));
});

test('inline code', () => {
    const out = renderMarkdown('use `console.log`');
    assert(out.includes('<code class="wb-md-code">console.log</code>'));
});

test('blockquote', () => {
    const out = renderMarkdown('> quoted text');
    assert(out.includes('<blockquote'));
    assert(out.includes('quoted text'));
});

test('link rendering', () => {
    const out = renderMarkdown('[reddit](https://reddit.com)');
    assert(out.includes('href="https://reddit.com"'));
    assert(out.includes('rel="noopener noreferrer"'));
    assert(out.includes('>reddit</a>'));
});

test('spoiler tag hidden by default', () => {
    const out = renderMarkdown('>!secret!<');
    assert(out.includes('wb-md-spoiler'));
    assert(out.includes('secret'));
});

test('empty input returns empty string', () => {
    assert.equal(renderMarkdown(''), '');
    assert.equal(renderMarkdown(null), '');
    assert.equal(renderMarkdown(undefined), '');
});

// ── Summary ──

console.log(`\n${passed + failed} tests: ${passed} passed, ${failed} failed`);
if (failed > 0) process.exit(1);

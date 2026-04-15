/**
 * Lightweight Reddit-flavored markdown → HTML renderer.
 * Handles: blockquotes, bold, italic, strikethrough, links, inline code,
 * code blocks, superscript, headings, spoiler tags, and unordered lists.
 * Output is sanitised — no raw HTML passes through.
 *
 * Implementation: code blocks and inline code spans are extracted to a
 * registry before any other processing, replaced with \x00BLOCK_n\x00 /
 * \x00SPAN_n\x00 sentinels, and restored at the end. This guarantees that
 * bold/italic/link patterns never touch code content.
 */

function escapeHtml(s: string): string {
    return s
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function extractCodeBlocks(text: string): { text: string; blocks: string[] } {
    const blocks: string[] = [];
    const out = text.replace(/```([\s\S]*?)```/g, (_, code: string) => {
        const idx = blocks.length;
        blocks.push('<pre class="wb-md-codeblock">' + (code as string).trim() + '</pre>');
        return `\x00BLOCK_${idx}\x00`;
    });
    return { text: out, blocks };
}

function processBlockLevel(text: string): string {
    const lines = text.split('\n');
    const out: string[] = [];
    let inQuote = false;
    for (const line of lines) {
        // Blockquotes (rendered as &gt; after HTML escaping; exclude spoiler prefix &gt;!)
        const quoteMatch = line.match(/^&gt;(?!!)\s?(.*)/);
        if (quoteMatch) {
            if (!inQuote) { out.push('<blockquote class="wb-md-quote">'); inQuote = true; }
            out.push(quoteMatch[1]);
        } else {
            if (inQuote) { out.push('</blockquote>'); inQuote = false; }
            const hMatch = line.match(/^(#{1,4})\s+(.*)/);
            if (hMatch) {
                // Render as h3-h6 to keep headings small relative to page chrome
                const level = Math.min(hMatch[1].length + 2, 6);
                out.push(`<h${level} class="wb-md-heading">${hMatch[2]}</h${level}>`);
            } else if (line.match(/^\s*[-*]\s+/)) {
                out.push('<li class="wb-md-li">' + line.replace(/^\s*[-*]\s+/, '') + '</li>');
            } else {
                out.push(line);
            }
        }
    }
    if (inQuote) out.push('</blockquote>');
    return out.join('\n');
}

function wrapListItems(text: string): string {
    return text.replace(
        /((?:<li class="wb-md-li">.*<\/li>\n?)+)/g,
        (m) => '<ul class="wb-md-ul">' + m + '</ul>'
    );
}

function extractInlineCode(text: string): { text: string; spans: string[] } {
    const spans: string[] = [];
    const out = text.replace(/`([^`\n]+)`/g, (_, code: string) => {
        const idx = spans.length;
        spans.push('<code class="wb-md-code">' + (code as string) + '</code>');
        return `\x00SPAN_${idx}\x00`;
    });
    return { text: out, spans };
}

function processInlineFormatting(text: string): string {
    // Order matters: bold+italic before bold before italic
    text = text.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
    text = text.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/(?<!\w)\*(.+?)\*(?!\w)/g, '<em>$1</em>');
    text = text.replace(/~~(.+?)~~/g, '<del>$1</del>');
    text = text.replace(/\^(\([^)]+\)|\S+)/g, (_, content: string) =>
        '<sup>' + (content as string).replace(/^\(|\)$/g, '') + '</sup>');
    return text;
}

function processLinks(text: string): string {
    // Named links before bare URLs to avoid double-linking
    text = text.replace(
        /\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g,
        '<a href="$2" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>'
    );
    // Bare URLs — negative lookbehind excludes URLs already inside href="..." or href='...'
    text = text.replace(
        /(?<!="|'>)(https?:\/\/[^\s<)]+)/g,
        '<a href="$1" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>'
    );
    text = text.replace(/^(-{3,}|\*{3,})$/gm, '<hr class="wb-md-hr">');
    return text;
}

function processSpoilers(text: string, spoilerLabel: string): string {
    // Uses data-wb-spoiler for event delegation in main.ts (no inline handlers).
    // <button> provides native keyboard activation (Enter + Space) and focus management.
    const label = spoilerLabel.replace(/"/g, '&quot;');
    const btn = `<button type="button" class="wb-md-spoiler" data-wb-spoiler aria-label="${label}">$1</button>`;
    // Reddit >!...!< syntax (kept for backwards compatibility with cached content)
    text = text.replace(/&gt;!(.+?)!&lt;/g, btn);
    // Canonical <spoiler>...</spoiler> tag — after HTML escaping this arrives as &lt;spoiler&gt;...&lt;/spoiler&gt;
    text = text.replace(/&lt;spoiler&gt;(.+?)&lt;\/spoiler&gt;/gi, btn);
    return text;
}

function restoreInlineCode(text: string, spans: string[]): string {
    return text.replace(/\x00SPAN_(\d+)\x00/g, (_, i: string) => spans[parseInt(i, 10)] ?? '');
}

function restoreCodeBlocks(text: string, blocks: string[]): string {
    return text.replace(/\x00BLOCK_(\d+)\x00/g, (_, i: string) => blocks[parseInt(i, 10)] ?? '');
}

export function renderMarkdown(src: string, spoilerLabel?: string): string {
    if (!src) return '';
    const label = spoilerLabel ?? window.t('Aria_RevealSpoiler');

    let text = escapeHtml(src);
    const { text: afterBlocks, blocks } = extractCodeBlocks(text);
    text = processBlockLevel(afterBlocks);
    text = wrapListItems(text);
    const { text: afterSpans, spans } = extractInlineCode(text);
    text = processInlineFormatting(afterSpans);
    text = processLinks(text);
    text = processSpoilers(text, label);
    text = restoreInlineCode(text, spans);
    text = restoreCodeBlocks(text, blocks);
    return text;
}

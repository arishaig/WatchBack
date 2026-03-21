/**
 * Lightweight Reddit-flavored markdown → HTML renderer.
 * Handles: blockquotes, bold, italic, strikethrough, links, inline code,
 * code blocks, superscript, headings, spoiler tags, and unordered lists.
 * Output is sanitised — no raw HTML passes through.
 */
export function renderMarkdown(src: string, spoilerLabel?: string): string {
    if (!src) return '';

    // Escape HTML entities first — prevents XSS while preserving literal &, <, > display
    let t = src.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

    // Fenced code blocks (```...```)
    t = t.replace(/```([\s\S]*?)```/g, (_, code: string) =>
        '<pre class="wb-md-codeblock">' + (code as string).trim() + '</pre>');

    // Process line-based features (blockquotes, headings, lists)
    const lines = t.split('\n');
    const out: string[] = [];
    let inQuote = false;
    for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        const quoteMatch = line.match(/^&gt;(?!!)\s?(.*)/);
        if (quoteMatch) {
            if (!inQuote) { out.push('<blockquote class="wb-md-quote">'); inQuote = true; }
            out.push(quoteMatch[1]);
        } else {
            if (inQuote) { out.push('</blockquote>'); inQuote = false; }
            // Headings (# to ####)
            const hMatch = line.match(/^(#{1,4})\s+(.*)/);
            if (hMatch) {
                const level = Math.min(hMatch[1].length + 2, 6); // render as h3-h6 to stay small
                out.push(`<h${level} class="wb-md-heading">${hMatch[2]}</h${level}>`);
            } else if (line.match(/^\s*[-*]\s+/)) {
                // Unordered list item
                out.push('<li class="wb-md-li">' + line.replace(/^\s*[-*]\s+/, '') + '</li>');
            } else {
                out.push(line);
            }
        }
    }
    if (inQuote) out.push('</blockquote>');
    t = out.join('\n');

    // Wrap consecutive <li> elements in <ul>
    t = t.replace(/((?:<li class="wb-md-li">.*<\/li>\n?)+)/g,
        (m) => '<ul class="wb-md-ul">' + m + '</ul>');

    // Inline code (backticks)
    t = t.replace(/`([^`\n]+)`/g, '<code class="wb-md-code">$1</code>');
    // Bold+italic
    t = t.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
    // Bold
    t = t.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    // Italic
    t = t.replace(/(?<!\w)\*(.+?)\*(?!\w)/g, '<em>$1</em>');
    // Strikethrough
    t = t.replace(/~~(.+?)~~/g, '<del>$1</del>');
    // Superscript (Reddit ^word or ^(phrase))
    t = t.replace(/\^(\([^)]+\)|\S+)/g, (_, content: string) =>
        '<sup>' + content.replace(/^\(|\)$/g, '') + '</sup>');
    // Reddit spoiler tags >!text!<
    const spoilerAriaLabel = spoilerLabel ?? window.t('Aria_RevealSpoiler');
    t = t.replace(/&gt;!(.+?)!&lt;/g,
        '<span class="wb-md-spoiler" tabindex="0" role="button" aria-label="' + spoilerAriaLabel.replace(/"/g, '&quot;') + '" onclick="this.classList.add(\'revealed\')" onkeydown="if(event.key===\'Enter\')this.classList.add(\'revealed\')">$1</span>');
    // Links [text](url)
    t = t.replace(/\[([^\]]+)\]\((https?:\/\/[^)]+)\)/g,
        '<a href="$2" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>');
    // Bare URLs (not already inside href="...")
    t = t.replace(/(?<!="|'>)(https?:\/\/[^\s<)]+)/g,
        '<a href="$1" target="_blank" rel="noopener noreferrer" class="wb-md-link">$1</a>');
    // Horizontal rules (--- or ***)
    t = t.replace(/^(-{3,}|\*{3,})$/gm, '<hr class="wb-md-hr">');

    return t;
}

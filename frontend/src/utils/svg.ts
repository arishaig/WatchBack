const UNSAFE_URL_SCHEME = /^(?:javascript|data|vbscript):/i;

// Browsers ignore whitespace/control characters inside the scheme (e.g. "java\tscript:"),
// so strip them before testing.
// eslint-disable-next-line no-control-regex
const isUnsafeUrl = (value: string): boolean => UNSAFE_URL_SCHEME.test(value.replace(/[\u0000-\u0020]/g, ''));

/**
 * Sanitize an SVG string by parsing it into DOM and stripping unsafe elements
 * (script, foreignObject) and event-handler attributes (on*), and javascript:/data:/vbscript: hrefs.
 */
export function sanitizeSvg(raw: string): string {
    if (!raw) return '';
    const parser = new DOMParser();
    const doc = parser.parseFromString(raw, 'image/svg+xml');
    const svg = doc.querySelector('svg');
    if (!svg) return '';
    // Remove dangerous elements
    for (const tag of ['script', 'foreignObject', 'iframe', 'object', 'embed']) {
        svg.querySelectorAll(tag).forEach(el => el.remove());
    }
    // Remove event handlers and javascript: hrefs
    const walk = (el: Element) => {
        for (const attr of [...el.attributes]) {
            const name = attr.name.toLowerCase();
            if (name.startsWith('on') || ((name === 'href' || name === 'xlink:href') && isUnsafeUrl(attr.value)))
                el.removeAttribute(attr.name);
        }
        for (const child of el.children) walk(child);
    };
    walk(svg);
    return svg.outerHTML;
}

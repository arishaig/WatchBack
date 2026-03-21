/**
 * Sanitize an SVG string by parsing it into DOM and stripping unsafe elements
 * (script, foreignObject) and event-handler attributes (on*).
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
            if (attr.name.startsWith('on') || (attr.name === 'href' && attr.value.trim().toLowerCase().startsWith('javascript:')))
                el.removeAttribute(attr.name);
        }
        for (const child of el.children) walk(child);
    };
    walk(svg);
    return svg.outerHTML;
}

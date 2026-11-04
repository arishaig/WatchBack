import { describe, it, expect } from 'vitest';
import subredditMappingsMethods from '../../src/modules/subredditMappings';

const methods = subredditMappingsMethods as Record<string, Function>;

function makeCtx(mappingFilter = ''): Record<string, unknown> {
    return {
        mappingFilter,
        mappingSources: [
            { id: 'builtin', name: 'Built-in', isBuiltIn: true, entries: [
                { index: 0, title: 'Severance', subreddits: ['SeveranceAppleTVPlus'] },
                { index: 1, title: 'Saturday Night Live', subreddits: ['LiveFromNewYork', 'snl'] },
            ] },
            { id: 'local', name: 'Local', isBuiltIn: false, entries: [
                { index: 0, title: 'Angel', subreddits: ['ANGEL'] },
            ] },
            { id: 'imp1', name: 'Friends list', isBuiltIn: false, entries: [
                { index: 0, title: 'Scrubs', subreddits: ['Scrubs'] },
            ] },
        ],
    };
}

describe('localMappings', () => {
    it('returns only your own mappings', () => {
        expect(methods.localMappings.call(makeCtx()).map((e: { title: string }) => e.title)).toEqual(['Angel']);
    });

    it('is empty before the lists load', () => {
        expect(methods.localMappings.call({ mappingSources: [] })).toEqual([]);
    });
});

describe('otherMappingCount', () => {
    it('counts the built-in and imported shows', () => {
        expect(methods.otherMappingCount.call(makeCtx())).toBe(3);
    });
});

describe('matchingMappings', () => {
    it('shows nothing until you search', () => {
        expect(methods.matchingMappings.call(makeCtx(''))).toEqual([]);
    });

    it('matches a title or a subreddit, ignoring case and an r/ prefix', () => {
        const titles = (q: string) => methods.matchingMappings.call(makeCtx(q))
            .map((m: { entry: { title: string } }) => m.entry.title);
        expect(titles('sever')).toEqual(['Severance']);
        expect(titles('r/SNL')).toEqual(['Saturday Night Live']);
        expect(titles('scrubs')).toEqual(['Scrubs']);
    });

    it('leaves your own mappings out (they are listed separately)', () => {
        expect(methods.matchingMappings.call(makeCtx('angel'))).toEqual([]);
    });

    it('stops at 20 matches', () => {
        const entries = Array.from({ length: 30 }, (_, i) => ({ index: i, title: `Show ${i}`, subreddits: [] }));
        const ctx = { mappingFilter: 'show', mappingSources: [{ id: 'builtin', name: 'Built-in', isBuiltIn: true, entries }] };
        expect(methods.matchingMappings.call(ctx)).toHaveLength(20);
    });
});

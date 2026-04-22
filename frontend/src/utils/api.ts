export type IntegrationField = { key: string; type: string; value?: string; hasValue?: boolean };
export type IntegrationMap = Record<string, { fields: IntegrationField[]; configured?: boolean; disabled?: boolean }>;

// Must stay in sync with FormValueExtensions.ExistingSentinel on the server
export const EXISTING_SENTINEL = '__EXISTING__';

/**
 * Build the flat config payload from integration fields + current edits.
 * For password fields with no new input, the field is omitted by default.
 * When includeExistingPlaceholder=true (for test calls), sends EXISTING_SENTINEL
 * for password fields that have a saved value but no new input.
 */
export function buildFieldPayload(
    fields: IntegrationField[],
    edits: Record<string, string>,
    opts: { includeExistingPlaceholder?: boolean } = {}
): Record<string, string> {
    const payload: Record<string, string> = {};
    for (const field of fields) {
        const val = edits[field.key];
        if (field.type === 'password') {
            if (val && val.trim() !== '') {
                payload[field.key] = val;
            } else if (opts.includeExistingPlaceholder && field.hasValue) {
                payload[field.key] = EXISTING_SENTINEL;
            }
        } else {
            payload[field.key] = val ?? '';
        }
    }
    return payload;
}

/** POST JSON to a URL; returns the Response. */
export async function postJson(url: string, payload: unknown): Promise<Response> {
    return fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
    });
}

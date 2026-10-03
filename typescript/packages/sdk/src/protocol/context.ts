import { ProtocolError } from '../errors.js';
import { canonicalBytes, requireSafeInteger, type JsonObject } from './json.js';

/** Trusted network identity; a remote document cannot supply this configuration. */
export class NetworkContext {
    constructor(readonly reference: number, readonly registry: string) {
        requireSafeInteger(reference, 0);
        if (reference > 0xffff_ffff || registry.length !== 42 || !/^0x[0-9a-f]{40}$/.test(registry))
            throw new ProtocolError('invalid_context', 'Expected a Neo uint32 network reference and lowercase registry hash.');
        Object.freeze(this);
    }

    static parse(value: string): NetworkContext {
        const match = /^neo:(0|[1-9][0-9]{0,9}):(0x[0-9a-f]{40})$/.exec(value);
        if (!match || match[0] !== value) throw new ProtocolError('invalid_context', 'Expected a canonical network context.');
        return new NetworkContext(Number(match[1]), match[2]!);
    }

    toString(): string { return `neo:${this.reference}:${this.registry}`; }
}

/** Binds a complete object to the trusted network, excluding only named root fields. */
export function signingInput(value: JsonObject, context: NetworkContext, excluded: readonly string[] = []): Uint8Array {
    if (Object.hasOwn(value, '$context'))
        throw new ProtocolError('invalid_context', 'A protocol object must not contain a root $context property.');
    const result: JsonObject = Object.create(null) as JsonObject;
    for (const [name, field] of Object.entries(value)) if (!excluded.includes(name)) result[name] = field;
    result['$context'] = context.toString();
    return canonicalBytes(result);
}

/** Advances a counter without overflow, approximation, or reuse of a previous value. */
export function nextRevision(known: number, requested?: number): number {
    requireSafeInteger(known, -1);
    const result = requested ?? known + 1;
    requireSafeInteger(result, 0);
    if (result <= known) throw new ProtocolError('revision_exhausted', 'Revision must exceed every known revision.');
    return result;
}

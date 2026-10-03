import { ProtocolError } from '../errors.js';
import { decodeBase64Url, encodeBase64Url } from './encoding.js';
import { canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject, type JsonValue } from './json.js';

/** Unknown wire fields remain distinct from the application's camelCase members. */
export interface ExtensibleModel { readonly additionalProperties?: JsonObject }

/** A model codec enforces wire representation; domain validators enforce authorization. */
export interface ProtocolCodec<T> {
    decode(value: JsonValue): T;
    encode(value: T): JsonObject;
    parse(input: string | Uint8Array): T;
    stringify(value: T): string;
}

export interface ValueCodec<T> {
    decode(value: JsonValue): T;
    encode(value: T): JsonValue;
}

export const text: ValueCodec<string> = {
    decode(value) {
        if (typeof value !== 'string') throw new ProtocolError('invalid_field', 'Expected a string.');
        return value;
    },
    encode(value) { return this.decode(value); },
};

export const integer: ValueCodec<number> = {
    decode(value) { requireSafeInteger(value); return value; },
    encode(value) { return this.decode(value); },
};

export const boolean: ValueCodec<boolean> = {
    decode(value) {
        if (typeof value !== 'boolean') throw new ProtocolError('invalid_field', 'Expected a boolean.');
        return value;
    },
    encode(value) { return this.decode(value); },
};

export const bytes: ValueCodec<Uint8Array> = {
    decode(value) { return decodeBase64Url(text.decode(value)); },
    encode(value) {
        if (!(value instanceof Uint8Array)) throw new ProtocolError('invalid_field', 'Expected a Uint8Array.');
        return encodeBase64Url(value);
    },
};

export function array<T>(item: ValueCodec<T>): ValueCodec<readonly T[]> {
    return {
        decode(value) {
            if (!Array.isArray(value)) throw new ProtocolError('invalid_field', 'Expected an array.');
            return value.map(element => item.decode(element));
        },
        encode(value) {
            if (!Array.isArray(value)) throw new ProtocolError('invalid_field', 'Expected an array.');
            return value.map(element => item.encode(element));
        },
    };
}

export function dictionary<T>(item: ValueCodec<T>): ValueCodec<Readonly<Record<string, T>>> {
    return {
        decode(value) {
            const result: Record<string, T> = Object.create(null) as Record<string, T>;
            for (const [key, field] of Object.entries(requireObject(value))) result[key] = item.decode(field);
            return result;
        },
        encode(value) {
            if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ProtocolError('invalid_field', 'Expected a dictionary.');
            const result: JsonObject = Object.create(null) as JsonObject;
            for (const [key, field] of Object.entries(value)) result[key] = item.encode(field);
            return result;
        },
    };
}

export function enumeration<const T extends string>(...values: T[]): ValueCodec<T> {
    return {
        decode(value) {
            if (typeof value !== 'string' || !values.includes(value as T)) throw new ProtocolError('invalid_field', 'Unknown enum value.');
            return value as T;
        },
        encode(value) { return this.decode(value); },
    };
}

export interface Field<T> { readonly wire: string; readonly codec: ValueCodec<T>; readonly optional?: boolean; readonly nullable?: boolean }
type Fields<T> = { [K in Exclude<keyof T, 'additionalProperties'>]-?: Field<Exclude<T[K], null | undefined>> };

/** Creates an explicit mapping without guessing wire names or discarding signed extensions. */
export function defineCodec<T extends ExtensibleModel>(fields: Fields<T>, type?: string): ProtocolCodec<T> {
    const entries = Object.entries(fields) as Array<[string, Field<unknown>]>;
    const wireNames = new Set(entries.map(([, field]) => field.wire));
    if (type !== undefined) wireNames.add('$type');
    if (wireNames.size !== entries.length + (type === undefined ? 0 : 1)) throw new Error('Duplicate codec field definition.');
    const modelNames = new Set(entries.map(([name]) => name));
    const codec: ProtocolCodec<T> = {
        decode(value) {
            const object = requireObject(value);
            // Also enforces scalar, integer and depth rules for direct object inputs.
            canonicalJson(object);
            if (type !== undefined && object['$type'] !== type) throw new ProtocolError('invalid_type', `Expected ${type}.`);
            const result: Record<string, unknown> = {};
            for (const [name, field] of entries) {
                if (!Object.hasOwn(object, field.wire)) {
                    if (!field.optional) throw new ProtocolError('missing_field', `Missing ${field.wire}.`);
                    continue;
                }
                const input = object[field.wire]!;
                if (input === null && field.nullable) result[name] = null;
                else result[name] = field.codec.decode(input);
            }
            const extras: JsonObject = Object.create(null) as JsonObject;
            for (const [name, field] of Object.entries(object)) if (!wireNames.has(name)) extras[name] = field;
            if (Object.keys(extras).length !== 0) result.additionalProperties = requireObject(parseJson(canonicalJson(extras)));
            return result as T;
        },
        encode(value) {
            if (value === null || typeof value !== 'object') throw new ProtocolError('invalid_model', 'Expected a model object.');
            for (const name of Object.keys(value))
                if (name !== 'additionalProperties' && !modelNames.has(name)) throw new ProtocolError('invalid_model', `Unknown model member ${name}; use additionalProperties for wire extensions.`);
            const result: JsonObject = Object.create(null) as JsonObject;
            if (type !== undefined) result['$type'] = type;
            for (const [name, field] of entries) {
                const input = (value as Record<string, unknown>)[name];
                if (input === undefined && field.optional) continue;
                if (input === undefined) throw new ProtocolError('missing_field', `Missing ${name}.`);
                if (input === null && field.nullable) result[field.wire] = null;
                else result[field.wire] = field.codec.encode(input);
            }
            for (const [name, field] of Object.entries(value.additionalProperties ?? {})) {
                if (wireNames.has(name)) throw new ProtocolError('conflicting_extension', `Extension conflicts with ${name}.`);
                result[name] = field;
            }
            // Copy extensions and validate the entire resulting tree without normalizing fields.
            return requireObject(parseJson(canonicalJson(result)));
        },
        parse(input) { return this.decode(parseJson(input)); },
        stringify(value) { return canonicalJson(this.encode(value)); },
    };
    return codec;
}

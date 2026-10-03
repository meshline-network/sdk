import { ProtocolError } from '../errors.js';
import { canonicalJson, type JsonObject, type JsonValue } from './json.js';

/** Complete business objects do not permit null, including extensions, unless their schema explicitly says otherwise. */
export function validateCompleteObject(value: JsonObject): void {
    canonicalJson(value);
    function visit(node: JsonValue): void {
        if (node === null) throw new ProtocolError('invalid_null', 'Null is not permitted in this complete protocol object.');
        if (Array.isArray(node)) for (const item of node) visit(item);
        else if (typeof node === 'object') for (const item of Object.values(node)) visit(item);
    }
    visit(value);
}

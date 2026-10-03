import { ProtocolError } from '../errors.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject, type JsonValue } from '../protocol/json.js';
import { validateMethodName } from './endpoint.js';
import { RelayError, rpcErrorCodes } from './relay-error.js';

export const maxSocketMessageBytes = 1_048_576;
export type RpcId = string | number;
export interface RpcNotification { readonly method: string; readonly params?: JsonObject }
export interface RpcFailure { readonly code: number; readonly message: string; readonly data?: JsonObject }
export type RpcMessage = { readonly kind: 'notification'; readonly notification: RpcNotification }
    | { readonly kind: 'success'; readonly id: RpcId; readonly result: JsonValue }
    | { readonly kind: 'failure'; readonly id: RpcId | null; readonly error: RpcFailure };

function requireId(value: JsonValue | undefined): RpcId {
    if (typeof value === 'number') { requireSafeInteger(value); return value; }
    if (typeof value !== 'string' || encodeUtf8(value).length > 128) throw new ProtocolError('invalid_rpc_id', 'RPC IDs must be safe integers or strings of at most 128 UTF-8 bytes.');
    return value;
}

export function encodeRpcRequest(id: RpcId, method: string, params?: JsonObject): string {
    requireId(id);
    validateMethodName(method);
    const value: JsonObject = { jsonrpc: '2.0', id, method, ...(params === undefined ? {} : { params: requireObject(params) }) };
    const text = canonicalJson(value);
    if (encodeUtf8(text).length > maxSocketMessageBytes) throw new ProtocolError('request_too_large', 'A WebSocket request cannot exceed 1 MiB.');
    return text;
}

/** Parses the whole message before ignoring unknown JSON-RPC wrapper extensions. */
export function decodeRpcMessage(text: string): RpcMessage {
    if (encodeUtf8(text).length > maxSocketMessageBytes) throw new ProtocolError('message_too_large', 'A WebSocket message cannot exceed 1 MiB.');
    const value = requireObject(parseJson(text));
    if (value.jsonrpc !== '2.0') throw new ProtocolError('invalid_rpc', 'Expected JSON-RPC 2.0.');
    const has = (key: string): boolean => Object.hasOwn(value, key);
    if (!has('id')) {
        if (typeof value.method !== 'string' || value.method.length === 0)
            throw new ProtocolError('invalid_rpc', 'A notification must have a method and no id.');
        // Future method names are retained. Dispatchers may ignore notifications they do not know.
        return { kind: 'notification', notification: { method: value.method, ...(has('params') ? { params: requireObject(value.params!) } : {}) } };
    }
    if (has('result') === has('error')) throw new ProtocolError('invalid_rpc', 'A response must contain exactly one of result or error.');
    if (has('result')) return { kind: 'success', id: requireId(value.id), result: value.result! };
    const error = requireObject(value.error!);
    requireSafeInteger(error.code);
    if (typeof error.message !== 'string') throw new ProtocolError('invalid_rpc', 'An RPC error must have a string message.');
    return { kind: 'failure', id: value.id === null ? null : requireId(value.id),
        error: { code: error.code, message: error.message, ...(Object.hasOwn(error, 'data') ? { data: requireObject(error.data!) } : {}) } };
}

export function rpcFailureError(error: RpcFailure): RelayError | ProtocolError {
    const code = Object.keys(rpcErrorCodes).find(key => rpcErrorCodes[key] === error.code);
    return code === undefined
        ? new ProtocolError('unknown_rpc_error', `Unmapped JSON-RPC error ${error.code}: ${error.message}`)
        : new RelayError({ code, message: error.message, ...(error.data === undefined ? {} : { data: error.data }) }, undefined, error.code);
}

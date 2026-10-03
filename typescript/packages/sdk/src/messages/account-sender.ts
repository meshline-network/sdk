import type { MessageManager } from '../components/message.js';
import { ProtocolError } from '../errors.js';
import { accountGroupHistorySecretSyncCodec, accountGroupPrivateStateRequestCodec, accountGroupPrivateStateSyncCodec,
    validateAccountGroupHistorySecretSync, validateAccountGroupPrivateStateRequest, validateAccountGroupPrivateStateSync } from '../models/groups.js';
import type { JsonObject } from '../protocol/json.js';
import type { MessageEffects } from './repository.js';

type AccountSender = (payload: JsonObject, devices: readonly string[] | undefined, signal?: AbortSignal, effects?: MessageEffects) => Promise<void>;
const senders = new WeakMap<MessageManager, AccountSender>();

/** Internal capability: managers can enqueue only recognized self-account group payloads. */
export function registerAccountSender(manager: MessageManager, sender: AccountSender): void { senders.set(manager, sender); }
export function sendGroupAccountPayload(manager: MessageManager, source: JsonObject, devices?: readonly string[], signal?: AbortSignal, effects?: MessageEffects): Promise<void> {
    const sender = senders.get(manager); if (!sender) throw new ProtocolError('not_initialized', 'Message manager has no account sender.');
    let payload: JsonObject;
    switch (source['$type']) {
        case 'meshline.account.group.state.request': {
            const value = accountGroupPrivateStateRequestCodec.decode(source); validateAccountGroupPrivateStateRequest(value); payload = accountGroupPrivateStateRequestCodec.encode(value); break;
        }
        case 'meshline.account.group.state.sync': {
            const value = accountGroupPrivateStateSyncCodec.decode(source);
            try { validateAccountGroupPrivateStateSync(value); payload = accountGroupPrivateStateSyncCodec.encode(value); }
            finally { for (const item of value.states) item.memberEncryptionPrivateKey.fill(0); } break;
        }
        case 'meshline.account.group.history_secret.sync': {
            const value = accountGroupHistorySecretSyncCodec.decode(source);
            try { validateAccountGroupHistorySecretSync(value); payload = accountGroupHistorySecretSyncCodec.encode(value); }
            finally { for (const item of value.secrets) item.applicationSecret.fill(0); } break;
        }
        default: throw new ProtocolError('invalid_payload', 'Unsupported group account payload.');
    }
    return sender(payload, devices && [...devices], signal, effects);
}

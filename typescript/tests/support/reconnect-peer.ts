import type { IncomingMessage, ServerResponse } from 'node:http';
import type { WebSocket } from 'ws';
import * as sdk from '@meshline/sdk';
import type { ReconnectEvent, ReconnectSnapshot } from '../portable/reconnect-models.js';

const context = sdk.NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
const peerId = 'QmNQa1FSTXNHmrjjfgUW3Px3Vkke4oKiFWdigWkYSux2Pi';
interface Session {
    snapshot(): ReconnectSnapshot;
    fail(error: unknown): void;
    control(action: string): void;
    request(method: string, body: sdk.JsonObject, token: string | undefined): sdk.JsonValue;
    connect(socket: WebSocket): void;
}

/** Local test relay: real signatures, TLS and wire frames, with explicit fault controls. */
export function createReconnectPeer(https: string, wss: string) {
    const sessions = new Map<string, Session>();
    function create(run: string): Session {
        const endpoint = `${https}/reconnect/${run}/v1`; const socketEndpoint = `${wss}/reconnect/${run}/v1`;
        const now = () => Math.floor(Date.now() / 1000);
        const relayKey = new Uint8Array(32).fill(7); const publicKey = sdk.accountPublicKey(relayKey); const relayId = sdk.getRelayId(publicKey);
        let descriptor: sdk.RelayDescriptor = { relayId, publicKey, endpoints: [endpoint, socketEndpoint, `/dns4/relay.example/tcp/4201/p2p/${peerId}`],
            capabilities: ['channel.host.v1', 'group.host.v1'], expiresAt: now() + 3600, relaySignature: new Uint8Array(64) };
        descriptor = { ...descriptor, relaySignature: sdk.signAccount(sdk.relayDescriptorInput(descriptor, context), relayKey) };
        const accountKey = new Uint8Array(32).fill(21); const signingKey = new Uint8Array(32).fill(22);
        let certificate: sdk.DeviceCertificate = { account: sdk.getAccountId('neo:860833102', sdk.accountPublicKey(accountKey)), accountPublicKey: sdk.accountPublicKey(accountKey),
            signingPublicKey: sdk.devicePublicKey(signingKey), encryptionPublicKey: sdk.encryptionPublicKey(new Uint8Array(32).fill(23)), notBefore: now() - 60, expiresAt: now() + 3600,
            deviceSignature: new Uint8Array(64), accountSignature: new Uint8Array(64) };
        certificate = { ...certificate, deviceSignature: sdk.signDevice(sdk.certificateDeviceInput(certificate, context), signingKey) };
        certificate = { ...certificate, accountSignature: sdk.signAccount(sdk.certificateAccountInput(certificate, context), accountKey) };
        const nonce = sdk.systemRandom.bytes(16); const channel = { relayId, channelId: sdk.deriveResourceId('channel', certificate.account, relayId, nonce, context) };
        const sign = <T extends sdk.ChannelPayload>(payload: T): T => ({ ...payload, value: { ...payload.value, deviceSignature: sdk.signDevice(sdk.channelPayloadInput(payload, context), signingKey) } });
        const channelDescriptor = sign({ kind: 'descriptor', value: { ...channel, nonce, creator: certificate.account, name: '原生重连频道 😀', revision: 0,
            status: 'active', createdAt: now(), updatedAt: now(), deviceSignature: new Uint8Array(64) } } satisfies sdk.ChannelPayload).value;
        const events: ReconnectEvent[] = []; const errors: string[] = [];
        const record = (kind: string, fields: Omit<ReconnectEvent, 'index' | 'at' | 'kind'> = {}) => { events.push({ index: events.length, at: Date.now(), kind, ...fields }); };
        const timeline: sdk.ChannelEvent[] = [{ sequence: 0, descriptorRev: 0, acceptedAt: now(), signerDeviceId: sdk.certificateId(certificate, context), payload: { kind: 'descriptor', value: channelDescriptor } }];
        const groups = [1, 2, 3].map(seed => sdk.deriveResourceId('group', certificate.account, relayId, new Uint8Array(16).fill(seed), context));
        const sockets = new Map<number, { socket: WebSocket; authenticated: boolean; channels: string[] }>(); let nextConnection = 0;
        const challenges = new Map<string, { account: string; connection: number; createdAt: number }>(); const httpSessions = new Set<string>();
        let holdAuthentication = false; let holdChannelAck = false; let malformedGroupAck = false; let rejectEmptyGroup = false; let holdGroupAck = false;
        const pendingAuth: { connection: number; send(): void }[] = []; const pendingSubscriptions: { connection: number; send(): void }[] = [];
        const challenge = (body: sdk.JsonObject, connection: number): sdk.JsonObject => {
            const nonce = sdk.encodeBase64Url(sdk.systemRandom.bytes(16)); const createdAt = now(); challenges.set(nonce, { account: String(body.account), connection, createdAt });
            return { nonce, created_at: createdAt, expires_at: createdAt + 300 };
        };
        const authenticate = (body: sdk.JsonObject, connection: number) => {
            const proof = sdk.deviceAuthenticationCodec.decode(body); sdk.validateCertificate(proof.signerCertificate, context);
            const issued = challenges.get(proof.nonce); challenges.delete(proof.nonce);
            if (!issued || issued.connection !== connection || issued.account !== proof.signerCertificate.account || now() - issued.createdAt >= 300
                || proof.signerCertificate.notBefore > now() || proof.signerCertificate.expiresAt <= now()
                || !sdk.verifyDevice(sdk.deviceAuthenticationInput(proof, relayId, connection ? socketEndpoint : endpoint, context), proof.deviceSignature, proof.signerCertificate.signingPublicKey)) throw new Error('Invalid native fixture authentication proof.');
            const token = `native-${run}-${sdk.encodeBase64Url(sdk.systemRandom.bytes(12))}`;
            if (!connection) httpSessions.add(token); record('authenticated', { connection });
            return { token, mode: 'device', expires_at: now() + 3600 };
        };
        function append(notify: boolean) {
            const sequence = timeline.length;
            const payload = sign({ kind: 'post', value: { channelId: channel.channelId, messageId: sdk.createIdentifier('message'), body: { contentType: 'text/plain', text: `重连帖子 ${sequence} 😀` }, deviceSignature: new Uint8Array(64) } } satisfies sdk.ChannelPayload);
            const event = { sequence, descriptorRev: 0, acceptedAt: now(), signerDeviceId: sdk.certificateId(certificate, context), payload };
            sdk.verifyChannelEvent(event, certificate, channelDescriptor, context, channelDescriptor); timeline.push(event); record('append', { sequence });
            if (notify) for (const [id, value] of sockets) if (value.authenticated && value.channels.includes(channel.channelId) && value.socket.readyState === 1) {
                value.socket.send(JSON.stringify({ jsonrpc: '2.0', method: 'channel.timeline.changed', params: sdk.channelTimelineChangedCodec.encode({ channelId: channel.channelId, head: sequence }) }));
                record('notification', { connection: id, sequence });
            }
        }
        append(false);
        return {
            fail: error => { errors.push(String(error)); },
            snapshot: () => ({ relayId, endpoint, channel, groups, events: [...events], errors: [...errors], openConnections: [...sockets.keys()], heldAuthentication: pendingAuth.length, heldSubscriptions: pendingSubscriptions.length }),
            control(action) {
                record('control', { action });
                if (action === 'drop-and-hold') { holdAuthentication = true; holdChannelAck = true; for (const { socket } of sockets.values()) socket.terminate(); }
                else if (action === 'release-auth') { holdAuthentication = false; for (const pending of pendingAuth.splice(0)) pending.send(); }
                else if (action === 'append-silent') append(false);
                else if (action === 'append-notify') append(true);
                else if (action === 'release-subscriptions') { holdChannelAck = false; holdGroupAck = false; for (const pending of pendingSubscriptions.splice(0)) pending.send(); }
                else if (action === 'malformed-group-ack') { malformedGroupAck = true; holdAuthentication = true; }
                else if (action === 'reject-empty-group') rejectEmptyGroup = true;
                else if (action === 'hold-group-ack') holdGroupAck = true;
                else throw new Error(`Unknown reconnect control: ${action}`);
            },
            request(method, body, token) {
                record('http', { method, params: body });
                if (method === 'relay.descriptor') return sdk.relayDescriptorCodec.encode(descriptor);
                if (method === 'auth.challenge') return challenge(body, 0);
                if (method === 'auth.device.verify') return authenticate(body, 0);
                if (method === 'relay.info') return { relay_id: relayId, name: 'Native reconnect fixture', server_time: now(), limits: {
                    message_retention: 86400, channel_timeline_retention: 86400, group_message_retention: 86400, max_group_members: 100, max_group_invite_ttl: 86400, max_channel_subscriptions: 100, max_group_subscriptions: 100 } };
                if (!token || !httpSessions.has(token)) throw new Error('Native fixture requires an authenticated HTTP session.');
                if (body.channel_id !== channel.channelId) throw new Error('Unknown fixture channel.');
                if (method === 'channel.resolve') return sdk.channelResolveResultCodec.encode({ descriptor: channelDescriptor, signerCertificate: certificate });
                if (method === 'channel.read') {
                    const after = body.after === undefined ? -1 : Number(body.after); const before = body.before === undefined ? Infinity : Number(body.before);
                    const selected = timeline.filter(value => value.sequence > after && value.sequence < before);
                    return sdk.channelReadPageCodec.encode({ events: selected, certificates: [certificate], hasMore: false });
                }
                throw new Error(`Unexpected native fixture HTTP method: ${method}`);
            },
            connect(socket) {
                const id = ++nextConnection; const connection = { socket, authenticated: false, channels: [] as string[] }; sockets.set(id, connection); record('open', { connection: id });
                socket.on('close', code => {
                    sockets.delete(id); record('close', { connection: id, code });
                    for (const pending of [pendingAuth, pendingSubscriptions]) for (let index = pending.length - 1; index >= 0; index--) if (pending[index]!.connection === id) pending.splice(index, 1);
                });
                socket.on('error', error => { errors.push(String(error)); });
                socket.on('message', data => {
                    try {
                        const request = sdk.requireObject(sdk.parseJson(String(data))); const method = String(request.method); const params = sdk.requireObject(request.params!);
                        record('rpc', { connection: id, method, params });
                        const ack = (result: sdk.JsonValue) => { if (socket.readyState === 1) { socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, result })); record('ack', { connection: id, method, params }); } };
                        if (method === 'auth.challenge') {
                            const send = () => ack(challenge(params, id)); if (holdAuthentication) pendingAuth.push({ connection: id, send }); else send(); return;
                        }
                        if (method === 'auth.device.verify') { const credentials = authenticate(params, id); connection.authenticated = true; ack(credentials); return; }
                        if (!connection.authenticated) throw new Error('Subscription was submitted before authentication.');
                        if (method === 'channel.subscribe') {
                            const value = sdk.channelSubscriptionRequestCodec.decode(params); sdk.validateChannelSubscriptionRequest(value);
                            connection.channels = [...value.channelIds]; if (holdChannelAck) pendingSubscriptions.push({ connection: id, send: () => ack(null) }); else ack(null); return;
                        }
                        if (method === 'group.subscribe') {
                            const value = sdk.groupSubscriptionRequestCodec.decode(params); sdk.validateGroupSubscriptionRequest(value);
                            if (malformedGroupAck) { malformedGroupAck = false; ack({ unexpected: true }); return; }
                            if (!value.groupIds.length && rejectEmptyGroup) {
                                rejectEmptyGroup = false; socket.send(JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: sdk.rpcErrorCodes.temporarily_unavailable, message: 'Injected transient empty-set rejection' } }));
                                record('rejected-empty', { connection: id, method, params }); return;
                            }
                            if (holdGroupAck) pendingSubscriptions.push({ connection: id, send: () => ack(null) }); else ack(null); return;
                        }
                        throw new Error(`Unexpected native fixture RPC method: ${method}`);
                    } catch (error) { errors.push(String(error)); socket.close(1011, 'Fixture validation failed'); }
                });
            },
        };
    }
    return {
        snapshots: () => Object.fromEntries([...sessions].map(([id, session]) => [id, session.snapshot()])),
        http(request: IncomingMessage, response: ServerResponse, body: string): boolean {
            const url = new URL(request.url!, https); if (!url.pathname.startsWith('/reconnect/')) return false;
            response.setHeader('Content-Type', 'application/json');
            const replySnapshot = (session: Session) => {
                // Keep fault-control traffic off idle pooled TLS connections. Relay /v1
                // requests still exercise normal keep-alive, disconnects and recovery.
                response.setHeader('Connection', 'close');
                response.end(JSON.stringify(session.snapshot()));
            };
            let activeSession: Session | undefined;
            try {
                const value = body ? sdk.requireObject(sdk.parseJson(body)) : Object.fromEntries(url.searchParams) as sdk.JsonObject;
                if (url.pathname === '/reconnect/begin' && request.method === 'POST') {
                    const run = String(value.run); if (!/^[a-f0-9]{24}$/.test(run) || sessions.has(run)) throw new Error('Invalid or reused native fixture run.');
                    const session = create(run); sessions.set(run, session); replySnapshot(session); return true;
                }
                const match = /^\/reconnect\/([a-f0-9]{24})\/(.+)$/.exec(url.pathname); const session = match && sessions.get(match[1]!); if (!session) throw new Error('Unknown native reconnect run.');
                activeSession = session;
                const path = match![2]!;
                if (path === 'control' && request.method === 'POST') { session.control(String(value.action)); replySnapshot(session); }
                else if (path === 'observations') replySnapshot(session);
                else if (path.startsWith('v1/')) response.end(JSON.stringify(session.request(path.slice(3).replaceAll('/', '.'), value, request.headers['x-meshline-session'] as string | undefined)));
                else throw new Error('Unknown reconnect route.');
            } catch (error) { activeSession?.fail(error); response.statusCode = 500; response.end(JSON.stringify({ code: 'internal_error', message: String(error) })); }
            return true;
        },
        connect(socket: WebSocket, request: IncomingMessage): boolean {
            const match = /^\/reconnect\/([a-f0-9]{24})\/v1$/.exec(request.url ?? ''); if (!match) return false;
            const session = sessions.get(match[1]!); if (!session) socket.close(1008, 'Unknown test run'); else session.connect(socket); return true;
        },
    };
}

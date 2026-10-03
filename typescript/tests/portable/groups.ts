import * as sdk from '@meshline/sdk';
import { GroupRepository } from '../../packages/sdk/dist/groups/repository.js';
import { GroupSecrets } from '../../packages/sdk/dist/groups/secrets.js';
import { GroupMessageProcessor } from '../../packages/sdk/dist/groups/messages.js';
import { GroupAccountSync } from '../../packages/sdk/dist/groups/account-sync.js';
import { MessageRepository } from '../../packages/sdk/dist/messages/repository.js';
import { context } from './relay-fixture.js';

interface Environment { readonly store: sdk.MeshlineStore; readonly device: sdk.DeviceManager; readonly accountMessages: sdk.MessageManager; readonly protector: sdk.SecretProtector; readonly random: sdk.RandomSource }
function engines({ store, device, protector }: Environment) {
    const accountId = device.certificate.account;
    const repository = new GroupRepository(store, context, accountId);
    const secrets = new GroupSecrets({ store, context, accountId, deviceId: () => sdk.certificateId(device.certificate, context), protector });
    return { repository, secrets, messages: new GroupMessageProcessor(store, context, accountId, secrets) };
}

/** Persist only encrypted key material and authenticated ciphertext before closing the platform store. */
export async function stageGroup(environment: Environment, relayId: string): Promise<{ group: sdk.GroupRef; forbidden: string[] }> {
    const { device, random } = environment; const { repository } = engines(environment); const certificate = device.certificate;
    const accountId = certificate.account; const deviceId = sdk.certificateId(certificate, context); const now = Math.floor(Date.now() / 1000);
    const nonce = random.bytes(16); const group = { relayId, groupId: sdk.deriveResourceId('group', accountId, relayId, nonce, context) };
    const memberKey = random.bytes(32); const clientSecret = random.bytes(32); const relaySecret = random.bytes(32); let applicationSecret: Uint8Array | undefined;
    try {
        const publicKey = sdk.encryptionPublicKey(memberKey); const commitment = sdk.groupClientSecretCommitment(group.groupId, clientSecret, context);
        let create: sdk.GroupCreate = { groupId: group.groupId, nonce, name: '浏览器群恢复', memberCapacity: 10, invitePolicy: 'administrators',
            owner: { account: accountId, memberEncryptionPublicKey: publicKey }, clientSecretCommitment: commitment, deviceSignature: new Uint8Array(64) };
        create = { ...create, deviceSignature: await device.sign(sdk.groupCreateInput(create, context)) };
        applicationSecret = sdk.deriveGroupApplicationSecret(group.groupId, 0, commitment, clientSecret, relaySecret, context);
        const timeline: sdk.GroupEvent[] = [{ sequence: 0, epoch: 0, acceptedAt: now, signerDeviceId: deviceId, payload: sdk.groupCreateCodec.encode(create) }];
        for (const payload of [sdk.groupMemberNicknameUpdateCodec.encode({ nickname: '群内昵称 😀' }), sdk.groupMessageCodec.encode({ body: { contentType: 'text/plain', text: '重启后解密群消息' } })]) {
            const envelope = await sdk.encryptGroupMessage({ context, signer: device, groupId: group.groupId, epoch: 0, messageId: sdk.createIdentifier('message', random), createdAt: now, applicationSecret, payload, random });
            timeline.push({ sequence: timeline.length, epoch: 0, acceptedAt: now, signerDeviceId: deviceId, payload: sdk.groupMessageEnvelopeCodec.encode(envelope) });
        }
        const syncPayload = sdk.accountGroupPrivateStateSyncCodec.encode({ states: [{ ...group, memberEncryptionPrivateKey: memberKey }] });
        const request = await sdk.encryptMessage({ context, signer: device, messageId: sdk.createIdentifier('message', random), createdAt: now, recipient: accountId, recipientDevices: [certificate], payload: syncPayload, random });
        const received = await sdk.decryptMessage({ context, receiver: device, sender: certificate, envelope: request.envelope, keyBox: request.recipientBoxes[0]! });
        sdk.authorizedDevice((await device.getDeviceState())!, deviceId, context, now);
        const accountRepository = new MessageRepository({ store: environment.store, context, accountId, deviceId: () => sdk.certificateId(device.certificate, context), secretProtector: environment.protector, clock: sdk.systemClock });
        await accountRepository.accept(relayId, { sequence: 0, envelope: request.envelope, keyBox: request.recipientBoxes[0]!, acceptedAt: now }, await accountRepository.prepare(request.envelope, received), false);
        await repository.synchronizePage(group, { async read() { return { events: timeline, certificates: [certificate], hasMore: false }; } });
        await repository.saveKeyPage(group, { keys: [{ epoch: 0,
            clientSecretBox: sdk.sealGroupClientSecret({ groupId: group.groupId, account: accountId, memberEncryptionPublicKey: publicKey, clientSecretCommitment: commitment }, clientSecret, context, random),
            relaySecretBox: sdk.sealGroupSecret(relaySecret, certificate.encryptionPublicKey, sdk.groupRelaySecretBoxAad({ groupId: group.groupId, account: accountId, deviceId, epoch: 0 }, context), random),
        }], hasMore: false }, -1);
        return { group, forbidden: [memberKey, clientSecret, relaySecret, applicationSecret].map(sdk.encodeBase64Url) };
    } finally { memberKey.fill(0); clientSecret.fill(0); relaySecret.fill(0); applicationSecret?.fill(0); }
}

export async function recoverGroup(environment: Environment, staged: Awaited<ReturnType<typeof stageGroup>>): Promise<{ recoveredGroup: boolean; protectedGroupSecrets: boolean; recoveredGroupAccountSync: boolean; failedGroupSyncUnchanged: boolean; groupKeyCursorAtomic: boolean }> {
    const { repository, secrets, messages } = engines(environment);
    const initiallyEmpty = !(await environment.store.read([{ collection: 'group_member_keys' }, { collection: 'group_account_meta' }])).sets.some(rows => rows.length);
    const consumer = new GroupAccountSync({ store: environment.store, context, accountId: environment.device.accountId, clock: sdk.systemClock, device: environment.device, messages: environment.accountMessages, secrets, rejected: (_id, error) => { throw error; } });
    // Inject an explicit storage failure before the atomic transaction. No SDK effect is mocked.
    const realStore = environment.store; const queries = [{ collection: 'group_member_keys' }, { collection: 'group_account_meta' }];
    const before = await realStore.read(queries); const injected = new Error('Injected group key/cursor commit failure');
    let failNext = true; let groupKeyCursorAtomic = false;
    const faultStore: sdk.MeshlineStore = {
        migrate: signal => realStore.migrate(signal), initialize: (binding, signal) => realStore.initialize(binding, signal),
        read: (queries, signal) => realStore.read(queries, signal), openQuery: (query, signal) => realStore.openQuery(query, signal),
        dispose: () => realStore.dispose(),
        async commit(version, mutations, signal) {
            const keyBatch = mutations.some(value => value.collection === 'group_member_keys');
            if (keyBatch && failNext) { failNext = false; throw injected; }
            const revision = await realStore.commit(version, mutations, signal);
            if (keyBatch) {
                const saved = await realStore.read(queries);
                groupKeyCursorAtomic = mutations.some(value => value.collection === 'group_account_meta')
                    && saved.sets.every(rows => rows.length === 1 && rows[0]!.revision === revision);
            }
            return revision;
        },
    };
    const guarded = new GroupAccountSync({ ...consumer.options, store: faultStore });
    let failedGroupSyncUnchanged = false;
    try { await guarded.consume(new AbortController().signal); throw new Error('Expected injected storage failure'); }
    catch (error) {
        if (error !== injected) throw error;
        failedGroupSyncUnchanged = JSON.stringify((await realStore.read(queries)).sets) === JSON.stringify(before.sets);
    }
    await guarded.consume(new AbortController().signal); const recovery = await secrets.derivePending(staged.group, environment.device);
    let text: string | undefined;
    await messages.processPending(staged.group, (_sequence, result) => { if (result.error) throw result.error; if (result.message) text = result.message.body?.text; });
    const projection = await repository.get(staged.group);
    const snapshot = await environment.store.read(['group_member_keys', 'group_client_secrets', 'group_application_secrets', 'group_epochs', 'messages'].map(collection => ({ collection })));
    const serialized = sdk.canonicalJson(snapshot.sets.map(rows => rows.map(row => row.value)));
    return { failedGroupSyncUnchanged, groupKeyCursorAtomic, recoveredGroup: recovery.derived.length === 1 && !recovery.rejected.length && text === '重启后解密群消息' && projection?.members[0]?.nickname === '群内昵称 😀',
        protectedGroupSecrets: snapshot.sets[0]!.length === 1 && snapshot.sets[2]!.length === 1 && staged.forbidden.every(value => !serialized.includes(value)),
        recoveredGroupAccountSync: initiallyEmpty && (await environment.store.read([{ collection: 'group_account_meta' }])).sets[0]![0]?.value.sequence === 2 };
}

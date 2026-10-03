import { expect, test } from 'vitest';
import * as sdk from '@meshline/sdk';
import { groupActors, groupContext as context, groupReference as group, groupVectors } from '../support/group-fixture.js';
const now = 1730000010; const actor = groupActors.get('A')!; const account = actor.certificate.account;
const invitation = (() => {
    const value: sdk.GroupInvite = { groupId: group.groupId, inviteId: 'inv_AAECAwQFBgcICQoLDA0ODw', inviter: account, createdAt: 1730000000, expiresAt: 1730001000, maxUses: 3, deviceSignature: new Uint8Array(64) };
    return { ...value, deviceSignature: sdk.signDevice(sdk.groupInviteInput(value, context), sdk.decodeBase64Url(actor.signing_private_key)) };
})();
const invitePage: sdk.GroupInvitePage = { invites: [{ invite: invitation, signerDeviceId: actor.device_id, uses: 2 }], certificates: [actor.certificate] };
const query = { groupId: group.groupId };
const otherGroupId = sdk.deriveResourceId('group', account, group.relayId, new Uint8Array(16).fill(33), context);
const applicationEntries = groupVectors.management_chain.member_requests.filter(row => row.request.$type === 'meshline.group.application').map(row => ({ application: sdk.groupApplicationCodec.decode(row.request), signerCertificate: groupActors.get(row.actor)!.certificate, acceptedAt: 1730000000 }));
const recoveryEntries = groupVectors.management_chain.member_requests.filter(row => row.request.$type === 'meshline.group.member.recovery').map(row => ({ request: sdk.groupMemberRecoveryRequestCodec.decode(row.request), signerCertificate: groupActors.get(row.actor)!.certificate, acceptedAt: 1730000000, expiresAt: 1730001000 }));

test('invitation resolution verifies network signature, historical certificate and exact request identities', () => {
    const result = { invite: invitation, signerCertificate: actor.certificate, uses: 2 }; const request = { groupId: group.groupId, inviteId: invitation.inviteId };
    sdk.validateGroupInviteResolveResult(result, request, context, now); sdk.validateGroupInvitePage(invitePage, query, context, now);
    expect(() => sdk.validateGroupInviteResolveResult(result, { ...request, inviteId: sdk.createIdentifier('invite') }, context, now)).toThrow('another group invitation');
    expect(() => sdk.validateGroupInviteResolveResult({ ...result, signerCertificate: groupActors.get('B')!.certificate }, request, context, now)).toThrow('another account');
    expect(() => sdk.validateGroupInviteResolveResult({ ...result, uses: 4 }, request, context, now)).toThrow('use count');
    expect(() => sdk.validateGroupInviteResolveResult(result, request, context, invitation.expiresAt)).toThrow('expire');
    const signature = invitation.deviceSignature.slice(); signature[0]! ^= 1;
    expect(() => sdk.validateGroupInviteResolveResult({ ...result, invite: { ...invitation, deviceSignature: signature } }, request, context, now)).toThrow('signature');
});
test.each([
    ['missing certificate', { ...invitePage, certificates: [] }],
    ['duplicate certificate', { ...invitePage, certificates: [actor.certificate, actor.certificate] }],
    ['duplicate invitation', { ...invitePage, invites: [...invitePage.invites, ...invitePage.invites] }],
    ['negative uses', { ...invitePage, invites: [{ ...invitePage.invites[0]!, uses: -1 }] }],
    ['invalid signer identifier', { ...invitePage, invites: [{ ...invitePage.invites[0]!, signerDeviceId: 'bad' }] }],
] as const)('invitation page rejects %s', (_name, value) => { expect(() => sdk.validateGroupInvitePage(value, query, context, now)).toThrow(); });
test.each(['', 'space cursor', 'unicode游标', 'a/b', 'end\n', '%00'])('list cursor rejects noncanonical text %j', cursor => {
    expect(() => sdk.validateGroupListQuery({ ...query, cursor })).toThrow('cursor');
    expect(() => sdk.validateGroupInvitePage({ ...invitePage, next: cursor }, query, context, now)).toThrow();
});
test('list pages reject no-progress continuations and excessive results', () => {
    sdk.validateGroupListQuery({ ...query, cursor: 'a.A_Z-0~', limit: 1 });
    expect(() => sdk.validateGroupInvitePage({ ...invitePage, next: 'same' }, { ...query, cursor: 'same' }, context, now)).toThrow('cursor');
    expect(() => sdk.validateGroupInvitePage({ invites: [], certificates: [], next: 'next' }, query, context, now)).toThrow('empty');
    expect(() => sdk.validateGroupListQuery({ ...query, limit: 0 })).toThrow();
    const unique = sdk.groupInviteCodec.decode(sdk.groupInviteCodec.encode({ ...invitation, inviteId: sdk.createIdentifier('invite') }));
    expect(() => sdk.validateGroupInvitePage({ ...invitePage, invites: [...invitePage.invites, { invite: unique, signerDeviceId: actor.device_id, uses: 0 }] }, { ...query, limit: 1 }, context, now)).toThrow('limit');
});
test('independent signed application and recovery vectors validate their historical acceptance records', () => {
    expect(applicationEntries.length).toBeGreaterThan(0); expect(recoveryEntries.length).toBeGreaterThan(0);
    for (const entry of applicationEntries) {
        sdk.validateGroupApplicationEntry(entry, group.groupId, context); sdk.validateGroupApplicationPage({ applications: [entry] }, query, context);
        expect(() => sdk.validateGroupApplicationPage({ applications: [entry, entry] }, query, context)).toThrow('repeats');
        expect(() => sdk.validateGroupApplicationEntry({ ...entry, acceptedAt: -1 }, group.groupId, context)).toThrow();
        expect(() => sdk.validateGroupApplicationEntry(entry, otherGroupId, context)).toThrow('another group');
    }
    for (const entry of recoveryEntries) {
        sdk.validateGroupRecoveryEntry(entry, group.groupId, context); sdk.validateGroupRecoveryPage({ requests: [entry] }, query, context);
        expect(() => sdk.validateGroupRecoveryPage({ requests: [entry, entry] }, query, context)).toThrow('repeats');
        expect(() => sdk.validateGroupRecoveryEntry({ ...entry, expiresAt: entry.acceptedAt }, group.groupId, context)).toThrow('expiry');
        expect(() => sdk.validateGroupRecoveryEntry(entry, otherGroupId, context)).toThrow('another group');
    }
});
test.each(['application', 'recovery'] as const)('%s records cannot substitute another signing account or tamper with the signed key', kind => {
    if (kind === 'application') {
        const entry = applicationEntries[0]!; const wrong = [...groupActors.values()].find(value => value.certificate.account !== entry.application.account)!.certificate;
        expect(() => sdk.validateGroupApplicationEntry({ ...entry, signerCertificate: wrong }, group.groupId, context)).toThrow('another account');
        expect(() => sdk.validateGroupApplicationEntry({ ...entry, application: { ...entry.application, memberEncryptionPublicKey: new Uint8Array(32) } }, group.groupId, context)).toThrow('signature');
    } else {
        const entry = recoveryEntries[0]!; const wrong = [...groupActors.values()].find(value => value.certificate.account !== entry.request.account)!.certificate;
        expect(() => sdk.validateGroupRecoveryEntry({ ...entry, signerCertificate: wrong }, group.groupId, context)).toThrow('another account');
        expect(() => sdk.validateGroupRecoveryEntry({ ...entry, request: { ...entry.request, memberEncryptionPublicKey: new Uint8Array(32) } }, group.groupId, context)).toThrow('signature');
    }
});
test.each(['application', 'recovery'] as const)('%s approval boxes must exactly match unique signed member accounts', kind => {
    const member = { account, memberEncryptionPublicKey: sdk.decodeBase64Url(actor.member_encryption_public_key) };
    const approval = { groupId: group.groupId, prevHash: 'sha256:' + sdk.encodeBase64Url(new Uint8Array(32)), members: [member], deviceSignature: new Uint8Array(64) };
    const box = sdk.sealGroupSecret(new Uint8Array(32).fill(3), member.memberEncryptionPublicKey, sdk.encodeUtf8('validation fixture'));
    const value = { approval, clientSecretCommitment: 'sha256:' + sdk.encodeBase64Url(new Uint8Array(32).fill(2)), clientSecretBoxes: { [account]: box } };
    const validate = kind === 'application' ? sdk.validateGroupApplicationApproveRequest : sdk.validateGroupRecoveryApproveRequest; validate(value);
    expect(() => validate({ ...value, clientSecretBoxes: {} })).toThrow('exactly');
    expect(() => validate({ ...value, clientSecretBoxes: { [groupActors.get('B')!.certificate.account]: box } })).toThrow('missing');
    expect(() => validate({ ...value, approval: { ...approval, members: [member, member] } })).toThrow('repeats');
    expect(() => validate({ ...value, clientSecretBoxes: { [account]: { ...box, sealedSecret: new Uint8Array(59) } } })).toThrow();
});
test('all admission codecs retain unknown fields while complete objects reject nested null', () => {
    const wire = sdk.groupInvitePageCodec.encode(invitePage); const withExtension = { ...wire, extension: { release: 2 } };
    expect(sdk.groupInvitePageCodec.encode(sdk.groupInvitePageCodec.decode(withExtension))).toEqual(withExtension);
    expect(() => sdk.validateGroupInvitePage(sdk.groupInvitePageCodec.decode({ ...wire, extension: { forbidden: null } }), query, context, now)).toThrow();
    expect(() => sdk.groupListQueryCodec.decode({ group_id: group.groupId, cursor: null })).toThrow();
    expect(() => sdk.groupResolveQueryCodec.decode({ group_id: group.groupId, invite_id: null })).toThrow();
    expect(() => sdk.groupApplicationPageCodec.decode({ applications: [], next: null })).toThrow();
});
test('group query, account batch and subscription identities are explicit and validated', () => {
    sdk.validateGroupResolveQuery(query); sdk.validateGroupInviteQuery({ ...query, inviteId: invitation.inviteId }); sdk.validateGroupAccountsRequest({ ...query, accounts: [account] });
    sdk.validateGroupSubscriptionRequest({ groupIds: [] }); sdk.validateGroupSubscriptionRequest({ groupIds: [group.groupId] });
    expect(() => sdk.validateGroupAccountsRequest({ ...query, accounts: [] })).toThrow('nonempty'); expect(() => sdk.validateGroupAccountsRequest({ ...query, accounts: [account, account] })).toThrow('unique');
    expect(() => sdk.validateGroupSubscriptionRequest({ groupIds: [group.groupId, group.groupId] })).toThrow('repeats');
    expect(() => sdk.validateGroupSubscriptionRequest({ groupIds: ['bad'] })).toThrow();
    sdk.validateGroupTimelineChangedNotification({ groupId: group.groupId, head: 0 }); sdk.validateGroupChangedNotification(query);
    expect(() => sdk.validateGroupTimelineChangedNotification({ groupId: group.groupId, head: -1 })).toThrow();
    expect(() => sdk.groupTimelineChangedNotificationCodec.decode({ group_id: group.groupId })).toThrow();
});

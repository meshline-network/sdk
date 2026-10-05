import type {
    AccountRecoveryOptions, BackgroundFailure, ChannelRef, ContactInvite,
    ConversationChange, GroupInvitation, GroupRef, MeshlineClient, QueryReader,
} from '@meshline/sdk';

/** Use only for initial establishment, after the platform helper has initialized the client. */
export async function establishAndStart(client: MeshlineClient, relayId: string) {
    await client.establishAccount({ relayId });
    await client.start();
}

/** Ordinary restarts reuse the same database and secret protector. */
export async function resume(client: MeshlineClient) {
    await client.start();
}

/** Explicit account recovery is an application decision, not a startup fallback. */
export async function recover(client: MeshlineClient, options: AccountRecoveryOptions) {
    await client.recoverAccount(options);
    await client.start();
}

/** Refresh account recovery messages before processing the group. Requires usable authorization. */
export async function refreshGroup(client: MeshlineClient, relayId: string, group: GroupRef, signal?: AbortSignal) {
    await client.messageManager.synchronize(relayId, signal);
    return client.groupManager.synchronize(group, signal);
}

export async function moveHomeRelay(client: MeshlineClient, nextRelayId: string) {
    await client.changeHomeRelay(nextRelayId);
}

export async function updateProfile(client: MeshlineClient, nickname: string) {
    return client.profileManager.updateProfile({ nickname, bio: null });
}

export async function inviteContact(client: MeshlineClient, expiresAt: number) {
    return client.messageManager.createInvite(expiresAt);
}

/** The recipient calls this after receiving the invitation through the application. */
export async function requestContact(client: MeshlineClient, invitation: ContactInvite) {
    return client.messageManager.addContact(invitation);
}

/** Call on the inviter only after the incoming request has arrived. */
export async function acceptContact(client: MeshlineClient, peerAccountId: string) {
    await client.messageManager.acceptContactRequest(peerAccountId);
    return client.messageManager.setContactAlias(peerAccountId, 'Alice');
}

/** Requires established contact authorization. The return value is an outbox status. */
export async function sendText(client: MeshlineClient, peerAccountId: string, text: string) {
    const queued = await client.messageManager.sendMessage(peerAccountId, {
        body: { contentType: 'text/plain', text },
    });
    return client.messageManager.getSendStatus(queued.messageId);
}

/** Inspect the returned state: failed/canceled and an absent record are not success. */
export async function waitForDelivery(client: MeshlineClient, messageId: string, signal?: AbortSignal) {
    return client.messageManager.waitForSendStatus(messageId, 'targetAccepted', signal);
}

export async function cancelQueuedMessage(client: MeshlineClient, messageId: string) {
    return client.messageManager.cancelMessage(messageId);
}

/** Pass the first returned localSequence as before to continue toward older messages. */
export async function previousMessages(client: MeshlineClient, peerAccountId: string, before: number, signal?: AbortSignal) {
    const reader = await client.messageManager.getMessageHistory(peerAccountId, { before }, signal);
    try { return await reader.readNext(50, signal); } finally { await reader.dispose(); }
}

/** Consumes and disposes a fixed snapshot; the application supplies rendering/export work. */
export async function consumePages<T>(
    reader: QueryReader<T>,
    consume: (page: readonly T[]) => void | Promise<void>,
    signal?: AbortSignal,
) {
    try {
        for (;;) {
            const page = await reader.readNext(50, signal);
            if (page.length === 0) return;
            await consume(page);
        }
    } finally {
        await reader.dispose();
    }
}

export async function unreadConversations(client: MeshlineClient) {
    const reader = await client.getConversations({ kinds: ['direct', 'group'], unreadOnly: true });
    try {
        return await reader.readNext(50);
    } finally {
        await reader.dispose();
    }
}

export async function markConversationRead(client: MeshlineClient, conversationId: string, localSequence: number) {
    await client.markRead(conversationId, localSequence);
}

export async function publishAnnouncement(client: MeshlineClient, relayId: string) {
    const channel = await client.channelManager.createChannel(relayId, 'Announcements');
    const post = await client.channelManager.publishPost(channel.ref, {
        body: { contentType: 'text/plain', text: 'First post' },
    });
    return client.channelManager.editPost(post.ref, {
        body: { contentType: 'text/plain', text: 'Updated post' },
    });
}

export async function followChannel(client: MeshlineClient, channel: ChannelRef) {
    await client.channelManager.follow(channel);
    return client.channelManager.loadChannelHistory(channel, { limit: 50 });
}

export async function createTeam(client: MeshlineClient, relayId: string, invitee: string, expiresAt: number) {
    const group = await client.groupManager.createGroup(relayId, { name: 'Team', memberCapacity: 20 });
    return client.groupManager.createInvite(group.ref, { expiresAt, invitee });
}

/** Preview is presentation data; applying does not grant membership. */
export async function applyToTeam(client: MeshlineClient, invitation: GroupInvitation) {
    const preview = await client.groupManager.getGroup(invitation);
    await client.groupManager.applyToGroup(invitation);
    return preview;
}

/** An authorized administrator calls this after the application appears. */
export async function approveTeamMember(client: MeshlineClient, group: GroupRef, applicant: string) {
    await client.groupManager.approveApplications(group, [applicant]);
}

/** Call after verified membership and group keys are available on this client. */
export async function sendTeamMessage(client: MeshlineClient, group: GroupRef) {
    await client.groupManager.setNickname(group, 'Alice');
    return client.groupManager.sendMessage(group, { body: { contentType: 'text/plain', text: 'Welcome' } });
}

/** Requires the group's owner authority. */
export async function rotateTeamSecret(client: MeshlineClient, group: GroupRef) {
    await client.groupManager.rotateSecret(group, { rotateOwnerMemberKey: true });
}

export async function requestTeamKeyRecovery(client: MeshlineClient, group: GroupRef) {
    return client.groupManager.requestKeyRecovery(group);
}

/** Call on an administrator after the recovery request appears. */
export async function approveTeamKeyRecovery(client: MeshlineClient, group: GroupRef, accountId: string) {
    await client.groupManager.approveKeyRecovery(group, [accountId]);
}

/** Observe each emitting component's failures, as well as the client's own work. */
export function observeClient(
    client: MeshlineClient,
    refresh: (change: ConversationChange) => void | Promise<void>,
    report: (failure: BackgroundFailure) => void | Promise<void>,
) {
    const detach = [
        client.on('conversationChanged', refresh),
        ...[
            client, client.accountManager, client.deviceManager, client.profileManager,
            client.messageManager, client.channelManager, client.groupManager,
        ].map(component => component.onLifecycle('backgroundError', report)),
    ];
    return () => { for (const unsubscribe of detach) unsubscribe(); };
}

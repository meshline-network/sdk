import { createIdentifier, decryptMessage, directMessageCodec, encryptMessage } from '@meshline/sdk';
import { MessageRepository } from '../../packages/sdk/dist/messages/repository.js';
import type { ClientNetwork } from './client-network.js';
import { messageDevice } from './message-fixture.js';
import { context } from './relay-fixture.js';

// The caller owns the network. Incoming records bypass contact negotiation to isolate local projections.
export async function createConversationFixture(network: ClientNetwork) {
    const local = await network.open();
    await local.client.establishAccount();
    network.clock.wall += 60;
    const peer = messageDevice(context, 94, 95);
    let sequence = 0;
    const repository = new MessageRepository({
        store: local.store, context, accountId: local.client.accountId, deviceId: () => '', clock: network.clock,
    });
    async function incoming(text: string, createdAt = network.clock.wall, sender = peer) {
        const payload = directMessageCodec.encode({ body: { contentType: 'text/plain', text } });
        const receiver = local.client.deviceManager;
        const message = await encryptMessage({
            context, signer: sender, messageId: createIdentifier('message'), createdAt,
            recipient: local.client.accountId, recipientDevices: [receiver.certificate], payload,
        });
        const decoded = await decryptMessage({
            context, receiver, envelope: message.envelope, keyBox: message.recipientBoxes[0]!, sender: sender.certificate,
        });
        await repository.accept(network.relays[0]!.descriptor.relayId, {
            sequence: sequence++, acceptedAt: network.clock.wall, envelope: message.envelope, keyBox: message.recipientBoxes[0]!,
        }, await repository.prepare(message.envelope, decoded), false);
        return (await repository.get({ sender: sender.certificate.account, messageId: message.envelope.messageId }))!;
    }
    return { ...local, network, peer, incoming };
}

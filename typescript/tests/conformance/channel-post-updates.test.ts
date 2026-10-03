import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import {
    contentReferenceCodec, messageBodyCodec, type ChannelPostInfo, type ChannelPostUpdate, type JsonValue,
} from '@meshline/sdk';
import { ChannelNetwork } from '../support/channel-network.js';

interface Content { body: JsonValue; attachments: JsonValue[] }
interface Scenario {
    id: string;
    initial: Content;
    steps: { id: string; update: Record<string, JsonValue>; expected: Content }[];
}
const suite = JSON.parse(readFileSync(new URL('../../../tests/scenarios/channel-post-updates.json', import.meta.url), 'utf8')) as {
    version: number; cases: Scenario[];
};
if (suite.version !== 1 || !suite.cases.length || new Set(suite.cases.map(row => row.id)).size !== suite.cases.length)
    throw new Error('Unsupported or invalid shared channel scenario suite.');

function update(fields: Record<string, JsonValue>): ChannelPostUpdate {
    for (const key of Object.keys(fields)) if (key !== 'body' && key !== 'attachments') throw new Error(`Unsupported update field: ${key}`);
    return {
        ...('body' in fields ? { body: fields.body === null ? null : messageBodyCodec.decode(fields.body!) } : {}),
        ...('attachments' in fields ? { attachments: fields.attachments === null ? null : (fields.attachments as JsonValue[]).map(value => contentReferenceCodec.decode(value)) } : {}),
    };
}
function content(post: ChannelPostInfo): Content {
    return { body: post.body ? messageBodyCodec.encode(post.body) : null, attachments: (post.attachments ?? []).map(value => contentReferenceCodec.encode(value)) };
}

test.each(suite.cases)('$id', async scenario => {
    const network = new ChannelNetwork();
    try {
        let owner = await network.client(31);
        const channel = await owner.channels.createChannel(network.network.descriptor.relayId, scenario.id);
        const original = await owner.channels.publishPost(channel.ref, {
            ...(scenario.initial.body === null ? {} : { body: messageBodyCodec.decode(scenario.initial.body) }),
            attachments: scenario.initial.attachments.map(value => contentReferenceCodec.decode(value)),
        });
        expect(content(original)).toEqual(scenario.initial);
        expect(scenario.steps.length).toBeGreaterThan(0);
        expect(new Set(scenario.steps.map(step => step.id)).size).toBe(scenario.steps.length);
        for (const step of scenario.steps) {
            const edited = await owner.channels.editPost(original.ref, update(step.update));
            expect(content(edited), step.id).toEqual(step.expected);
            expect(edited.ref).toEqual(original.ref);
            expect(edited.messageId).toBe(original.messageId);
            expect(edited.author).toBe(original.author);
        }
        await owner.dispose();
        owner = await network.client(31, owner.path);
        const reader = await owner.channels.getPosts({ channelId: channel.ref.channelId });
        try {
            const posts = await reader.readNext(10);
            expect(posts).toHaveLength(1);
            expect(content(posts[0]!)).toEqual(scenario.steps.at(-1)!.expected);
            expect(posts[0]!.ref).toEqual(original.ref);
            expect(posts[0]!.messageId).toBe(original.messageId);
        } finally { await reader.dispose(); }
    } finally { await network.dispose(); }
});

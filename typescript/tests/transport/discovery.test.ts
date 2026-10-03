import { expect, test } from 'vitest';
import { HttpRelayTransport, RelayDiscovery, NetworkContext, relayDescriptorCodec } from '../../packages/sdk/src/index.js';
import { AdvancingClock } from '../support/clock.js';
import { TestRegistry, signedDescriptor } from '../support/relay-fixture.js';

test('discovery is coalesced, cached until expiry and returns copies that cannot corrupt trust', async () => {
    const registry = new TestRegistry();
    const clock = new AdvancingClock();
    let value = signedDescriptor({ expiresAt: clock.wall + 100 });
    let requests = 0;
    const http = new HttpRelayTransport({ fetch: async () => { requests++; return new Response(relayDescriptorCodec.stringify(value), { headers: { 'content-type': 'application/json' } }); } });
    const discovery = new RelayDiscovery(value.relayId, registry, http, clock);
    const results = await Promise.all(Array.from({ length: 12 }, () => discovery.getDescriptor()));
    expect(registry.reads).toBe(1); expect(requests).toBe(1);
    results[0]!.publicKey.fill(0);
    (results[1]!.endpoints as string[])[0] = 'https://untrusted.example';
    expect((await discovery.getDescriptor()).endpoints[0]).toBe('https://relay.example/v1');
    expect((await discovery.getDescriptor()).publicKey).toEqual(value.publicKey);
    clock.wall += 100;
    value = signedDescriptor({ expiresAt: clock.wall + 100 });
    expect((await discovery.getDescriptor()).expiresAt).toBe(clock.wall + 100);
    expect(registry.reads).toBe(2); expect(requests).toBe(2);
    discovery.dispose(); http.dispose();
});

test.each(['missing', 'disabled', 'wrong-id', 'insecure', 'bad-signature', 'expired'] as const)('invalid discovery %s never reaches advertised endpoint', async defect => {
    const registry = new TestRegistry();
    const clock = new AdvancingClock();
    let descriptor = signedDescriptor();
    if (defect === 'missing') registry.entry = undefined;
    if (defect === 'disabled') registry.entry = { ...registry.entry!, status: 'disabled' };
    if (defect === 'wrong-id') registry.entry = { ...registry.entry!, relayId: '0x' + '0'.repeat(40) };
    if (defect === 'insecure') registry.entry = { ...registry.entry!, endpoint: 'http://bootstrap.example' };
    if (defect === 'bad-signature') descriptor = { ...descriptor, relaySignature: new Uint8Array(64) };
    if (defect === 'expired') descriptor = signedDescriptor({ expiresAt: clock.wall });
    const requests: string[] = [];
    const http = new HttpRelayTransport({ fetch: async url => { requests.push(url); return new Response(relayDescriptorCodec.stringify(descriptor), { headers: { 'content-type': 'application/json' } }); } });
    const discovery = new RelayDiscovery(signedDescriptor().relayId, registry, http, clock);
    await expect(discovery.getInfo()).rejects.toThrow();
    expect(requests.every(url => url.startsWith('https://bootstrap.example/'))).toBe(true);
    discovery.dispose(); http.dispose();
});

test('advertised endpoints and capability limits are used only after signature validation', async () => {
    const registry = new TestRegistry(); const value = signedDescriptor();
    const requests: string[] = [];
    const http = new HttpRelayTransport({ fetch: async url => {
        requests.push(url);
        return new Response(url.includes('bootstrap') ? relayDescriptorCodec.stringify(value) : JSON.stringify({
            relay_id: value.relayId, name: 'Test Relay', server_time: 1730000000, limits: { message_retention: 1 },
        }), { headers: { 'content-type': 'application/json' } });
    } });
    const discovery = new RelayDiscovery(value.relayId, registry, http, new AdvancingClock());
    await expect(discovery.getInfo()).rejects.toThrow('limits');
    expect(requests).toEqual(['https://bootstrap.example/relay/descriptor', 'https://relay.example/v1/relay/info']);
    discovery.dispose(); http.dispose();
});

test('registry network identity cannot change even while a descriptor remains cached', async () => {
    const registry = new TestRegistry(); const value = signedDescriptor();
    const http = new HttpRelayTransport({ fetch: async () => new Response(relayDescriptorCodec.stringify(value), { headers: { 'content-type': 'application/json' } }) });
    const discovery = new RelayDiscovery(value.relayId, registry, http, new AdvancingClock());
    await discovery.getDescriptor();
    registry.context = new NetworkContext(1, registry.context.registry);
    await expect(discovery.getDescriptor()).rejects.toThrow('context');
    discovery.dispose(); http.dispose();
});

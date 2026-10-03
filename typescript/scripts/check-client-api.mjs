import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';

// This gate checks callable destinations, not parameter/behavioral equivalence.
// Overloads stay separate in the report even when TypeScript uses a union argument.
const definitions = [
    ['Meshline.MeshlineClient', 'client.ts'],
    ...['Account', 'Device', 'Profile', 'Message', 'Channel', 'Group'].map(name => [`Meshline.Components.${name}Manager`, `components/${name.toLowerCase()}.ts`]),
    ['Meshline.Components.ClientComponent', 'components/component.ts'],
];
const aliases = { 'Meshline.Components.MessageManager.SetAliasAsync': 'setContactAlias' };
const lifecycle = new Set(['InitializeAsync', 'StartAsync', 'StopAsync', 'DisposeAsync']);
const bytes = await readFile(new URL('../../dotnet/docs/api/coverage.json', import.meta.url));
const coverage = JSON.parse(bytes); const methods = [];
for (const [type, destination] of definitions) {
    const source = await readFile(new URL(`../packages/sdk/src/${destination}`, import.meta.url), 'utf8');
    for (const symbol of Object.keys(coverage.symbols)) {
        const prefix = `M:${type}.`; if (!symbol.startsWith(prefix)) continue;
        const name = symbol.slice(prefix.length).split('(')[0];
        if (name === '#ctor' || type.endsWith('.ClientComponent') && !lifecycle.has(name)) continue;
        const plain = name.replace(/Async$/, ''); const method = aliases[`${type}.${name}`] ?? plain[0].toLowerCase() + plain.slice(1);
        const present = new RegExp(`^    (?:async )?${method}\\(`, 'm').test(source);
        methods.push({ symbol, destination: `packages/sdk/src/${destination}`, method, present,
            note: name === 'GetDeviceStateAsync' || name === 'AddContactAsync' || name === 'GetGroupAsync' ? 'Overloads map to a union argument.' : name === 'CreateInviteAsync' && type.endsWith('.GroupManager') ? 'Targeted and shareable overloads map to an options object.' : undefined });
    }
}
if (methods.some(value => !value.present)) throw new Error('Missing client API destinations:\n' + methods.filter(value => !value.present).map(value => value.symbol).join('\n'));
const result = { scope: 'Documented public methods on MeshlineClient, its six managers, and the four common lifecycle methods. Constructors, properties, events, parameter contracts and behavior need separate review.',
    source: '../../dotnet/docs/api/coverage.json', sourceSha256: createHash('sha256').update(bytes).digest('hex'), methods };
await mkdir(new URL('../artifacts/api/', import.meta.url), { recursive: true });
await writeFile(new URL('../artifacts/api/client-methods.json', import.meta.url), JSON.stringify(result, null, 2) + '\n');
console.log(`${methods.length} documented public method signatures have callable TypeScript destinations. This does not establish behavioral parity.`);

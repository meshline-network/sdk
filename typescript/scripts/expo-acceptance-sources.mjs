import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

// Only fixture imports are relocated. Internal engines resolve to the independent
// installed tarball; no workspace SDK implementation is copied into the consumer.
export const acceptanceSources = {
    'App.tsx': 'tests/expo/App.tsx',
    'sqlite-registry-checks.ts': 'tests/expo/sqlite-registry-checks.ts',
    'portable-vectors.ts': 'tests/expo/portable-vectors.ts',
    'transport-checks.ts': 'tests/expo/transport-checks.ts',
    'restart-checks.ts': 'tests/expo/restart-checks.ts',
    'workflow-checks.ts': 'tests/expo/workflow-checks.ts',
    'lifecycle-checks.ts': 'tests/expo/lifecycle-checks.ts',
    'reconnect-checks.ts': 'tests/expo/reconnect-checks.ts',
    'interop-checks.ts': 'tests/expo/interop-checks.ts',
    'process-recovery-checks.ts': 'tests/expo/process-recovery-checks.ts',
    'managers.ts': 'tests/portable/managers.ts',
    'groups.ts': 'tests/portable/groups.ts',
    'relay-fixture.ts': 'tests/portable/relay-fixture.ts',
    'client-network.ts': 'tests/portable/client-network.ts',
    'clock.ts': 'tests/portable/clock.ts',
    'reconnect.ts': 'tests/portable/reconnect.ts',
    'reconnect-models.ts': 'tests/portable/reconnect-models.ts',
    'interop.ts': 'tests/portable/interop.ts',
    'interop-models.ts': 'tests/portable/interop-models.ts',
    'process-recovery.ts': 'tests/portable/process-recovery.ts',
};
export const generatedSources = ['vectors.ts', 'index.ts'];
export const hash = value => createHash('sha256').update(value).digest('hex');
export async function acceptanceSource(root, name) {
    if (!Object.hasOwn(acceptanceSources, name)) throw new Error(`Unexpected acceptance source: ${name}`);
    const source = await readFile(join(root, acceptanceSources[name]), 'utf8');
    return source.replaceAll('../portable/managers.js', './managers')
        .replaceAll('../portable/client-network.js', './client-network')
        .replaceAll('../portable/relay-fixture.js', './relay-fixture')
        .replaceAll('../portable/reconnect.js', './reconnect')
        .replaceAll('../portable/interop.js', './interop')
        .replaceAll('../portable/process-recovery.js', './process-recovery')
        .replaceAll('../../packages/sdk/dist/', './node_modules/@meshline/sdk/dist/')
        .replaceAll("from './groups.js'", "from './groups'")
        .replaceAll("from './clock.js'", "from './clock'")
        .replaceAll("from './reconnect-models.js'", "from './reconnect-models'")
        .replaceAll("from './interop-models.js'", "from './interop-models'")
        .replaceAll("from './interop.js'", "from './interop'")
        .replaceAll("from './relay-fixture.js'", "from './relay-fixture'");
}
export async function verifyAcceptanceSources(root, consumer, receipt) {
    const names = [...Object.keys(acceptanceSources), ...generatedSources];
    if (JSON.stringify(Object.keys(receipt.expoAcceptanceSources).sort()) !== JSON.stringify(names.sort())) throw new Error('Acceptance source inventory differs; rerun check:expo-bundle.');
    for (const [name, sha256] of Object.entries(receipt.expoAcceptanceSources)) {
        if (hash(await readFile(join(consumer, name))) !== sha256) throw new Error(`Consumer acceptance source changed: ${name}`);
        if (!generatedSources.includes(name) && hash(await acceptanceSource(root, name)) !== sha256) throw new Error(`Workspace acceptance source changed: ${name}`);
    }
    const fixtureHashes = Object.fromEntries(Object.keys(acceptanceSources).map(name => [name, receipt.expoAcceptanceSources[name]]));
    if (hash(JSON.stringify(fixtureHashes)) !== receipt.expoAcceptanceFixtureSha256) throw new Error('Acceptance fixture fingerprint differs.');
}

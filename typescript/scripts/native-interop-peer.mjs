import { createServer } from 'node:https';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { join } from 'node:path';
import { build } from 'vite';

const root = fileURLToPath(new URL('../', import.meta.url));
const directory = join(root, 'artifacts/native/interop'); await mkdir(directory, { recursive: true });
const bundle = join(directory, 'peer.mjs');
const inputs = ['tests/support/native-interop-peer.ts', 'tests/support/group-network.ts', 'tests/support/channel-network.ts', 'tests/support/messaging-network.ts', 'tests/support/dotnet.ts',
    'tests/support/relay-fixture.ts', '../tests/interop/dotnet/WorkflowSession.cs', '../tests/interop/dotnet/bin/Release/net10.0/Meshline.Interop.dll', '../tests/interop/dotnet/bin/Release/net10.0/Meshline.Sdk.dll'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sources = Object.fromEntries(await Promise.all(inputs.map(async path => [path, hash(await readFile(join(root, path)))])));
if (process.argv.includes('--prepare')) {
    await build({ configFile: false, build: { ssr: join(root, 'tests/support/native-interop-peer.ts'), outDir: directory, emptyOutDir: false,
        rollupOptions: { output: { entryFileNames: 'peer.mjs' } } } });
    await writeFile(join(directory, 'build.json'), JSON.stringify({ builtAt: new Date().toISOString(), sources, bundleSha256: hash(await readFile(bundle)) }, null, 2) + '\n');
    console.log('Prepared the native .NET interoperability peer.');
} else {
    const prepared = JSON.parse(await readFile(join(directory, 'build.json'), 'utf8'));
    if (JSON.stringify(prepared.sources) !== JSON.stringify(sources) || prepared.bundleSha256 !== hash(await readFile(bundle))) throw new Error('Interop peer inputs changed; rerun --prepare.');
    const { trusted } = JSON.parse(await readFile(join(root, 'artifacts/native/tls/material.json'), 'utf8'));
    const { createNativeInteropPeer } = await import(pathToFileURL(bundle).href);
    const peer = createNativeInteropPeer('https://127.0.0.1:18445', join(directory, 'runs'), join(root, '../tests/interop/dotnet/bin/Release/net10.0/Meshline.Interop.dll'));
    const server = createServer({ key: trusted.privateKey, cert: trusted.certificate }, (request, response) => {
        void (async () => {
            let body = ''; for await (const chunk of request) body += String(chunk);
            if (!await peer.http(request, response, body)) { response.statusCode = 404; response.end(); }
        })().catch(error => { console.error(error); if (!response.headersSent) response.statusCode = 500; response.end(); });
    });
    await new Promise((accept, reject) => { server.once('error', reject); server.listen(18445, '127.0.0.1', accept); });
    const receipt = { startedAt: new Date().toISOString(), port: 18445, caSha256: hash(trusted.ca), sources, bundleSha256: prepared.bundleSha256 };
    await writeFile(join(directory, 'peer.json'), JSON.stringify(receipt, null, 2) + '\n');
    const flush = () => writeFile(join(directory, 'observations.json'), JSON.stringify(peer.snapshots(), null, 2) + '\n');
    const timer = setInterval(() => { void flush().catch(console.error); }, 1000);
    const close = async () => { clearInterval(timer); server.closeAllConnections(); server.close(); await peer.dispose(); await flush(); };
    process.on('SIGINT', close); process.on('SIGTERM', close);
    console.log('Native .NET interoperability peer listening on https://127.0.0.1:18445.');
}

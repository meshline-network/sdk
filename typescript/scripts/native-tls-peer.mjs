import { createServer } from 'node:https';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { WebSocketServer } from 'ws';
import { createReconnectPeer } from '../tests/support/reconnect-peer.ts';

const root = fileURLToPath(new URL('../', import.meta.url));
const directory = join(root, 'artifacts/native/tls');
await mkdir(directory, { recursive: true });
async function material() {
    const child = spawn('dotnet', [join(root, '../tests/interop/dotnet/bin/Release/net10.0/Meshline.Interop.dll')], { windowsHide: true });
    let output = ''; let errors = '';
    child.stdout.on('data', data => output += data); child.stderr.on('data', data => errors += data);
    child.stdin.end(JSON.stringify({ operation: 'tls-material' }) + '\n');
    await new Promise((resolve, reject) => { child.once('error', reject); child.once('close', code => code === 0 ? resolve() : reject(new Error(errors))); });
    const response = JSON.parse(output); if (!response.result) throw new Error(response.message || 'Missing test TLS material.');
    return response.result;
}
if (process.argv.includes('--prepare')) {
    const trusted = await material(); const untrusted = await material();
    await writeFile(join(directory, 'material.json'), JSON.stringify({ trusted, untrusted }));
    await writeFile(join(directory, 'ca.pem'), trusted.ca);
    console.log('Prepared app-scoped localhost test CA and independent untrusted certificate.');
    process.exit(0);
}
const certificates = JSON.parse(await readFile(join(directory, 'material.json'), 'utf8'));
const observations = { requests: [], upgrades: [], closes: [], messages: [], errors: [] };
const reconnect = createReconnectPeer('https://127.0.0.1:18443', 'wss://127.0.0.1:18443');
async function start(certificate, port, trusted) {
    const server = createServer({ key: certificate.privateKey, cert: certificate.certificate }, async (request, response) => {
        try {
            const url = request.url ?? '';
            if (url === '/observations') { response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(observations)); return; }
            if (url === '/reset') { for (const rows of Object.values(observations)) rows.length = 0; response.end('{}'); return; }
            let body = ''; for await (const chunk of request) body += chunk;
            observations.requests.push({ trusted, url, headers: request.headers, body });
            if (trusted && reconnect.http(request, response, body)) return;
            if (url.startsWith('/body-delay/') || url === '/body-reset') {
                response.writeHead(200, { 'Content-Type': 'application/json' }); response.flushHeaders(); response.write('{"value":');
                const timer = setTimeout(() => { if (url === '/body-reset') response.destroy(); else response.end('1}'); }, url === '/body-reset' ? 500 : 30000);
                response.once('close', () => { clearTimeout(timer); observations.closes.push({ url, disconnected: true }); }); return;
            }
            if (url.startsWith('/redirect/')) { response.writeHead(302, { location: `https://127.0.0.1:${port}/redirect-target` }); response.end(); return; }
            if (url === '/seed') response.setHeader('Set-Cookie', 'ambient=meshline-test; Secure; HttpOnly; Path=/');
            if (url.startsWith('/echo/')) response.setHeader('Set-Cookie', 'injected=meshline-test; Secure; HttpOnly; Path=/');
            response.setHeader('Content-Type', 'application/json');
            response.end(JSON.stringify({ unicode: '中文😀', cookie: request.headers.cookie ?? null, body: body ? JSON.parse(body) : null }));
        } catch (error) { observations.errors.push(String(error)); response.statusCode = 500; response.end('{}'); }
    });
    const ws = new WebSocketServer({ noServer: true, perMessageDeflate: false });
    const sockets = new Set();
    server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
    server.on('upgrade', (request, socket, head) => {
        const url = request.url ?? ''; observations.upgrades.push({ trusted, url, headers: request.headers });
        if (url === '/redirect') { socket.end(`HTTP/1.1 302 Found\r\nLocation: wss://127.0.0.1:${port}/redirect-target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n`); return; }
        if (url === '/opening') return;
        if (url === '/unresponsive-close') {
            const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
            socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
            socket.on('data', () => {}); socket.on('close', () => observations.closes.push({ url, disconnected: true })); return;
        }
        ws.handleUpgrade(request, socket, head, client => ws.emit('connection', client, request));
    });
    ws.on('connection', (socket, request) => {
        const url = request.url ?? '';
        socket.on('error', error => observations.errors.push(String(error)));
        socket.on('close', code => observations.closes.push({ url, code }));
        if (trusted && reconnect.connect(socket, request)) return;
        if (url === '/binary') { socket.send(new Uint8Array([1, 2])); return; }
        if (url === '/oversized') { socket.send('x'.repeat(1048577)); return; }
        socket.on('message', data => {
            const message = JSON.parse(String(data));
            observations.messages.push({ url, method: message.method });
            if (url === '/silent') return;
            socket.send(JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { unicode: '中文😀', method: message.method } }));
        });
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });
    return { server, ws, sockets };
}
const peers = [await start(certificates.trusted, 18443, true), await start(certificates.untrusted, 18444, false)];
console.log('Native TLS fixture listening on loopback ports 18443 (trusted) and 18444 (untrusted).');
const receipt = { startedAt: new Date().toISOString(), caSha256: createHash('sha256').update(certificates.trusted.ca).digest('hex'), ports: [18443, 18444],
    reconnectPeerSha256: createHash('sha256').update(await readFile(join(root, 'tests/support/reconnect-peer.ts'))).digest('hex') };
await writeFile(join(directory, 'peer.json'), JSON.stringify(receipt, null, 2));
const flush = () => Promise.all([writeFile(join(directory, 'observations.json'), JSON.stringify(observations, null, 2)), writeFile(join(directory, 'reconnect-observations.json'), JSON.stringify(reconnect.snapshots(), null, 2))]);
const timer = setInterval(() => void flush(), 1000);
async function close() { clearInterval(timer); for (const peer of peers) { for (const socket of peer.sockets) socket.destroy(); peer.ws.close(); peer.server.close(); } await flush(); }
process.on('SIGTERM', close); process.on('SIGINT', close);

import { createServer } from 'node:https';
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import type { Duplex } from 'node:stream';
import { WebSocketServer } from 'ws';
import { DotnetBridge } from './dotnet.js';

export interface TlsMaterial { ca: string; certificate: string; privateKey: string }
export async function tlsMaterial(): Promise<TlsMaterial> {
    const bridge = new DotnetBridge();
    try { return await bridge.invoke<TlsMaterial>({ operation: 'tls-material' }); }
    finally { await bridge.dispose(); }
}

export async function startTlsPeer(material: TlsMaterial, handler: (request: IncomingMessage, response: ServerResponse) => Promise<void> | void) {
    const failures: unknown[] = [];
    const requests: { url: string; headers: IncomingMessage['headers']; body: string }[] = [];
    const server = createServer({ key: material.privateKey, cert: material.certificate }, (request, response) => {
        response.setHeader('Access-Control-Allow-Origin', request.headers.origin ?? '*');
        response.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, PATCH, DELETE, OPTIONS');
        response.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Meshline-Session');
        response.setHeader('Access-Control-Expose-Headers', 'Retry-After');
        if (request.method === 'OPTIONS') { response.writeHead(204); response.end(); return; }
        void (async () => {
            let body = ''; for await (const chunk of request) body += String(chunk);
            requests.push({ url: request.url ?? '', headers: request.headers, body });
            await handler(request, response);
        })().catch(error => { failures.push(error); if (!response.headersSent) response.writeHead(500); response.end(); });
    });
    const sockets = new Set<Duplex>();
    server.on('connection', socket => { sockets.add(socket); socket.once('close', () => sockets.delete(socket)); });
    const websocket = new WebSocketServer({ noServer: true, perMessageDeflate: false });
    const upgrades: { url: string; headers: IncomingMessage['headers'] }[] = [];
    server.on('upgrade', (request, socket, head) => {
        upgrades.push({ url: request.url ?? '', headers: request.headers });
        if (request.url === '/redirect') { socket.end(`HTTP/1.1 302 Found\r\nLocation: ${wss}/redirect-target\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`); return; }
        websocket.handleUpgrade(request, socket, head, client => websocket.emit('connection', client, request));
    });
    await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const https = `https://127.0.0.1:${(server.address() as AddressInfo).port}`;
    const wss = https.replace('https:', 'wss:');
    return { https, wss, websocket, requests, upgrades, failures,
        async close() {
            for (const client of websocket.clients) client.terminate();
            for (const socket of sockets) socket.destroy();
            await Promise.all([new Promise<void>(resolve => websocket.close(() => resolve())), new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()))]);
        },
    };
}

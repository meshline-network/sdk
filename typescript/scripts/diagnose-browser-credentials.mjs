// Minimal platform diagnostic, deliberately independent of SDK code and Vite.
import { chromium, firefox, webkit } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { createServer } from 'node:https';
import { fileURLToPath } from 'node:url';

const output = spawnSync('dotnet', [fileURLToPath(new URL('../../tests/interop/dotnet/bin/Release/net10.0/Meshline.Interop.dll', import.meta.url))], {
    input: '{"operation":"tls-material"}\n', encoding: 'utf8', windowsHide: true,
});
if (output.status !== 0) throw new Error(output.stderr);
const { result: material, error } = JSON.parse(output.stdout);
if (error) throw new Error(error);
const server = createServer({ key: material.privateKey, cert: material.certificate }, (request, response) => {
    if (request.url === '/') {
        response.writeHead(200, { 'Content-Type': 'text/html', 'Set-Cookie': 'ambient=test-only; Secure; SameSite=None' });
        response.end('<!doctype html><title>Native credentials diagnostic</title>');
    } else {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ cookie: request.headers.cookie ?? null }));
    }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `https://127.0.0.1:${server.address().port}`;
try {
    for (const [name, engine] of Object.entries({ chromium, firefox, webkit })) {
        const browser = await engine.launch({ headless: true });
        try {
            const context = await browser.newContext({ ignoreHTTPSErrors: true });
            const page = await context.newPage();
            await page.goto(url);
            const result = await page.evaluate(async () => {
                const result = {};
                for (const credentials of ['omit', 'same-origin', 'include']) {
                    const request = new Request('/native', { credentials });
                    result[credentials] = { configured: request.credentials, response: await (await fetch(request)).json() };
                }
                return result;
            });
            console.log(JSON.stringify({ engine: name, version: browser.version(), result }));
            await context.close();
        } finally { await browser.close(); }
    }
} finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }

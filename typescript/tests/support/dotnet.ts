import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';
import type { RelayFetch, RelayFetchInit } from '@meshline/sdk';

export class DotnetBridge {
    private readonly process: ChildProcessWithoutNullStreams;
    private readonly pending: Array<{ resolve(value: unknown): void; reject(error: Error): void }> = [];
    private readonly closed: Promise<void>;
    private errorOutput = '';
    private terminalError: Error | undefined;

    get processId(): number | undefined { return this.process.pid; }
    constructor(fetch?: RelayFetch, assembly = fileURLToPath(new URL('../../../tests/interop/dotnet/bin/Release/net10.0/Meshline.Interop.dll', import.meta.url))) {
        this.process = spawn('dotnet', [assembly, ...(fetch ? ['--workflow'] : [])], { stdio: 'pipe', windowsHide: true });
        this.process.stderr.on('data', data => { this.errorOutput += String(data); });
        const lines = createInterface({ input: this.process.stdout });
        lines.on('line', line => {
            if (fetch) {
                let value: { http?: { id: number; url: string; method: RelayFetchInit['method']; headers: Record<string, string>; body?: string | null } };
                try { value = JSON.parse(line); } catch { value = {}; }
                if (value.http) {
                    const request = value.http;
                    void (async () => {
                        try {
                            const response = await fetch(request.url, { method: request.method, headers: request.headers, ...(request.body == null ? {} : { body: request.body }), signal: new AbortController().signal, redirect: 'error', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer' });
                            this.process.stdin.write(JSON.stringify({ httpResponse: { id: request.id, status: response.status, body: response.status === 204 ? null : new TextDecoder().decode(await response.arrayBuffer()) } }) + '\n');
                        } catch (error) { this.process.stdin.write(JSON.stringify({ httpResponse: { id: request.id, error: error instanceof Error ? error.message : String(error) } }) + '\n'); }
                    })(); return;
                }
            }
            const waiting = this.pending.shift();
            if (!waiting) return;
            try { waiting.resolve(JSON.parse(line)); }
            catch (cause) { waiting.reject(new Error(`Invalid bridge response: ${line}`, { cause })); }
        });
        this.closed = new Promise(resolve => {
            this.process.once('close', code => {
                this.terminalError = new Error(`.NET bridge exited (${code}): ${this.errorOutput}`);
                for (const waiting of this.pending.splice(0)) waiting.reject(this.terminalError);
                lines.close();
                resolve();
            });
        });
        this.process.once('error', error => {
            this.terminalError = error;
            for (const waiting of this.pending.splice(0)) waiting.reject(error);
        });
    }

    async invoke<T>(request: Record<string, unknown>): Promise<T> {
        if (this.terminalError) throw this.terminalError;
        const response = await new Promise<unknown>((resolve, reject) => {
            this.pending.push({ resolve, reject });
            this.process.stdin.write(JSON.stringify(request) + '\n');
        }) as { result?: T; error?: string; message?: string };
        if (response.error) throw new Error(`${response.error}: ${response.message}`);
        return response.result as T;
    }

    async dispose(): Promise<void> {
        this.process.stdin.end();
        const timeout = setTimeout(() => this.process.kill(), 3000);
        try { await this.closed; }
        finally { clearTimeout(timeout); }
    }
}

import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { Nep6AccountSigner, NetworkContext, verifyAccount } from '@meshline/sdk';

interface Wallet { version: string; scrypt: { n: number; r: number; p: number }; accounts: { address: string; isDefault: boolean; key: string | null;
    contract: { script: string; deployed: boolean; parameters: { type: string }[] } }[] }
const vectors = JSON.parse(readFileSync(new URL('../../../tests/vectors/nep6-wallets.json', import.meta.url), 'utf8')) as {
    vectors: { password: string; publicKeys: string[]; wallet: Wallet }[];
};
const context = new NetworkContext(860833102, '0x0123456789012345678901234567890123456789');

describe('NEP-6 account signer', () => {
    it.each([[0, 0], [0, 1], [1, 0]])('decrypts Neo-generated vector %i account %i and signs the exact input', async (vectorIndex, accountIndex) => {
        const vector = vectors.vectors[vectorIndex!]!;
        const signer = await Nep6AccountSigner.fromJson(JSON.stringify(vector.wallet), vector.password.normalize('NFD'), { context, accountIndex: accountIndex! });
        try {
            expect(Buffer.from(signer.publicKey).toString('hex')).toBe(vector.publicKeys[accountIndex!]);
            expect(signer.address).toBe(vector.wallet.accounts[accountIndex!]!.address);
            expect(signer.accountId).toBe(`neo:860833102:${signer.address}`);
            const input = new TextEncoder().encode('Meshline account authorization');
            const signature = await signer.sign(input);
            expect(signature).toHaveLength(64);
            expect(verifyAccount(input, signature, signer.publicKey)).toBe(true);
            expect(verifyAccount(new Uint8Array([1]), signature, signer.publicKey)).toBe(false);
            signer.publicKey.fill(0);
            expect(Buffer.from(signer.publicKey).toString('hex')).toBe(vector.publicKeys[accountIndex!]);
        } finally { signer.dispose(); }
    });

    it('defaults to file order rather than isDefault and does not modify wallet JSON', async () => {
        const vector = vectors.vectors[0]!;
        expect(vector.wallet.accounts[1]!.isDefault).toBe(true);
        const json = JSON.stringify(vector.wallet);
        const signer = await Nep6AccountSigner.fromJson(json, vector.password, { context });
        try { expect(signer.address).toBe(vector.wallet.accounts[0]!.address); expect(JSON.stringify(vector.wallet)).toBe(json); }
        finally { signer.dispose(); }
    });

    it('rejects incorrect passwords', async () => {
        await expect(Nep6AccountSigner.fromJson(JSON.stringify(vectors.vectors[0]!.wallet), 'wrong password', { context })).rejects.toThrow();
    });

    it.each(['watch-only', 'deployed', 'multisig', 'address', 'script', 'checksum', 'version', 'scrypt'])('rejects %s without selecting a later account', async mutation => {
        const vector = structuredClone(vectors.vectors[0]!);
        const wallet = vector.wallet, account = wallet.accounts[0]!;
        switch (mutation) {
            case 'watch-only': account.key = null; break;
            case 'deployed': account.contract.deployed = true; break;
            case 'multisig': account.contract.parameters.push({ type: 'Signature' }); break;
            case 'address': account.address = wallet.accounts[1]!.address; break;
            case 'script': account.contract.script = 'AA=='; break;
            case 'checksum': account.key = account.key!.slice(0, 57) + '0'; break;
            case 'version': wallet.version = '2.0'; break;
            case 'scrypt': wallet.scrypt.n = 2 ** 30; break;
        }
        await expect(Nep6AccountSigner.fromJson(JSON.stringify(wallet), vector.password, { context })).rejects.toThrow();
    });

    it('rejects missing accounts, cancellation and signing after disposal', async () => {
        const vector = vectors.vectors[0]!, json = JSON.stringify(vector.wallet);
        await expect(Nep6AccountSigner.fromJson(json, vector.password, { context, accountIndex: 2 })).rejects.toThrow();
        await expect(Nep6AccountSigner.fromJson(json, vector.password, { context }, AbortSignal.abort())).rejects.toThrow();
        const signer = await Nep6AccountSigner.fromJson(json, vector.password, { context });
        await expect(signer.sign(new Uint8Array([1]), AbortSignal.abort())).rejects.toThrow();
        signer.dispose();
        await expect(signer.sign(new Uint8Array([1]))).rejects.toThrow('disposed');
    });
});

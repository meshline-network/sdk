import { accountPublicKey, certificateAccountInput, certificateDeviceInput, certificateId, devicePublicKey, encryptionPublicKey, getAccountId,
    signAccount, signDevice, agreeKey, type DeviceCertificate, type NetworkContext } from '@meshline/sdk';

export function messageDevice(context: NetworkContext, accountSeed: number, deviceSeed: number, encryptionSeed = deviceSeed + 1) {
    const accountKey = new Uint8Array(32).fill(accountSeed); const signingKey = new Uint8Array(32).fill(deviceSeed); const encryptionKey = new Uint8Array(32).fill(encryptionSeed);
    const account = getAccountId(`neo:${context.reference}`, accountPublicKey(accountKey));
    let certificate: DeviceCertificate = { account, accountPublicKey: accountPublicKey(accountKey), signingPublicKey: devicePublicKey(signingKey), encryptionPublicKey: encryptionPublicKey(encryptionKey),
        notBefore: 1700000000, expiresAt: 1760000000, deviceSignature: new Uint8Array(64), accountSignature: new Uint8Array(64) };
    certificate = { ...certificate, deviceSignature: signDevice(certificateDeviceInput(certificate, context), signingKey) };
    certificate = { ...certificate, accountSignature: signAccount(certificateAccountInput(certificate, context), accountKey) };
    return { certificate, accountKey, signingKey, encryptionKey, id: certificateId(certificate, context),
        async sign(input: Uint8Array) { return signDevice(input, signingKey); }, async deriveSharedSecret(peer: Uint8Array) { return agreeKey(encryptionKey, peer); } };
}

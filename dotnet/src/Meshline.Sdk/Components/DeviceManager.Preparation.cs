using Meshline.Interactions;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    async Task ValidateLocalKeysAsync(LocalDeviceRecord? local, CancellationToken cancellationToken)
    {
        ClearCachedKeys();
        if (local is null) return;
        var certificate = ReadLocalCertificate(local);
        try
        {
            _signingKey = await ReadPreparationKeyAsync(local, certificate, false, cancellationToken).ConfigureAwait(false);
            _encryptionKey = await ReadPreparationKeyAsync(local, certificate, true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ClearCachedKeys();
            throw;
        }
    }

    async Task<DeviceCertificate> PrepareLocalDeviceAsync(DeviceAuthorizationIntent authorization, TimeSpan validity,
        CancellationToken cancellationToken)
    {
        var operationId = authorization.OperationId;
        ValidateValidity(validity);
        byte[]? signingKey = null;
        byte[]? encryptionKey = null;
        DeviceCertificate certificate;
        var previousLocalDeviceId = authorization.PreviousLocalDeviceId;
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            DeviceCertificate? reusable = null;
            DeviceCertificate? draft;
            LocalDeviceRecord? local;
            DevicePreparationRecord? preparation;
            DatabaseBinding binding;
            await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                binding = await database.Bindings.SingleAsync(cancellationToken).ConfigureAwait(false);
                local = await database.LocalDevices.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                preparation = await database.DevicePreparations.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (preparation?.OperationId is { } pendingOperationId && pendingOperationId != operationId)
                    throw new DeviceAuthorizationException(DeviceAuthorizationFailure.OperationConflict, "A different local device preparation is still unfinished.");
                draft = preparation?.OperationId is null ? null : ProtocolModel.FromJson<DeviceCertificate>(
                    preparation.DraftCertificateJson ?? throw new InvalidDataException("The pending device certificate is missing."))
                    ?? throw new InvalidDataException("The pending device certificate is null.");
                if (local is not null && draft is null)
                {
                    var current = ReadLocalCertificate(local);
                    // Reconcile the exact signed publication before renewing its local
                    // certificate. Expiry must not turn our own request into a conflict.
                    if (current.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds()
                        || await database.SignedRequests.AnyAsync(value => value.Method == "device.state.publish" && value.Pending, cancellationToken).ConfigureAwait(false))
                    {
                        if (current.NotBefore > Clock.UtcNow.ToUnixTimeSeconds())
                            throw new DeviceAuthorizationException(DeviceAuthorizationFailure.NotEffective, "The local certificate is not yet valid. Check the clock before retrying.");
                        Local = current;
                        reusable = current;
                    }
                }
                if (reusable is null)
                {
                    RequireAuthorizationSigner();
                    if (draft is not null)
                    {
                        var draftRecord = new LocalDeviceRecord
                        {
                            DeviceId = draft.GetDeviceId(Context),
                            CertificateJson = draft.ToJson(),
                            ProtectedSigningKey = preparation!.ProtectedSigningKey ?? throw new InvalidDataException("The pending signing key is missing."),
                            ProtectedEncryptionKey = preparation.ProtectedEncryptionKey ?? throw new InvalidDataException("The pending encryption key is missing.")
                        };
                        signingKey = await ReadPreparationKeyAsync(draftRecord, draft, false, cancellationToken).ConfigureAwait(false);
                        encryptionKey = await ReadPreparationKeyAsync(draftRecord, draft, true, cancellationToken).ConfigureAwait(false);
                    }
                    if (draft is null && local is not null)
                    {
                        var previous = ReadLocalCertificate(local);
                        signingKey = _signingKey?.ToArray() ?? await ReadPreparationKeyAsync(local, previous, false, cancellationToken).ConfigureAwait(false);
                        encryptionKey = _encryptionKey?.ToArray() ?? await ReadPreparationKeyAsync(local, previous, true, cancellationToken).ConfigureAwait(false);
                    }
                    else if (draft is null)
                    {
                        signingKey = RandomNumberGenerator.GetBytes(32);
                        encryptionKey = RandomNumberGenerator.GetBytes(32);
                    }
                }
            }
            finally { _deviceGate.Release(); }
            if (reusable is not null)
            {
                await relayClients.RebindDeviceAsync(previousLocalDeviceId, reusable).ConfigureAwait(false);
                return reusable;
            }
            var signer = accountSigner!;
            var protector = secretProtector ?? throw new InvalidOperationException("A secret protector is required.");
            if (draft is null || draft.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            {
                var signingPublicKey = new byte[32];
                var encryptionPublicKey = new byte[32];
                Ed25519.GeneratePublicKey(signingKey.AsSpan(), signingPublicKey.AsSpan());
                X25519.GeneratePublicKey(encryptionKey.AsSpan(), encryptionPublicKey.AsSpan());
                var now = Clock.UtcNow.ToUnixTimeSeconds();
                // An absent declaration on an existing certificate/draft is intentional.
                // Only a brand-new device takes the saved creation intent.
                var pushGateway = draft is not null ? draft.PushGateway
                    : local is not null ? ReadLocalCertificate(local).PushGateway : authorization.InitialPushGateway;
                draft = new DeviceCertificate
                {
                    Account = Options.AccountId,
                    AccountPublicKey = signer.PublicKey,
                    SigningPublicKey = [.. signingPublicKey],
                    EncryptionPublicKey = [.. encryptionPublicKey],
                    NotBefore = now,
                    ExpiresAt = checked(now + (long)validity.TotalSeconds),
                    PushGateway = pushGateway,
                    DeviceSignature = [],
                    AccountSignature = []
                };
                var signature = new byte[Ed25519.SignatureSize];
                Ed25519.Sign(signingKey.AsSpan(), draft.GetDeviceSigningInput(Context), signature.AsSpan());
                draft = draft with { DeviceSignature = [.. signature] };
                var id = draft.GetDeviceId(Context);
                var protectedSigningKey = await protector.ProtectAsync(signingKey, KeyPurpose(id, "signing"), cancellationToken).ConfigureAwait(false);
                var protectedEncryptionKey = await protector.ProtectAsync(encryptionKey, KeyPurpose(id, "encryption"), cancellationToken).ConfigureAwait(false);
                if (preparation is null)
                {
                    preparation = new DevicePreparationRecord
                    {
                        OperationId = operationId,
                        DraftCertificateJson = draft.ToJson(),
                        ProtectedSigningKey = protectedSigningKey,
                        ProtectedEncryptionKey = protectedEncryptionKey
                    };
                    database.DevicePreparations.Add(preparation);
                }
                else
                {
                    preparation.OperationId = operationId;
                    preparation.DraftCertificateJson = draft.ToJson();
                    preparation.ProtectedSigningKey = protectedSigningKey;
                    preparation.ProtectedEncryptionKey = protectedEncryptionKey;
                }
                // Persist both keys and exact signing input before invoking the account signer.
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            certificate = draft with
            {
                AccountSignature = [.. await signer.SignAsync(draft.GetAccountSigningInput(Context), cancellationToken).ConfigureAwait(false)]
            };
            if (certificate.Validate(Context) is { } violation)
                throw new CryptographicException(violation.Message);
            var deviceId = certificate.GetDeviceId(Context);
            if (local is not null && local.DeviceId != deviceId)
                throw new InvalidDataException("A renewal draft must retain the bound local device identity.");
            if (local is null)
            {
                local = new LocalDeviceRecord
                {
                    DeviceId = deviceId,
                    CertificateJson = certificate.ToJson(),
                    ProtectedSigningKey = preparation!.ProtectedSigningKey!,
                    ProtectedEncryptionKey = preparation.ProtectedEncryptionKey!
                };
                database.LocalDevices.Add(local);
            }
            else
            {
                local.CertificateJson = certificate.ToJson();
                local.ProtectedSigningKey = preparation!.ProtectedSigningKey!;
                local.ProtectedEncryptionKey = preparation.ProtectedEncryptionKey!;
            }
            binding.DeviceId = deviceId;
            // Activation and draft consumption commit atomically. A failed save
            // leaves the same draft keys and signing input available to the next attempt.
            preparation!.ActivatedOperationId = operationId;
            preparation.OperationId = null;
            preparation.DraftCertificateJson = null;
            preparation.ProtectedSigningKey = null;
            preparation.ProtectedEncryptionKey = null;
            await CommitLocalDeviceAsync(database, certificate, signingKey, encryptionKey, cancellationToken).ConfigureAwait(false);
            await relayClients.RebindDeviceAsync(previousLocalDeviceId, certificate).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SecretAccessException or IOException)
        {
            throw new DeviceAuthorizationException(DeviceAuthorizationFailure.KeyAccessRequired, "The pending device keys could not be accessed; the operation was retained.", exception)
            {
                KeyIssue = (exception as SecretAccessException)?.Failure ?? SecretAccessFailure.Unknown
            };
        }
        finally
        {
            if (signingKey is not null) CryptographicOperations.ZeroMemory(signingKey);
            if (encryptionKey is not null) CryptographicOperations.ZeroMemory(encryptionKey);
        }
        DeviceChanged?.Invoke(this, new(certificate.GetDeviceId(Context)));
        return certificate;
    }

    // Acquire the SQLite writer before the key gate, matching message transactions
    // that sign/decrypt. Wallet signing and session retirement hold neither lock.
    async Task CommitLocalDeviceAsync(MeshlineDbContext database, DeviceCertificate certificate,
        byte[]? signingKey, byte[]? encryptionKey, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (signingKey is not null && encryptionKey is not null)
            {
                ClearCachedKeys();
                _signingKey = signingKey.ToArray();
                _encryptionKey = encryptionKey.ToArray();
            }
            Local = certificate;
        }
        finally { _deviceGate.Release(); }
    }

    async Task<byte[]> ReadPreparationKeyAsync(LocalDeviceRecord record, DeviceCertificate certificate, bool encryption, CancellationToken cancellationToken)
    {
        try { return await ReadKeyAsync(record, certificate, encryption, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is SecretAccessException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new DeviceAuthorizationException(DeviceAuthorizationFailure.KeyAccessRequired, "The stored device key could not be read. Local data and pending work have been preserved.", exception)
            {
                KeyIssue = (exception as SecretAccessException)?.Failure ?? SecretAccessFailure.Unknown
            };
        }
    }
}

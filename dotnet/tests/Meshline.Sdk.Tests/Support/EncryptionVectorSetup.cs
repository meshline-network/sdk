using Meshline.Models.Protocol;
using Meshline.Storage;

namespace Meshline.Tests.Support;

internal static class EncryptionVectorSetup
{
    internal static TestClient CreateClient() => new(new AccountSigner(ProtocolVectors.Decode(ProtocolVectors.Read("identity-auth").GetProperty("device_certificate").GetProperty("private_key").GetString()!)));
    internal static async Task SeedDeviceAsync(
        TestClient fixture,
        DeviceCertificate certificate,
        byte[] signingKey,
        byte[] encryptionKey,
        CancellationToken cancellationToken)
    {
        var deviceId = certificate.GetDeviceId(TestNetwork.Context);
        await fixture.Database.MigrateAsync();
        await using var database = fixture.Database.Open();
        database.Bindings.Add(new DatabaseBinding
        {
            Id = 1,
            Context = TestNetwork.Context.ToString(),
            AccountId = certificate.Account,
            DeviceId = deviceId
        });
        database.LocalDevices.Add(new LocalDeviceRecord
        {
            DeviceId = deviceId,
            CertificateJson = certificate.ToJson(),
            ProtectedSigningKey = await fixture.Protector.ProtectAsync(signingKey, $"Meshline/device-signing/v1/{TestNetwork.Context}/{certificate.Account}/{deviceId}", cancellationToken),
            ProtectedEncryptionKey = await fixture.Protector.ProtectAsync(encryptionKey, $"Meshline/device-encryption/v1/{TestNetwork.Context}/{certificate.Account}/{deviceId}", cancellationToken)
        });
        await database.SaveChangesAsync(cancellationToken);
    }
}

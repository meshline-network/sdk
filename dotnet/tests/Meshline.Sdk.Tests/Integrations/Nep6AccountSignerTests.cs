using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Integrations;

public sealed class Nep6AccountSignerTests
{
    static readonly NetworkContext Context = new() { Reference = 860833102, Registry = "0x0123456789012345678901234567890123456789" };
    static JsonNode Vector(int index = 0) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Vectors", "nep6-wallets.json")))!["vectors"]![index]!;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Neo_generated_wallets_decrypt_and_sign_exact_input(int vectorIndex, int accountIndex)
    {
        var vector = Vector(vectorIndex);
        var json = vector["wallet"]!.ToJsonString();
        var password = vector["password"]!.GetValue<string>().Normalize(NormalizationForm.FormD);
        using var signer = Nep6AccountSigner.Parse(json, password, Context, accountIndex);
        var input = "Meshline account authorization"u8.ToArray();

        var signature = await signer.SignAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(vector["publicKeys"]![accountIndex]!.GetValue<string>(), Convert.ToHexStringLower(signer.PublicKey.AsSpan()));
        Assert.Equal(vector["wallet"]!["accounts"]![accountIndex]!["address"]!.GetValue<string>(), signer.Address);
        Assert.Equal($"neo:{Context.Reference}:{signer.Address}", signer.AccountId);
        Assert.Equal(64, signature.Length);
        Assert.True(AccountAdapter.VerifySignature(signer.AccountId, signer.PublicKey.AsSpan(), input, signature));
        Assert.False(AccountAdapter.VerifySignature(signer.AccountId, signer.PublicKey.AsSpan(), "changed"u8, signature));
        Assert.Equal(json, vector["wallet"]!.ToJsonString());
    }

    [Fact]
    public void File_loading_selects_first_account_even_when_second_is_default()
    {
        var vector = Vector();
        var json = vector["wallet"]!.ToJsonString();
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            using var signer = Nep6AccountSigner.Load(path, vector["password"]!.GetValue<string>(), Context);
            Assert.Equal(vector["wallet"]!["accounts"]![0]!["address"]!.GetValue<string>(), signer.Address);
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Wrong_password_fails_instead_of_returning_a_different_identity()
    {
        Assert.Throws<CryptographicException>(() => Nep6AccountSigner.Parse(Vector()["wallet"]!.ToJsonString(), "wrong password", Context));
    }

    [Theory]
    [InlineData("watch-only")]
    [InlineData("deployed")]
    [InlineData("multisig")]
    [InlineData("address")]
    [InlineData("script")]
    [InlineData("checksum")]
    [InlineData("version")]
    [InlineData("scrypt")]
    [InlineData("scrypt-overflow")]
    public void Invalid_wallets_are_rejected_without_selecting_another_account(string mutation)
    {
        var vector = Vector();
        var wallet = vector["wallet"]!;
        var account = wallet["accounts"]![0]!;
        switch (mutation)
        {
            case "watch-only": account["key"] = null; break;
            case "deployed": account["contract"]!["deployed"] = true; break;
            case "multisig": account["contract"]!["parameters"]!.AsArray().Add(new JsonObject { ["type"] = "Signature" }); break;
            case "address": account["address"] = wallet["accounts"]![1]!["address"]!.DeepClone(); break;
            case "script": account["contract"]!["script"] = "AA=="; break;
            case "checksum": account["key"] = account["key"]!.GetValue<string>()[..57] + "0"; break;
            case "version": wallet["version"] = "2.0"; break;
            case "scrypt": wallet["scrypt"]!["n"] = 1 << 30; break;
            case "scrypt-overflow":
                wallet["scrypt"]!["n"] = 1 << 30;
                wallet["scrypt"]!["r"] = int.MaxValue;
                wallet["scrypt"]!["p"] = int.MaxValue;
                break;
        }

        Assert.Throws<InvalidDataException>(() => Nep6AccountSigner.Parse(wallet.ToJsonString(), vector["password"]!.GetValue<string>(), Context));
    }

    [Fact]
    public async Task Cancellation_and_disposal_prevent_signing()
    {
        var vector = Vector();
        using var signer = Nep6AccountSigner.Parse(vector["wallet"]!.ToJsonString(), vector["password"]!.GetValue<string>(), Context);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signer.SignAsync(new byte[] { 1 }, canceled.Token));
        signer.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => signer.SignAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));
    }
}

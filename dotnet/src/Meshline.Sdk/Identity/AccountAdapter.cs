using Meshline.Validation;
using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Meshline.Identity;

/// <summary>
/// Derives and validates account identifiers and verifies signatures for supported account namespaces.
/// </summary>
public abstract partial class AccountAdapter
{
    static readonly FrozenDictionary<string, AccountAdapter> _adapters = CreateAdapters();

    [GeneratedRegex(@"\A[-a-z0-9]{3,8}:[-_a-zA-Z0-9]{1,32}:[-.%a-zA-Z0-9]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountIdRegex { get; }
    /// <summary>
    /// The built-in account adapter for the Neo account namespace.
    /// </summary>
    public static AccountAdapter Neo => _adapters["neo"];

    /// <summary>
    /// The account namespace handled by this adapter.
    /// </summary>
    protected abstract string Namespace { get; }

    /// <summary>
    /// Derives the CAIP-10 account identifier for a chain and account public key.
    /// </summary>
    /// <param name="chainId">The CAIP-2 chain identifier whose account address should be derived.</param>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <returns>The account's canonical CAIP-10 identifier.</returns>
    /// <exception cref="NotSupportedException">The account or chain identifier uses an unsupported namespace.</exception>
    /// <exception cref="FormatException">The chain or account identifier is malformed, or the public key is not a valid compressed Neo N3 P-256 key.</exception>
    public static string GetAccountId(string chainId, ReadOnlySpan<byte> publicKey) =>
        For(chainId).GetAccountIdCore(chainId, publicKey);

    /// <summary>
    /// Validates the account identifier using the adapter selected by its namespace.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <exception cref="NotSupportedException">The account or chain identifier uses an unsupported namespace.</exception>
    public static ProtocolViolation? ValidateAccountId(string accountId) =>
        For(accountId).ValidateAccountIdCore(accountId);

    /// <summary>
    /// Determines whether a public key derives the supplied account identifier.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <returns><see langword="true"/> if the public key derives the account identifier; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The account or chain identifier uses an unsupported namespace.</exception>
    /// <exception cref="FormatException">The chain or account identifier is malformed, or the public key is not a valid compressed Neo N3 P-256 key.</exception>
    public static bool MatchesPublicKey(string accountId, ReadOnlySpan<byte> publicKey) =>
        GetAccountId(GetChainId(accountId), publicKey) == accountId;

    /// <summary>
    /// Verifies the signature and its binding to the supplied identity.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="signature">The signature bytes to verify against the supplied input.</param>
    /// <returns><see langword="true"/> if the signature is valid and the public key matches the supplied identifier; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The account or chain identifier uses an unsupported namespace.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The cryptographic provider cannot create or use the P-256 signature verifier; an invalid signature normally returns false.</exception>
    public static bool VerifySignature(string accountId, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        var adapter = For(accountId);
        return adapter.ValidateAccountIdCore(accountId) is null
            && adapter.VerifySignature(publicKey, data, signature)
            && MatchesPublicKey(accountId, publicKey);
    }

    /// <summary>
    /// Verifies the signature using the public key and identity algorithm.
    /// </summary>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="signature">The signature bytes to verify against the supplied input.</param>
    /// <returns><see langword="true"/> if the signature is valid; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The cryptographic provider cannot create or use the P-256 signature verifier; an invalid signature normally returns false.</exception>
    public abstract bool VerifySignature(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
    /// <summary>
    /// Derives an account identifier using this adapter's chain-specific address rules.
    /// </summary>
    /// <param name="chainId">The CAIP-2 chain identifier whose account address should be derived.</param>
    /// <param name="publicKey">The public key in the encoding required by the identity's signature algorithm.</param>
    /// <returns>The derived account identifier in the adapter's namespace.</returns>
    /// <exception cref="FormatException">The chain or account identifier is malformed, or the public key is not a valid compressed Neo N3 P-256 key.</exception>
    protected abstract string GetAccountIdCore(string chainId, ReadOnlySpan<byte> publicKey);
    /// <summary>
    /// Validates an account identifier using this adapter's chain-specific address rules.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    protected abstract ProtocolViolation? ValidateAccountIdCore(string accountId);

    /// <summary>
    /// Selects the registered adapter for an account namespace, chain identifier, or account identifier.
    /// </summary>
    /// <param name="id">An account namespace, CAIP-2 chain identifier, or CAIP-10 account identifier.</param>
    /// <returns>The adapter registered for the identifier's namespace.</returns>
    /// <exception cref="NotSupportedException">No adapter is registered for the identifier namespace.</exception>
    public static AccountAdapter For(string id)
    {
        var separator = id.IndexOf(':');
        var accountNamespace = separator < 0 ? id : id[..separator];
        return _adapters.TryGetValue(accountNamespace, out var adapter)
            ? adapter
            : throw new NotSupportedException($"Account namespace '{accountNamespace}' is not supported.");
    }

    /// <summary>
    /// Extracts the CAIP-2 chain identifier from a syntactically valid CAIP-10 account identifier.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <returns>The chain portion of the supplied account identifier.</returns>
    /// <exception cref="FormatException">The input is not a syntactically valid CAIP-10 account identifier.</exception>
    public static string GetChainId(string accountId)
    {
        if (accountId is null || !AccountIdRegex.IsMatch(accountId))
            throw new FormatException("Expected a CAIP-10 account identifier.");

        return accountId[..accountId.LastIndexOf(':')];
    }

    static FrozenDictionary<string, AccountAdapter> CreateAdapters()
    {
        var adapters = new Dictionary<string, AccountAdapter>(StringComparer.Ordinal);
        foreach (var type in typeof(AccountAdapter).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !type.IsSubclassOf(typeof(AccountAdapter)))
                continue;

            var adapter = (AccountAdapter)Activator.CreateInstance(type, nonPublic: true)!;
            var accountNamespace = adapter.Namespace;
            if (!adapters.TryAdd(accountNamespace, adapter))
                throw new InvalidOperationException($"Account adapters '{adapters[accountNamespace].GetType().FullName}' and '{type.FullName}' declare the same namespace '{accountNamespace}'.");
        }

        return adapters.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

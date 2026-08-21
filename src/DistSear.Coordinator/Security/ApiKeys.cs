using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DistSear.Coordinator.Security;

/// <summary>A service credential and the groups it grants.</summary>
public sealed record ApiKeyRecord(string Name, IReadOnlyList<string> Groups, bool CanWrite);

/// <summary>
/// Looks up an API key by its hash.
///
/// Only hashes are ever stored. A leaked store of raw keys is a leaked set of credentials, whereas
/// hashes are useless to an attacker without the original — the same reason passwords are not stored
/// in the clear.
/// </summary>
public interface IApiKeyStore
{
    Task<ApiKeyRecord?> FindAsync(string presentedKey, CancellationToken cancellationToken);
}

public static class ApiKeyHasher
{
    /// <summary>
    /// SHA-256 of the key. A deliberately fast hash, unlike a password hash: API keys are
    /// high-entropy random strings, so there is no dictionary to attack and no reason to pay a work
    /// factor on every request.
    /// </summary>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Generates a new key with 256 bits of entropy.</summary>
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// In-memory key store, for local development and tests. The Azure deployment replaces this with a
/// Cosmos-backed implementation reading the <c>apikeys</c> container.
/// </summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<string, ApiKeyRecord> _byHash = new(StringComparer.Ordinal);

    public void Add(string key, ApiKeyRecord record) => _byHash[ApiKeyHasher.Hash(key)] = record;

    public Task<ApiKeyRecord?> FindAsync(string presentedKey, CancellationToken cancellationToken) =>
        Task.FromResult(_byHash.GetValueOrDefault(ApiKeyHasher.Hash(presentedKey)));
}

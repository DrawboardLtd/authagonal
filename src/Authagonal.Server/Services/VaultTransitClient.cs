using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Authagonal.Server.Services;

/// <summary>
/// HTTP client for HashiCorp Vault Transit secrets engine.
/// Handles sign, verify, key creation, rotation, and public key export.
/// Authagonal uses ECDSA P-256 (ES256) Transit keys; the sign endpoint requests
/// JWS-format signatures (raw R‖S) so the bytes can be used directly as the
/// JWT signature without DER→P1363 conversion.
/// </summary>
public class VaultTransitClient
{
    public const string EcdsaP256 = "ecdsa-p256";

    private readonly HttpClient _client;
    private readonly ILogger<VaultTransitClient> _logger;

    public VaultTransitClient(HttpClient client, ILogger<VaultTransitClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Sign data using a Transit key. Returns raw R‖S signature bytes (JWS-marshaled).</summary>
    public virtual async Task<byte[]> SignAsync(string keyName, byte[] data, CancellationToken ct = default)
    {
        var input = Convert.ToBase64String(data);
        var payload = JsonSerializer.Serialize(
            new VaultSignRequest { Input = input, HashAlgorithm = "sha2-256", MarshalingAlgorithm = "jws" },
            AuthagonalJsonContext.Default.VaultSignRequest);

        var response = await PostAsync($"/v1/transit/sign/{keyName}/sha2-256", payload, ct);
        var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseSignResponse);

        var sig = result?.Data?.Signature
            ?? throw new InvalidOperationException($"Vault Transit sign returned no signature for key '{keyName}'");

        // Vault returns "vault:v{version}:{base64sig}"
        var parts = sig.Split(':');
        if (parts.Length != 3)
            throw new InvalidOperationException($"Unexpected Vault signature format: {sig}");

        return Convert.FromBase64String(parts[2]);
    }

    /// <summary>Verify a signature using a Transit key. <paramref name="signature"/> must be JWS-marshaled (raw R‖S).</summary>
    public virtual async Task<bool> VerifyAsync(string keyName, byte[] data, byte[] signature, CancellationToken ct = default)
    {
        var input = Convert.ToBase64String(data);
        var sig = $"vault:v1:{Convert.ToBase64String(signature)}";
        var payload = JsonSerializer.Serialize(
            new VaultVerifyRequest { Input = input, Signature = sig, HashAlgorithm = "sha2-256", MarshalingAlgorithm = "jws" },
            AuthagonalJsonContext.Default.VaultVerifyRequest);

        var response = await PostAsync($"/v1/transit/verify/{keyName}/sha2-256", payload, ct);
        var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseVerifyResponse);
        return result?.Data?.Valid ?? false;
    }

    /// <summary>Create a new Transit key.</summary>
    public virtual async Task CreateKeyAsync(string keyName, string type = EcdsaP256, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(
            new VaultCreateKeyRequest { Type = type },
            AuthagonalJsonContext.Default.VaultCreateKeyRequest);
        await PostAsync($"/v1/transit/keys/{keyName}", payload, ct);
        _logger.LogInformation("Created Vault Transit key {KeyName} (type={Type})", keyName, type);
    }

    /// <summary>
    /// Idempotently ensure a Transit key exists with the desired type. If a key with the
    /// same name exists with a different type, it is deleted and recreated — Vault Transit
    /// keys cannot be retyped in place. Used during provisioning and the one-time
    /// rsa-2048 → ecdsa-p256 migration; subsequent calls are no-ops.
    /// </summary>
    public virtual async Task EnsureKeyTypeAsync(string keyName, string desiredType = EcdsaP256, CancellationToken ct = default)
    {
        var info = await ReadKeyAsync(keyName, ct);
        if (info is null)
        {
            await CreateKeyAsync(keyName, desiredType, ct);
            return;
        }

        if (string.Equals(info.Type, desiredType, StringComparison.OrdinalIgnoreCase))
            return;

        _logger.LogWarning(
            "Vault Transit key {KeyName} has type {ExistingType}; recreating as {DesiredType}",
            keyName, info.Type, desiredType);

        await DeleteKeyAsync(keyName, ct);
        await CreateKeyAsync(keyName, desiredType, ct);
    }

    /// <summary>Rotate a Transit key (creates a new version).</summary>
    public virtual async Task RotateKeyAsync(string keyName, CancellationToken ct = default)
    {
        await PostAsync($"/v1/transit/keys/{keyName}/rotate", "{}", ct);
        _logger.LogInformation("Rotated Vault Transit key {KeyName}", keyName);
    }

    /// <summary>Delete a Transit key (must enable deletion_allowed first).</summary>
    public virtual async Task DeleteKeyAsync(string keyName, CancellationToken ct = default)
    {
        // Enable deletion
        var configPayload = JsonSerializer.Serialize(
            new VaultKeyConfigRequest { DeletionAllowed = true },
            AuthagonalJsonContext.Default.VaultKeyConfigRequest);
        await PostAsync($"/v1/transit/keys/{keyName}/config", configPayload, ct);
        // Delete
        await DeleteAsync($"/v1/transit/keys/{keyName}", ct);
        _logger.LogInformation("Deleted Vault Transit key {KeyName}", keyName);
    }

    /// <summary>Read key metadata including all versions and their public keys.</summary>
    public virtual async Task<TransitKeyInfo?> ReadKeyAsync(string keyName, CancellationToken ct = default)
    {
        try
        {
            var response = await GetAsync($"/v1/transit/keys/{keyName}", ct);
            var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseTransitKeyInfo);
            return result?.Data;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Check if a Transit key exists.</summary>
    public virtual async Task<bool> KeyExistsAsync(string keyName, CancellationToken ct = default)
    {
        return await ReadKeyAsync(keyName, ct) is not null;
    }

    private async Task<string> PostAsync(string path, string jsonBody, CancellationToken ct)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(path, content, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Vault POST {Path} failed: {Status} {Body}", path, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        return body;
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var response = await _client.DeleteAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _client.GetAsync(path, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            response.EnsureSuccessStatusCode();

        return body;
    }

}

// ── Vault DTOs ──────────────────────────────────────────────────────

internal sealed class VaultResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

internal sealed class SignResponse
{
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

internal sealed class VerifyResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }
}

internal sealed class VaultSignRequest
{
    [JsonPropertyName("input")]
    public required string Input { get; set; }
    [JsonPropertyName("hash_algorithm")]
    public required string HashAlgorithm { get; set; }
    [JsonPropertyName("marshaling_algorithm")]
    public required string MarshalingAlgorithm { get; set; }
}

internal sealed class VaultVerifyRequest
{
    [JsonPropertyName("input")]
    public required string Input { get; set; }
    [JsonPropertyName("signature")]
    public required string Signature { get; set; }
    [JsonPropertyName("hash_algorithm")]
    public required string HashAlgorithm { get; set; }
    [JsonPropertyName("marshaling_algorithm")]
    public required string MarshalingAlgorithm { get; set; }
}

internal sealed class VaultCreateKeyRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}

internal sealed class VaultKeyConfigRequest
{
    [JsonPropertyName("deletion_allowed")]
    public bool DeletionAllowed { get; set; }
}

/// <summary>Transit key metadata from Vault.</summary>
public sealed class TransitKeyInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("latest_version")]
    public int LatestVersion { get; set; }

    [JsonPropertyName("min_decryption_version")]
    public int MinDecryptionVersion { get; set; }

    [JsonPropertyName("min_encryption_version")]
    public int MinEncryptionVersion { get; set; }

    /// <summary>
    /// Key versions — keyed by version number string.
    /// Each version contains the public key for RSA keys.
    /// </summary>
    [JsonPropertyName("keys")]
    public Dictionary<string, TransitKeyVersion>? Keys { get; set; }
}

public sealed class TransitKeyVersion
{
    [JsonPropertyName("public_key")]
    public string? PublicKey { get; set; }

    [JsonPropertyName("creation_time")]
    public string? CreationTime { get; set; }
}

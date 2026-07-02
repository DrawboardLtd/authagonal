using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

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
    /// <summary>Symmetric key type for Transit encrypt/decrypt (encryption-as-a-service). Distinct from
    /// the ECDSA signing keys — a key can't both sign and encrypt, so encryption uses its own key.</summary>
    public const string Aes256Gcm96 = "aes256-gcm96";

    private readonly HttpClient _client;
    private readonly ILogger<VaultTransitClient> _logger;

    public VaultTransitClient(HttpClient client, ILogger<VaultTransitClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Sign data using a Transit key. Returns raw R‖S signature bytes (JWS-marshaled).</summary>
    public virtual async Task<byte[]> SignAsync(string keyName, byte[] data, CancellationToken ct = default, int keyVersion = 0)
    {
        var input = Convert.ToBase64String(data);
        var payload = JsonSerializer.Serialize(
            // key_version omitted (null) => Vault signs with the latest version; a specific version is
            // passed for publish-ahead rotation so the signature matches the advertised kid.
            new VaultSignRequest { Input = input, HashAlgorithm = "sha2-256", MarshalingAlgorithm = "jws", KeyVersion = keyVersion > 0 ? keyVersion : null },
            AuthagonalJsonContext.Default.VaultSignRequest);

        var response = await PostAsync($"/v1/transit/sign/{keyName}/sha2-256", payload, ct);
        var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseSignResponse);

        var sig = result?.Data?.Signature
            ?? throw new InvalidOperationException($"Vault Transit sign returned no signature for key '{keyName}'");

        // Vault returns "vault:v{version}:{base64urlsig}" — with marshaling_algorithm=jws
        // the signature is base64url-encoded (per JWS spec), not standard base64.
        var parts = sig.Split(':');
        if (parts.Length != 3)
            throw new InvalidOperationException($"Unexpected Vault signature format: {sig}");

        return Base64UrlEncoder.DecodeBytes(parts[2]);
    }

    /// <summary>Verify a signature using a Transit key. <paramref name="signature"/> must be JWS-marshaled (raw R‖S).</summary>
    public virtual async Task<bool> VerifyAsync(string keyName, byte[] data, byte[] signature, CancellationToken ct = default)
    {
        var input = Convert.ToBase64String(data);
        // jws marshaling expects the signature back in base64url
        var sig = $"vault:v1:{Base64UrlEncoder.Encode(signature)}";
        var payload = JsonSerializer.Serialize(
            new VaultVerifyRequest { Input = input, Signature = sig, HashAlgorithm = "sha2-256", MarshalingAlgorithm = "jws" },
            AuthagonalJsonContext.Default.VaultVerifyRequest);

        var response = await PostAsync($"/v1/transit/verify/{keyName}/sha2-256", payload, ct);
        var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseVerifyResponse);
        return result?.Data?.Valid ?? false;
    }

    /// <summary>
    /// Encrypt plaintext with a Transit key (must be a symmetric <see cref="Aes256Gcm96"/> key). Returns
    /// the Vault ciphertext token ("vault:v{version}:{base64}") — store it verbatim; <see cref="DecryptAsync"/>
    /// reverses it. The key material never leaves Vault, and the version prefix lets Vault rewrap on rotation.
    /// </summary>
    public virtual async Task<string> EncryptAsync(string keyName, byte[] plaintext, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(
            new VaultEncryptRequest { Plaintext = Convert.ToBase64String(plaintext) },
            AuthagonalJsonContext.Default.VaultEncryptRequest);

        var response = await PostAsync($"/v1/transit/encrypt/{keyName}", payload, ct);
        var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseEncryptResponse);

        return result?.Data?.Ciphertext
            ?? throw new InvalidOperationException($"Vault Transit encrypt returned no ciphertext for key '{keyName}'");
    }

    /// <summary>Decrypt a Vault ciphertext token ("vault:v{version}:...") produced by <see cref="EncryptAsync"/>
    /// with the same key. Returns the original plaintext bytes.</summary>
    public virtual async Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(
            new VaultDecryptRequest { Ciphertext = ciphertext },
            AuthagonalJsonContext.Default.VaultDecryptRequest);

        var response = await PostAsync($"/v1/transit/decrypt/{keyName}", payload, ct);
        var result = JsonSerializer.Deserialize(response, AuthagonalJsonContext.Default.VaultResponseDecryptResponse);

        var b64 = result?.Data?.Plaintext
            ?? throw new InvalidOperationException($"Vault Transit decrypt returned no plaintext for key '{keyName}'");
        return Convert.FromBase64String(b64);
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

internal sealed class EncryptResponse
{
    [JsonPropertyName("ciphertext")]
    public string? Ciphertext { get; set; }
}

internal sealed class DecryptResponse
{
    [JsonPropertyName("plaintext")]
    public string? Plaintext { get; set; }
}

internal sealed class VaultSignRequest
{
    [JsonPropertyName("input")]
    public required string Input { get; set; }
    [JsonPropertyName("hash_algorithm")]
    public required string HashAlgorithm { get; set; }
    [JsonPropertyName("marshaling_algorithm")]
    public required string MarshalingAlgorithm { get; set; }
    // Sign with a specific key version (publish-ahead rotation). Omitted => Vault uses the latest version.
    [JsonPropertyName("key_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeyVersion { get; set; }
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

internal sealed class VaultEncryptRequest
{
    [JsonPropertyName("plaintext")]
    public required string Plaintext { get; set; }
}

internal sealed class VaultDecryptRequest
{
    [JsonPropertyName("ciphertext")]
    public required string Ciphertext { get; set; }
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

/// <summary>
/// A single Transit key version. Vault serializes this as an OBJECT
/// (<c>{ public_key, creation_time }</c>) for asymmetric keys (ecdsa-p256 / rsa) but as a bare
/// NUMBER (the unix-seconds creation timestamp) for symmetric keys (aes256-gcm96). The custom
/// converter tolerates both shapes so <see cref="VaultTransitClient.ReadKeyAsync"/> — and everything
/// downstream of it (KeyExists / EnsureKeyType) — works for encryption keys too, not just signing keys.
/// </summary>
[JsonConverter(typeof(TransitKeyVersionConverter))]
public sealed class TransitKeyVersion
{
    [JsonPropertyName("public_key")]
    public string? PublicKey { get; set; }

    [JsonPropertyName("creation_time")]
    public string? CreationTime { get; set; }
}

/// <summary>Reads <see cref="TransitKeyVersion"/> from either Vault shape (object for asymmetric keys, bare unix-seconds number for symmetric keys).</summary>
public sealed class TransitKeyVersionConverter : JsonConverter<TransitKeyVersion>
{
    public override TransitKeyVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Symmetric keys (aes256-gcm96): the version value is just the unix-seconds creation timestamp.
        if (reader.TokenType == JsonTokenType.Number)
            return new TransitKeyVersion { CreationTime = reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) };

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new TransitKeyVersion();
        }

        // Asymmetric keys: { "public_key": "...", "creation_time": "..." } (plus fields we ignore).
        var version = new TransitKeyVersion();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "public_key":
                    version.PublicKey = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case "creation_time":
                    version.CreationTime = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString(),
                        JsonTokenType.Number => reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => null,
                    };
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return version;
    }

    public override void Write(Utf8JsonWriter writer, TransitKeyVersion value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.PublicKey is not null) writer.WriteString("public_key", value.PublicKey);
        if (value.CreationTime is not null) writer.WriteString("creation_time", value.CreationTime);
        writer.WriteEndObject();
    }
}

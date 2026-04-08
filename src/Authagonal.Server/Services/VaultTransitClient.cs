using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Authagonal.Server.Services;

/// <summary>
/// HTTP client for HashiCorp Vault Transit secrets engine.
/// Handles sign, verify, key creation, rotation, and public key export.
/// </summary>
public sealed class VaultTransitClient
{
    private readonly HttpClient _client;
    private readonly ILogger<VaultTransitClient> _logger;

    public VaultTransitClient(HttpClient client, ILogger<VaultTransitClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Sign data using a Transit key. Returns raw signature bytes.</summary>
    public async Task<byte[]> SignAsync(string keyName, byte[] data, CancellationToken ct = default)
    {
        var input = Convert.ToBase64String(data);
        var payload = JsonSerializer.Serialize(new { input, hash_algorithm = "sha2-256", signature_algorithm = "pkcs1v15" });

        var response = await PostAsync($"/v1/transit/sign/{keyName}/sha2-256", payload, ct);
        var result = JsonSerializer.Deserialize<VaultResponse<SignResponse>>(response);

        var sig = result?.Data?.Signature
            ?? throw new InvalidOperationException($"Vault Transit sign returned no signature for key '{keyName}'");

        // Vault returns "vault:v{version}:{base64sig}"
        var parts = sig.Split(':');
        if (parts.Length != 3)
            throw new InvalidOperationException($"Unexpected Vault signature format: {sig}");

        return Convert.FromBase64String(parts[2]);
    }

    /// <summary>Create a new Transit key.</summary>
    public async Task CreateKeyAsync(string keyName, string type = "rsa-2048", CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { type });
        await PostAsync($"/v1/transit/keys/{keyName}", payload, ct);
        _logger.LogInformation("Created Vault Transit key {KeyName} (type={Type})", keyName, type);
    }

    /// <summary>Rotate a Transit key (creates a new version).</summary>
    public async Task RotateKeyAsync(string keyName, CancellationToken ct = default)
    {
        await PostAsync($"/v1/transit/keys/{keyName}/rotate", "{}", ct);
        _logger.LogInformation("Rotated Vault Transit key {KeyName}", keyName);
    }

    /// <summary>Read key metadata including all versions and their public keys.</summary>
    public async Task<TransitKeyInfo?> ReadKeyAsync(string keyName, CancellationToken ct = default)
    {
        try
        {
            var response = await GetAsync($"/v1/transit/keys/{keyName}", ct);
            var result = JsonSerializer.Deserialize<VaultResponse<TransitKeyInfo>>(response);
            return result?.Data;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Check if a Transit key exists.</summary>
    public async Task<bool> KeyExistsAsync(string keyName, CancellationToken ct = default)
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

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _client.GetAsync(path, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            response.EnsureSuccessStatusCode();

        return body;
    }

    // ── Response DTOs ────────────────────────────────────────────────

    private sealed class VaultResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed class SignResponse
    {
        [JsonPropertyName("signature")]
        public string? Signature { get; set; }
    }
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

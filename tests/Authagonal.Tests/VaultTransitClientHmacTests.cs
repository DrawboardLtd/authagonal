using System.Net;
using System.Text;
using Authagonal.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Authagonal.Tests;

/// <summary>
/// Increment 2 of searchable PII encryption: the keyed-HMAC primitive behind blind indexes.
/// These exercise the HTTP/JSON plumbing of <see cref="VaultTransitClient.HmacAsync"/> and
/// <see cref="VaultTransitClient.HmacBatchAsync"/> against a stub Vault (right path, base64 input,
/// token parse, batch order + count guard, empty short-circuit). HMAC determinism itself is a Vault
/// property, exercised end-to-end elsewhere.
/// </summary>
public class VaultTransitClientHmacTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, string> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string LastBody = "";
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var json = respond(request, LastBody);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static VaultTransitClient NewClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://vault.test") },
            NullLogger<VaultTransitClient>.Instance);

    [Fact]
    public async Task Hmac_HitsHmacEndpoint_SendsBase64Input_ReturnsToken()
    {
        var handler = new StubHandler((_, _) => """{"data":{"hmac":"vault:v1:Zm9v"}}""");
        var client = NewClient(handler);

        var token = await client.HmacAsync("idx-acme", Encoding.UTF8.GetBytes("SMITH"));

        Assert.Equal("vault:v1:Zm9v", token);
        Assert.Equal("/v1/transit/hmac/idx-acme/sha2-256", handler.LastRequest!.RequestUri!.AbsolutePath);
        // Input is base64-encoded in the request body, per Vault's contract.
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("SMITH")), handler.LastBody);
    }

    [Fact]
    public async Task HmacBatch_ReturnsTokensInInputOrder()
    {
        var handler = new StubHandler((_, _) =>
            """{"data":{"batch_results":[{"hmac":"vault:v1:AA"},{"hmac":"vault:v1:BB"},{"hmac":"vault:v1:CC"}]}}""");
        var client = NewClient(handler);

        var tokens = await client.HmacBatchAsync("idx-acme",
        [
            Encoding.UTF8.GetBytes("SM"),
            Encoding.UTF8.GetBytes("SMI"),
            Encoding.UTF8.GetBytes("SMITH"),
        ]);

        Assert.Equal(new[] { "vault:v1:AA", "vault:v1:BB", "vault:v1:CC" }, tokens);
        Assert.Contains("batch_input", handler.LastBody);
        Assert.Equal("/v1/transit/hmac/idx-acme/sha2-256", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task HmacBatch_Empty_MakesNoRequest()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("should not call Vault for empty batch"));
        var client = NewClient(handler);

        var tokens = await client.HmacBatchAsync("idx-acme", []);

        Assert.Empty(tokens);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HmacBatch_CountMismatch_Throws()
    {
        // Vault returns fewer results than inputs — a corrupt/misordered response must not silently
        // mis-map tokens to the wrong values.
        var handler = new StubHandler((_, _) => """{"data":{"batch_results":[{"hmac":"vault:v1:AA"}]}}""");
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.HmacBatchAsync("idx-acme", [Encoding.UTF8.GetBytes("A"), Encoding.UTF8.GetBytes("B")]));
    }
}

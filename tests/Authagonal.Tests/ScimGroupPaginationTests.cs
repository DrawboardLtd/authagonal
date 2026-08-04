using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Models;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// The `[43]` pagination cluster: what `ServiceProviderConfig` promises has to be true of BOTH collections.
/// </summary>
/// <remarks>
/// <para>
/// <c>pagination.index</c> has now been wrong in both directions, and nothing asserted it either time. It said
/// <c>false</c> while <c>/Groups</c> paged only by <c>startIndex</c>, so an integrator following the
/// advertisement could never read past the first page of groups. It was then flipped to <c>true</c> — but
/// <c>/Users</c> refuses <c>startIndex &gt; 1</c> outright with a 400, so <c>true</c> was a lie about the other
/// collection and a client that selected index paging provider-wide aborted its user sync after 100 users.
/// draft-ietf-scim-cursor-pagination §4 has no per-endpoint qualifier, so the only honest value is one that
/// holds everywhere.
/// </para>
/// <para>
/// The other two gaps were on <c>/Groups</c> alone: a truncated filtered scan returned neither
/// <c>totalResults</c> nor <c>nextCursor</c>, so a client could not tell "nothing matched" from "the scan window
/// ran out before reaching the matches"; and RFC 7644 §3.4.2.4 <c>count</c> handling existed on <c>/Users</c> and
/// not here, so <c>count=0</c> returned a full group including its entire members array.
/// </para>
/// </remarks>
public sealed class ScimGroupPaginationTests : IAsyncDisposable
{
    private readonly AuthagonalTestFactory _factory = new();

    private async Task<HttpClient> ScimClientAsync()
    {
        await _factory.SeedTestDataAsync();
        var (_, rawToken) = await _factory.SeedScimClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    /// <summary>Seeded through the store, not the API — 2,000+ HTTP creates would dominate the run.</summary>
    private async Task SeedGroupsAsync(int count, string displayNamePrefix, DateTimeOffset createdAt)
    {
        for (var i = 0; i < count; i++)
        {
            await _factory.ScimGroupStore.CreateAsync(new ScimGroup
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = $"{displayNamePrefix}{i}",
                OrganizationId = "scim-client",
                CreatedAt = createdAt.AddMilliseconds(i),
            });
        }
    }

    // ── the advertisement, and both collections honouring it ─────────────────

    [Fact]
    public async Task ServiceProviderConfig_AdvertisesCursorAndNotIndex()
    {
        var client = await ScimClientAsync();

        var config = await (await client.GetAsync("/scim/v2/ServiceProviderConfig"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var pagination = config.GetProperty("pagination");
        Assert.True(pagination.GetProperty("cursor").GetBoolean());
        Assert.False(pagination.GetProperty("index").GetBoolean());
    }

    /// <summary>
    /// The claim is provider-level, so it has to hold on the collection that refuses index paging.
    /// </summary>
    [Fact]
    public async Task UsersRefusesIndexPaging_WhichIsWhyItCannotBeAdvertised()
    {
        var client = await ScimClientAsync();

        var response = await client.GetAsync("/scim/v2/Users?startIndex=101&count=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("startIndex", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>And cursor paging, which IS advertised, has to work on both.</summary>
    [Fact]
    public async Task BothCollectionsIssueAFollowableCursor()
    {
        var client = await ScimClientAsync();
        await SeedGroupsAsync(3, "grp-", DateTimeOffset.UtcNow.AddMinutes(-5));

        var groups = await (await client.GetAsync("/scim/v2/Groups?count=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var cursor = groups.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var page2 = await client.GetAsync($"/scim/v2/Groups?count=2&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var page2Json = await page2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page2Json.GetProperty("Resources").GetArrayLength());

        // /Users is cursor-only, so it must at least accept the parameter.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scim/v2/Users?count=1")).StatusCode);
    }

    [Fact]
    public async Task AForgedCursorIsRefused()
    {
        var client = await ScimClientAsync();

        var response = await client.GetAsync("/scim/v2/Groups?cursor=" + Uri.EscapeDataString("bm90LWEtY3Vyc29y"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── RFC 7644 §3.4.2.4 count handling ─────────────────────────────────────

    /// <summary>
    /// <c>count=0</c> asks how many there are and for none of them — it returned one full group, members and all.
    /// </summary>
    [Fact]
    public async Task CountZero_ReturnsTheTotalAndNoResources()
    {
        var client = await ScimClientAsync();
        await SeedGroupsAsync(3, "grp-", DateTimeOffset.UtcNow.AddMinutes(-5));

        var json = await (await client.GetAsync("/scim/v2/Groups?count=0"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, json.GetProperty("itemsPerPage").GetInt32());
        Assert.Equal(0, json.GetProperty("Resources").GetArrayLength());
        Assert.Equal(3, json.GetProperty("totalResults").GetInt32());
    }

    /// <summary>A negative count was silently clamped to 1, so `count=-1` returned a resource.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task ANegativeCountIsRefused(int count)
    {
        var client = await ScimClientAsync();

        var response = await client.GetAsync($"/scim/v2/Groups?count={count}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("negative", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Same two answers as the sibling collection, which is the point.</summary>
    [Fact]
    public async Task UsersAnswersCountTheSameWay()
    {
        var client = await ScimClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/scim/v2/Users?count=-1")).StatusCode);

        var json = await (await client.GetAsync("/scim/v2/Users?count=0"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("itemsPerPage").GetInt32());
    }

    // ── a truncated filtered scan must say so ────────────────────────────────

    /// <summary>
    /// The matching group sorts past the scan window, so the first page matches nothing — and must not look
    /// like an empty result.
    /// </summary>
    /// <remarks>
    /// The filtered branch bounds itself to 10 windows of 200 rows. With the match beyond that, it previously
    /// returned <c>{"itemsPerPage":0,"Resources":[]}</c> with no <c>totalResults</c> and no <c>nextCursor</c>:
    /// a cursor-following connector saw no cursor and concluded the filtered set was empty, and a
    /// totalResults-reading one saw the field absent. Both silently missed the group.
    /// </remarks>
    [Fact]
    public async Task ATruncatedFilteredScan_IssuesACursorInsteadOfLookingEmpty()
    {
        var client = await ScimClientAsync();

        var basis = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedGroupsAsync(2_050, "filler-", basis);
        await _factory.ScimGroupStore.CreateAsync(new ScimGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "needle",
            OrganizationId = "scim-client",
            CreatedAt = basis.AddDays(1), // sorts last, past the 10 × 200 window
        });

        var first = await (await client.GetAsync("/scim/v2/Groups?filter=" + Uri.EscapeDataString("displayName eq \"needle\"")))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Nothing matched yet — and that is exactly when the old response was indistinguishable from "empty".
        Assert.Equal(0, first.GetProperty("Resources").GetArrayLength());
        Assert.True(
            first.TryGetProperty("nextCursor", out var cursorElement)
            && cursorElement.ValueKind == JsonValueKind.String,
            "a truncated filtered scan returned no nextCursor, so the client cannot tell it was truncated");

        // totalResults must stay absent while the scan is incomplete — claiming 0 would be the same lie.
        Assert.True(
            !first.TryGetProperty("totalResults", out var total) || total.ValueKind == JsonValueKind.Null,
            "totalResults was reported for an incomplete scan");

        // Following the cursor makes progress and eventually finds it.
        var cursor = cursorElement.GetString();
        var found = false;
        for (var page = 0; page < 40 && cursor is not null; page++)
        {
            var next = await (await client.GetAsync(
                    "/scim/v2/Groups?filter=" + Uri.EscapeDataString("displayName eq \"needle\"")
                    + "&cursor=" + Uri.EscapeDataString(cursor)))
                .Content.ReadFromJsonAsync<JsonElement>();

            if (next.GetProperty("Resources").GetArrayLength() > 0)
            {
                found = true;
                break;
            }

            cursor = next.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }

        Assert.True(found, "following nextCursor never reached the matching group");
    }

    /// <summary>The control: a completed filtered scan reports its total and issues no cursor.</summary>
    [Fact]
    public async Task ACompletedFilteredScan_ReportsItsTotalAndStops()
    {
        var client = await ScimClientAsync();
        await SeedGroupsAsync(5, "eng-", DateTimeOffset.UtcNow.AddMinutes(-5));

        var json = await (await client.GetAsync("/scim/v2/Groups?filter=" + Uri.EscapeDataString("displayName sw \"eng-\"")))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(5, json.GetProperty("Resources").GetArrayLength());
        Assert.Equal(5, json.GetProperty("totalResults").GetInt32());
        Assert.True(
            !json.TryGetProperty("nextCursor", out var c) || c.ValueKind == JsonValueKind.Null,
            "a completed scan issued a cursor, so a client would keep polling");
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

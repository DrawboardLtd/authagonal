using System.Text.Json;
using System.Text.Json.Serialization;

namespace Authagonal.Migration;

/// <summary>
/// Source-generated serialization metadata for the two types this package puts on the wire or in a row.
/// </summary>
/// <remarks>
/// <para>
/// Every other shipped Authagonal package declares <c>IsTrimmable</c>, and this one did not — the single
/// exception in the solution, and the only one whose serialization went through the reflection-based
/// resolver. That combination is what made the omission load-bearing rather than cosmetic: a host that
/// trims (or publishes AOT) gets a <c>Authagonal.Migration</c> that has never been trim-analyzed, and the
/// first thing to break is the report. <c>JsonSerializer.Serialize(report)</c> writes the marker that
/// <c>BlocksRerun</c> and the status endpoint both read, so losing the property metadata turns the
/// migration's own record of what it did into <c>{}</c> — while the run itself reports success.
/// </para>
/// <para>
/// <see cref="JsonSerializerDefaults.Web"/> rather than the plain defaults, because the endpoint's casing is
/// the one externally observable contract here: <c>Results.Json</c> already serializes through
/// <c>JsonSerializerOptions.Web</c>, so a context on the default options would have quietly renamed every
/// field of a documented admin response from <c>dryRun</c> to <c>DryRun</c>. Web defaults keep it.
/// </para>
/// <para>
/// The persisted <c>StatsJson</c> does move to camelCase, since it now shares this context. That is safe in
/// both directions and deliberately so: Web defaults are case-insensitive on read, so a marker written by an
/// earlier version — <c>{"UsersCreated":5}</c> — still deserializes into the same report.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DuendeMigrationReport))]
[JsonSerializable(typeof(MigrationStatusResponse))]
internal sealed partial class MigrationJsonContext : JsonSerializerContext;

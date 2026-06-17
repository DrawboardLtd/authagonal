using System.Text.Json.Serialization;

namespace Authagonal.AzureProvider;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AzureJsonContext : JsonSerializerContext
{
}

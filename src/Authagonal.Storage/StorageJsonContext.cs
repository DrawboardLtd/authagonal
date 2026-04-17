using System.Text.Json.Serialization;

namespace Authagonal.Storage;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class StorageJsonContext : JsonSerializerContext
{
}

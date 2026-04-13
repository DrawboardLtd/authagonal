using System.Text.Json.Serialization;

namespace Authagonal.Backup;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal partial class BackupJsonContext : JsonSerializerContext
{
}

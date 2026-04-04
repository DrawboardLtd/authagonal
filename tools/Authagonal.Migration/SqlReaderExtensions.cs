using Microsoft.Data.SqlClient;

namespace Authagonal.Migration;

internal static class SqlReaderExtensions
{
    public static string? GetStringOrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

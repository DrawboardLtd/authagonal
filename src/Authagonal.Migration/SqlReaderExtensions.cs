using Microsoft.Data.SqlClient;

namespace Authagonal.Migration;

internal static class SqlReaderExtensions
{
    public static string? GetStringOrNull(this SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static async Task<bool> TableExistsAsync(this SqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = @t";
        cmd.Parameters.AddWithValue("@t", table);
        var count = (int)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }
}

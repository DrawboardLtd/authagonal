namespace Authagonal.Backup;

internal static class BackupPath
{
    /// <summary>
    /// Validates that a value is a single, safe path segment before it is combined into a filesystem
    /// path. Backup ids and file names are single segments (derived from timestamps, table names, or
    /// manifest keys); a value containing a separator, a parent reference (<c>..</c>), or a rooted
    /// path is rejected — preventing a crafted manifest/backup from reading or writing outside the
    /// backup root (directory traversal).
    /// </summary>
    public static string Safe(string segment, string paramName)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment.Contains("..", StringComparison.Ordinal)
            || segment.IndexOfAny(['/', '\\']) >= 0
            || Path.IsPathRooted(segment))
        {
            throw new ArgumentException($"Unsafe backup path segment: '{segment}'", paramName);
        }

        return segment;
    }
}

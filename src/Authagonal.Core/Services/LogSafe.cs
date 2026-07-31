using System.Text;

namespace Authagonal.Core.Services;

/// <summary>
/// Renders request-supplied values into a form that is safe to write into an application log.
/// </summary>
/// <remarks>
/// Two problems, one helper.
/// <para>
/// <b>Injection.</b> These lines are reached from anonymous endpoints and the sinks are line-oriented, so
/// an address such as <c>a@x\r\n2026-08-01 INFO Admin sign-in from 10.0.0.1</c> writes a second entry that
/// reads as genuine — the attacker forges the record an incident is later reconstructed from. Unbounded
/// length is the same problem by volume.
/// </para>
/// <para>
/// <b>PII.</b> An email address is the account's login identifier, and normal application logs travel much
/// further than the user store — to an aggregator, a support ticket, a screenshot. The domain is what these
/// lines are actually diagnostic for, so the local part is reduced to one character: enough to confirm an
/// address a user has quoted to support, not enough to enumerate the directory from log access alone.
/// </para>
/// </remarks>
public static class LogSafe
{
    /// <summary>Characters kept from any one request-supplied value.</summary>
    private const int MaxLength = 64;

    /// <summary>Request-supplied text with control characters neutralised and length capped.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";

        var builder = new StringBuilder(Math.Min(value.Length, MaxLength) + 1);
        foreach (var c in value)
        {
            if (builder.Length >= MaxLength)
            {
                builder.Append('~');
                break;
            }
            // Covers CR, LF, NUL, ANSI escapes and the rest — anything that could end the current log
            // record or re-interpret it downstream.
            builder.Append(char.IsControl(c) ? '?' : c);
        }
        return builder.ToString();
    }

    /// <summary>An email address as <c>j***@example.com</c>: domain kept, local part reduced.</summary>
    public static string Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "(none)";

        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "(malformed)";

        return Text($"{email[0]}***@{email[(at + 1)..]}");
    }
}

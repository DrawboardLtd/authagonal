using System.Text;

namespace Authagonal.Core.Services;

/// <summary>
/// Prefix slicing for index keys, measured in Unicode scalar values rather than UTF-16 code units.
/// </summary>
/// <remarks>
/// Every backend builds its name / email-local-part prefix index by taking the first N characters of a
/// normalized value, on both the write side (which prefixes to store) and the read side (which prefix
/// to look up). Taken as UTF-16 code units, that cut lands in the middle of a surrogate pair for any
/// value holding a non-BMP character — an emoji, a CJK extension-B ideograph, a mathematical
/// alphanumeric — and the lone surrogate left behind becomes a row key, where it has no UTF-8
/// encoding at all: Azure Table Storage rejects the write outright, and a SQL driver either
/// substitutes U+FFFD (so the row can never be found again, and the index entry outlives the user it
/// pointed at) or throws and surfaces as a 500.
/// <para>
/// Counting and slicing in runes keeps write and read on the same boundaries, and keeps those
/// boundaries identical across the SQL, Azure and AWS stores — the prefix-index schemes are required
/// to agree byte-for-byte, so fixing one backend's slicing and not the others would have broken
/// exactly the parity the constants are commented to preserve.
/// </para>
/// </remarks>
public static class TextPrefix
{
    /// <summary>Number of Unicode scalar values in <paramref name="value"/>.</summary>
    public static int RuneCount(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i += StrideAt(value, i)) count++;
        return count;
    }

    /// <summary>
    /// The first <paramref name="runes"/> scalar values of <paramref name="value"/>, or the whole
    /// string when it holds fewer. Never returns a string that ends mid-pair.
    /// </summary>
    public static string Take(string value, int runes)
    {
        if (runes <= 0) return "";
        var i = 0;
        for (var taken = 0; taken < runes && i < value.Length; taken++) i += StrideAt(value, i);
        return i >= value.Length ? value : value[..i];
    }

    /// <summary>
    /// The offsets at which each successive rune ends, so the prefix of the first <c>n</c> runes is
    /// <c>value[..Boundaries(value)[n - 1]]</c>. Its <c>Count</c> is the rune count.
    /// </summary>
    public static IReadOnlyList<int> Boundaries(string value)
    {
        var ends = new List<int>(value.Length);
        for (var i = 0; i < value.Length;)
        {
            i += StrideAt(value, i);
            ends.Add(i);
        }
        return ends;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is well-formed UTF-16: every high surrogate is followed by a
    /// low one, and no low surrogate stands alone. A value that fails this has no UTF-8 encoding, so
    /// it can neither be stored as a key nor match one.
    /// </summary>
    public static bool IsWellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Code units spanned by the scalar starting at <paramref name="index"/>. An unpaired surrogate
    /// counts as one unit so callers still terminate; <see cref="IsWellFormed"/> is what refuses it.
    /// </summary>
    private static int StrideAt(string value, int index)
        => Rune.TryGetRuneAt(value, index, out var rune) ? rune.Utf16SequenceLength : 1;
}

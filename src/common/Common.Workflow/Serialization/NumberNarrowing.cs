using System.Text.Json;

namespace Common.Workflow.Serialization;

/// <summary>
/// Decides what CLR type a number becomes. This is the persisted and wire-facing shape of a
/// value — what an event stores and what an API returns — so it is behaviour, not housekeeping;
/// it was previously made by four separate copies of the rule that did not all agree. Inside an
/// expression there is one number type (a double, per <c>api/docs/template-expressions.md</c>),
/// so the two rules below no longer affect what a template computes; a result narrows through
/// <see cref="FromDouble"/> on its way out.
///
/// There are deliberately <b>two</b> rules, because the two sources carry different evidence:
/// a <c>google.protobuf.Value</c> has only a double, while JSON still has the author's token.
/// They differ only for a whole number written with a decimal point or an exponent — <c>5.0</c> is
/// an <c>int</c> from proto and a <c>double</c> from JSON. Collapsing them would mean either
/// giving up narrowing on the proto side, or turning every whole-valued decimal an API returns
/// into an integer and silently changing division.
/// </summary>
public static class NumberNarrowing
{
    /// <summary>
    /// Proto carries every number as a double, so the value is the only evidence there is: a
    /// whole one becomes an int or a long. A counter incremented across a loop back-edge has to
    /// stay an integer rather than drifting to "1.0".
    /// </summary>
    public static object FromDouble(double value)
    {
        // 2^63. long.MaxValue is not representable as a double — it rounds up to exactly this —
        // so the obvious "value <= long.MaxValue" admits 2^63 itself, and the cast then
        // saturates back down to long.MaxValue, silently changing the number. Compare against
        // the power of two exclusively instead. int.MaxValue is exactly representable and needs
        // no equivalent.
        const double twoPow63 = 9223372036854775808d;

        // IsFinite covers NaN and both infinities. NaN would fail the floor test anyway
        // (NaN == anything is false); the infinities would not.
        if (double.IsFinite(value) && value == Math.Floor(value))
        {
            if (value is >= int.MinValue and <= int.MaxValue)
                return (int)value;

            if (value is >= -twoPow63 and < twoPow63)
                return (long)value;
        }

        return value;
    }

    /// <summary>
    /// JSON keeps the token, which is better evidence than the value: an author who wrote
    /// <c>5.0</c> meant a decimal, and this is the last point that can still tell.
    ///
    /// The integer attempts come first for exactness, not just speed — an integer above 2^53
    /// read through a double would come back silently rounded.
    /// </summary>
    public static object Read(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt32(out var intValue))
            return intValue;

        if (reader.TryGetInt64(out var longValue))
            return longValue;

        return reader.GetDouble();
    }
}

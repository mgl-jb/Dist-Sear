using System.Globalization;
using DistSear.Abstractions.Mapping;

namespace DistSear.Index.Segments;

/// <summary>
/// Converts loosely-typed document values (whatever came back from JSON) into the canonical forms
/// the index needs: a term string for exact matching, and a double for doc values.
/// </summary>
public static class FieldCoercion
{
    /// <summary>
    /// Position gap inserted between the values of a multi-valued text field, so that a phrase
    /// query cannot match across the boundary between two separate values.
    /// </summary>
    public const int PositionIncrementGap = 100;

    /// <summary>Flattens a scalar or a collection into a sequence of scalars.</summary>
    public static IEnumerable<object> Flatten(object? value)
    {
        switch (value)
        {
            case null:
                yield break;

            case string s:
                yield return s;
                yield break;

            case System.Collections.IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        yield return item;
                    }
                }

                yield break;

            default:
                yield return value;
                yield break;
        }
    }

    /// <summary>Canonical indexed term for a non-analyzed value. Must round-trip with query parsing.</summary>
    public static string ToTerm(object value, FieldType type) => type switch
    {
        FieldType.Keyword or FieldType.Text => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        FieldType.Boolean => ToBoolean(value) ? "true" : "false",
        FieldType.Long => ToLong(value).ToString(CultureInfo.InvariantCulture),
        FieldType.Double => ToDouble(value).ToString("R", CultureInfo.InvariantCulture),
        FieldType.Date => ToEpochMilliseconds(value).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported field type.")
    };

    /// <summary>
    /// Numeric projection used by doc values, so range filters, sorting and range facets all share
    /// a single representation regardless of the declared numeric type.
    /// </summary>
    public static double ToSortableDouble(object value, FieldType type) => type switch
    {
        FieldType.Long => ToLong(value),
        FieldType.Double => ToDouble(value),
        FieldType.Date => ToEpochMilliseconds(value),
        FieldType.Boolean => ToBoolean(value) ? 1 : 0,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Field type has no numeric projection.")
    };

    public static bool IsNumeric(FieldType type) =>
        type is FieldType.Long or FieldType.Double or FieldType.Date or FieldType.Boolean;

    public static long ToLong(object value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        double d => (long)d,
        float f => (long)f,
        decimal m => (long)m,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
    };

    public static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        decimal m => (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
    };

    public static bool ToBoolean(object value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var parsed) ? parsed : !string.IsNullOrEmpty(s) && s != "0",
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };

    public static long ToEpochMilliseconds(object value) => value switch
    {
        DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
        DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds(),
        long l => l,
        int i => i,
        double d => (long)d,
        string s when DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            => parsed.ToUnixTimeMilliseconds(),
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) => epoch,
        _ => throw new FormatException($"Cannot interpret '{value}' as a date.")
    };
}

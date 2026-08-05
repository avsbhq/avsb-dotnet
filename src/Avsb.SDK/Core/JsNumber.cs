using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Avsb.SDK.Core;

/// <summary>
/// JavaScript value coercion, ported exactly.
///
/// The reference evaluator is TypeScript, so every string the engine produces is a JavaScript
/// string and every numeric comparison is a JavaScript number comparison. Two places make that
/// visible to a .NET port:
///
/// 1. Reason strings. The conformance corpus records reasons verbatim, and they interpolate
///    numbers: "threshold 1000", not "threshold 1000.0" and not a culture-formatted "1000,5".
///    Anything that runs ToString() without an invariant culture produces a different string on
///    a machine with a comma decimal separator.
/// 2. Bucketing inputs. The bucketing key is String(resolvedAttribute) + flagKey. If the hash
///    attribute resolves to a number, "42" and "42.0" hash differently, so the same user lands
///    in a different variation depending on which SDK evaluated. That is the silent
///    cross-language assignment skew this program exists to kill.
///
/// <see cref="Stringify(double)"/> implements ECMA-262 Number::toString radix 10: the shortest
/// decimal that round-trips, printed as a plain integer when it is integral and within 1e21, and
/// in exponential form outside the documented window.
///
/// Deliberate divergence, documented because it is unobservable in the corpus: a missing
/// attribute and an explicit JSON null are both null here, so <see cref="StringOf"/> maps null to
/// "undefined" and <see cref="NumberOf"/> maps it to NaN (JavaScript would say "null" and 0 for an
/// explicit null). Both choices fail closed: an operator against a missing attribute does not
/// match.
/// </summary>
internal static class JsNumber
{
    /// <summary>Render a double exactly as JavaScript's String(number) would.</summary>
    public static string Stringify(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        // Covers both zeros: JavaScript prints negative zero as "0".
        if (value == 0.0) return "0";
        if (value < 0.0) return "-" + Stringify(-value);

        // "R" is the shortest round-trippable form on .NET Core 3.0 and later, which is the
        // same digit set ECMA-262 asks for. Only the layout differs, so reformat rather than
        // re-deriving the digits.
        string round = value.ToString("R", CultureInfo.InvariantCulture);

        string mantissa = round;
        int exponent = 0;
        int eIndex = round.IndexOfAny(new[] { 'E', 'e' });
        if (eIndex >= 0)
        {
            mantissa = round.Substring(0, eIndex);
            exponent = int.Parse(round.Substring(eIndex + 1), CultureInfo.InvariantCulture);
        }

        string integerPart = mantissa;
        string fractionPart = string.Empty;
        int dotIndex = mantissa.IndexOf('.');
        if (dotIndex >= 0)
        {
            integerPart = mantissa.Substring(0, dotIndex);
            fractionPart = mantissa.Substring(dotIndex + 1);
        }

        string digits = integerPart + fractionPart;
        // n is where the decimal point sits relative to the digit string.
        int n = integerPart.Length + exponent;

        int leading = 0;
        while (leading < digits.Length && digits[leading] == '0') leading++;
        digits = digits.Substring(leading);
        n -= leading;

        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";

        int k = digits.Length;

        if (k <= n && n <= 21)
        {
            return digits + new string('0', n - k);
        }
        if (n > 0 && n <= 21)
        {
            return digits.Substring(0, n) + "." + digits.Substring(n);
        }
        if (n > -6 && n <= 0)
        {
            return "0." + new string('0', -n) + digits;
        }

        string significand = k == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1);
        int printedExponent = n - 1;
        return significand + "e" + (printedExponent >= 0 ? "+" : "-")
            + Math.Abs(printedExponent).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Coerce any deserialised JSON value the way JavaScript's String(value) does.
    ///
    /// Arrays join on a comma with holes rendered empty, and plain objects stringify to
    /// "[object Object]". Those look strange in C#, and they are exactly what the reference
    /// engine compares against, so a port that "fixes" them stops matching.
    /// </summary>
    public static string StringOf(object? value)
    {
        switch (value)
        {
            case null:
                return "undefined";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case JsonElement element:
                return StringOfJson(element);
            case double d:
                return Stringify(d);
            case float f:
                return Stringify(f);
            case int i:
                return Stringify(i);
            case long l:
                return Stringify(l);
            case short sh:
                return Stringify(sh);
            case decimal dec:
                return Stringify((double)dec);
            case IReadOnlyList<object?> list:
                return JoinArray(list);
            case IReadOnlyDictionary<string, object?>:
                return "[object Object]";
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>
    /// Coerce any deserialised JSON value the way JavaScript's Number(value) does. Anything
    /// unparseable becomes NaN, and every comparison against NaN is false, so an unusable
    /// attribute never matches.
    /// </summary>
    public static double NumberOf(object? value)
    {
        switch (value)
        {
            case null:
                return double.NaN;
            case bool b:
                return b ? 1.0 : 0.0;
            case double d:
                return d;
            case float f:
                return f;
            case int i:
                return i;
            case long l:
                return l;
            case short sh:
                return sh;
            case decimal dec:
                return (double)dec;
            case string s:
                return ParseNumericString(s);
            case JsonElement element:
                return NumberOfJson(element);
            case IReadOnlyList<object?> list:
                if (list.Count == 0) return 0.0;
                if (list.Count == 1) return NumberOf(list[0]);
                return double.NaN;
            default:
                return double.NaN;
        }
    }

    /// <summary>True when the value is truthy under JavaScript rules (spec section 1.4).</summary>
    public static bool IsTruthy(object? value)
    {
        switch (value)
        {
            case null:
                return false;
            case bool b:
                return b;
            case string s:
                return s.Length > 0;
            case double d:
                return d != 0.0 && !double.IsNaN(d);
            case float f:
                return f != 0.0f && !float.IsNaN(f);
            case int i:
                return i != 0;
            case long l:
                return l != 0;
            case short sh:
                return sh != 0;
            case decimal dec:
                return dec != 0m;
            case JsonElement element:
                return IsTruthyJson(element);
            default:
                // Arrays and objects are truthy in JavaScript, even when empty. Python and Ruby
                // would disagree; the corpus locks the JavaScript answer.
                return true;
        }
    }

    private static string StringOfJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "undefined",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => Stringify(element.GetDouble()),
        JsonValueKind.Array => JoinJsonArray(element),
        _ => "[object Object]",
    };

    private static double NumberOfJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => 1.0,
        JsonValueKind.False => 0.0,
        JsonValueKind.String => ParseNumericString(element.GetString() ?? string.Empty),
        JsonValueKind.Null or JsonValueKind.Undefined => double.NaN,
        _ => double.NaN,
    };

    private static bool IsTruthyJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => (element.GetString() ?? string.Empty).Length > 0,
        JsonValueKind.Number => element.GetDouble() != 0.0 && !double.IsNaN(element.GetDouble()),
        // Arrays and objects, empty or not.
        _ => true,
    };

    private static string JoinArray(IReadOnlyList<object?> list)
    {
        var output = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) output.Append(',');
            // Array.prototype.join renders null and undefined as an empty string.
            if (list[i] is not null) output.Append(StringOf(list[i]));
        }
        return output.ToString();
    }

    private static string JoinJsonArray(JsonElement element)
    {
        var output = new StringBuilder();
        bool first = true;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (!first) output.Append(',');
            first = false;
            if (item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                output.Append(StringOfJson(item));
            }
        }
        return output.ToString();
    }

    private static double ParseNumericString(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            // Number("") is 0 in JavaScript, however odd that reads.
            return 0.0;
        }
        if (trimmed is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (trimmed == "-Infinity") return double.NegativeInfinity;

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(trimmed.Substring(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out long hex) ? hex : double.NaN;
        }

        // NumberStyles is restricted deliberately: AllowThousands or a currency symbol would
        // accept strings JavaScript rejects, and accepting them would make a targeting
        // comparison match here and miss everywhere else.
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowExponent;
        return double.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : double.NaN;
    }
}

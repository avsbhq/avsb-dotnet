using System.Text.Json;

namespace Avsb.SDK.Core;

/// <summary>
/// Version-number comparison for the six semver_* audience operators, ported from
/// packages/avsb-core/src/semver.ts. Every SDK reads one grammar:
///
/// - surrounding ASCII whitespace (space, tab, LF, VT, FF, CR) is trimmed;
/// - one optional leading "v" or "V";
/// - one to three dot-separated numeric parts, digits only, no leading zero unless the part is
///   exactly "0"; a missing minor or patch reads as 0;
/// - an optional "-prerelease" of dot-separated, non-empty [0-9A-Za-z-] identifiers, a
///   digits-only one without a leading zero;
/// - an optional "+build" of the same identifiers (leading zeros allowed), checked and then
///   ignored.
///
/// Order is SemVer 2.0.0 section 11. Numeric parts compare as digit strings (length first, then
/// ordinal), so a version past 64 bits orders exactly as it does in the TypeScript reference.
/// Text identifiers compare ORDINALLY: the default culture-aware comparison would put "alpha"
/// before "Beta", which ASCII order does not. Characters are checked against explicit ASCII
/// ranges, because char.IsDigit accepts digits from other scripts. Any unparsable side, or an
/// attribute that is neither a string nor a number, is no match, semver_neq included. Pinned by
/// the operator_semver_* conformance cases.
/// </summary>
internal static class SemVer
{
    /// <summary>The most numeric parts a version core carries: major, minor, patch.</summary>
    private const int MaxCoreParts = 3;

    private static readonly char[] AsciiWhitespace = { ' ', '\t', '\n', '\v', '\f', '\r' };

    /// <summary>A parsed version. Numeric parts are canonical digit strings; build metadata is dropped.</summary>
    internal sealed class Parsed
    {
        public Parsed(string major, string minor, string patch, IReadOnlyList<string> prerelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease;
        }

        public string Major { get; }
        public string Minor { get; }
        public string Patch { get; }
        public IReadOnlyList<string> Prerelease { get; }
    }

    public static bool IsOperator(string? op) => op switch
    {
        "semver_eq" or "semver_neq" or "semver_gt" or "semver_gte" or "semver_lt" or "semver_lte" => true,
        _ => false,
    };

    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    private static bool IsIdentifierChar(char ch) =>
        IsDigit(ch) || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '-';

    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0) return false;
        foreach (char ch in value)
        {
            if (!IsDigit(ch)) return false;
        }
        return true;
    }

    /// <summary>Digits only, with no leading zero unless the identifier is exactly "0".</summary>
    private static bool IsNumericIdentifier(string value) =>
        IsAllDigits(value) && (value.Length == 1 || value[0] != '0');

    /// <summary>A non-empty run of [0-9A-Za-z-].</summary>
    private static bool IsDotIdentifier(string value)
    {
        if (value.Length == 0) return false;
        foreach (char ch in value)
        {
            if (!IsIdentifierChar(ch)) return false;
        }
        return true;
    }

    private static bool IsPrereleaseIdentifier(string value) =>
        IsDotIdentifier(value) && (!IsAllDigits(value) || IsNumericIdentifier(value));

    /// <summary>Read a version string, or null when it does not follow the grammar.</summary>
    public static Parsed? Parse(string input)
    {
        string text = input.Trim(AsciiWhitespace);
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);

        int plus = text.IndexOf('+');
        if (plus != -1)
        {
            foreach (string identifier in text.Substring(plus + 1).Split('.'))
            {
                if (!IsDotIdentifier(identifier)) return null;
            }
            text = text.Substring(0, plus);
        }

        string[] prerelease = Array.Empty<string>();
        int dash = text.IndexOf('-');
        if (dash != -1)
        {
            prerelease = text.Substring(dash + 1).Split('.');
            foreach (string identifier in prerelease)
            {
                if (!IsPrereleaseIdentifier(identifier)) return null;
            }
            text = text.Substring(0, dash);
        }

        string[] parts = text.Split('.');
        if (parts.Length > MaxCoreParts) return null;
        foreach (string part in parts)
        {
            if (!IsNumericIdentifier(part)) return null;
        }

        return new Parsed(
            parts[0],
            parts.Length > 1 ? parts[1] : "0",
            parts.Length > 2 ? parts[2] : "0",
            prerelease);
    }

    /// <summary>Two canonical digit strings, ordered as the integers they spell, at any size.</summary>
    private static int CompareNumeric(string a, string b)
    {
        if (a.Length != b.Length) return Math.Sign(a.Length - b.Length);
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static int ComparePrereleaseIdentifier(string a, string b)
    {
        bool aNumeric = IsAllDigits(a);
        bool bNumeric = IsAllDigits(b);
        if (aNumeric && bNumeric) return CompareNumeric(a, b);
        if (aNumeric) return -1;
        if (bNumeric) return 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>SemVer 2.0.0 precedence: -1 when a is lower, 1 when higher, 0 when equal.</summary>
    public static int Compare(Parsed a, Parsed b)
    {
        int core = CompareNumeric(a.Major, b.Major);
        if (core == 0) core = CompareNumeric(a.Minor, b.Minor);
        if (core == 0) core = CompareNumeric(a.Patch, b.Patch);
        if (core != 0) return core;

        bool aRelease = a.Prerelease.Count == 0;
        bool bRelease = b.Prerelease.Count == 0;
        if (aRelease && bRelease) return 0;
        if (aRelease) return 1;
        if (bRelease) return -1;

        int shared = Math.Min(a.Prerelease.Count, b.Prerelease.Count);
        for (int i = 0; i < shared; i++)
        {
            int order = ComparePrereleaseIdentifier(a.Prerelease[i], b.Prerelease[i]);
            if (order != 0) return order;
        }
        return Math.Sign(a.Prerelease.Count - b.Prerelease.Count);
    }

    /// <summary>
    /// The version text of a context attribute: a string as is, a number as JavaScript's
    /// String(n) prints it, anything else (absent, boolean, list, object) has none.
    /// </summary>
    private static string? VersionText(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } element => JsNumber.Stringify(element.GetDouble()),
        double d => JsNumber.Stringify(d),
        float f => JsNumber.Stringify(f),
        int i => JsNumber.Stringify(i),
        long l => JsNumber.Stringify(l),
        short sh => JsNumber.Stringify(sh),
        decimal dec => JsNumber.Stringify((double)dec),
        _ => null,
    };

    /// <summary>
    /// Answer one semver_* condition: actual is the context attribute, expected the rule value
    /// (already its first entry when it was a list). Never throws; anything unreadable is false.
    /// </summary>
    public static bool Evaluate(string? op, object? actual, object? expected)
    {
        if (!IsOperator(op)) return false;
        string? actualText = VersionText(actual);
        if (actualText is null || expected is not string expectedText) return false;

        Parsed? actualVersion = Parse(actualText);
        Parsed? expectedVersion = Parse(expectedText);
        if (actualVersion is null || expectedVersion is null) return false;

        int order = Compare(actualVersion, expectedVersion);
        return op switch
        {
            "semver_eq" => order == 0,
            "semver_neq" => order != 0,
            "semver_gt" => order > 0,
            "semver_gte" => order >= 0,
            "semver_lt" => order < 0,
            "semver_lte" => order <= 0,
            _ => false,
        };
    }
}

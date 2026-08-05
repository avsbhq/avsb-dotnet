using System.Text.RegularExpressions;

namespace Avsb.SDK;

/// <summary>
/// SDK key shape validation at construction (spec section 1.2).
///
/// Real keys are minted as sdk_{environmentKey}_{id}, where the environment key matches
/// ^[a-z][a-z0-9_]*$ (it may itself contain underscores, which is why this validates with one
/// anchored pattern instead of splitting on '_') and the id is a cuid2 of 24 lowercase
/// alphanumerics.
///
/// A bad key used to be completely invisible: the CDN 404s, the fetch returns nothing, and every
/// flag serves its default. The four most common bad values are a pasted dashboard URL, a
/// truncated copy, a snippet key, and an API token pasted into the wrong field, and all four are
/// recognisable on sight.
///
/// This NEVER throws and never blocks construction. A key we do not recognise may still be valid,
/// because the mint format can grow; the datafile fetch is the real arbiter.
/// </summary>
public static class SdkKeyShape
{
    private static readonly Regex Pattern =
        new("^sdk_[a-z][a-z0-9_]*_[a-z0-9]{8,}$", RegexOptions.Compiled);

    /// <summary>A real-shaped example, so the message is comparable against what was pasted.</summary>
    private const string ExampleKey = "sdk_production_ttqm0eaj4vth1krcb2xn";

    /// <summary>Outcome of a shape check. Message is null when valid.</summary>
    public sealed record Result(bool Valid, string? Message);

    /// <summary>
    /// Redact all but the recognisable head of a key so a message can be pasted into a bug
    /// report. The environment name is the useful half ("you used the development key"); the id
    /// tail is not.
    /// </summary>
    public static string Describe(string? sdkKey)
    {
        if (string.IsNullOrEmpty(sdkKey)) return "(empty)";
        if (sdkKey!.Length <= 12) return sdkKey;
        return sdkKey.Substring(0, 12) + "..." + sdkKey.Substring(sdkKey.Length - 4);
    }

    /// <summary>Validate the shape, returning the message to log rather than logging it.</summary>
    public static Result Validate(string? sdkKey)
    {
        if (sdkKey is not null && Pattern.IsMatch(sdkKey))
        {
            return new Result(true, null);
        }

        return new Result(false,
            $"AvsB SDK key \"{Describe(sdkKey)}\" does not look like an SDK key: {Diagnose(sdkKey)}."
            + $" Keys look like {ExampleKey} (sdk_<environment>_<id>). Copy the key for the"
            + " environment you want from app.avsb.cloud, then Settings, then Environments. The SDK"
            + " will keep going with the value you passed, so if the datafile request 404s next,"
            + " this is why.");
    }

    private static string Diagnose(string? sdkKey)
    {
        if (sdkKey is null) return "no SDK key was passed at all";
        string trimmed = sdkKey.Trim();
        if (trimmed.Length == 0) return "no SDK key was passed at all";
        if (trimmed != sdkKey)
        {
            return "it has leading or trailing whitespace, which usually means a copy-paste picked"
                + " up a line break";
        }
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "it looks like a URL, not a key. Copy the key itself, not the address of the"
                + " page it is on";
        }
        if (trimmed.StartsWith("avsb_pat_", StringComparison.Ordinal)
            || trimmed.StartsWith("avsb_svc_", StringComparison.Ordinal))
        {
            return "it looks like an API token. API tokens talk to the management API; SDKs need"
                + " the environment SDK key";
        }
        if (trimmed.StartsWith("sdk-", StringComparison.Ordinal)
            || trimmed.StartsWith("pub_", StringComparison.Ordinal)
            || trimmed.StartsWith("srv_", StringComparison.Ordinal))
        {
            return "that key prefix has never existed in A vs B. There is one key format for SDKs";
        }
        if (!trimmed.StartsWith("sdk_", StringComparison.Ordinal))
        {
            return "it does not start with \"sdk_\"";
        }
        if (trimmed.Split('_').Length < 3)
        {
            return "it is missing the environment segment, so it may have been truncated";
        }
        return "the id after the environment name is too short or has characters a generated id"
            + " never contains, so it may have been truncated or edited";
    }
}

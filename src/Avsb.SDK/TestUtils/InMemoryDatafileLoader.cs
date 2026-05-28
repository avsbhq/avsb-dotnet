using Avsb.SDK.Core;

namespace Avsb.SDK.TestUtils;

/// <summary>
/// Loads a FlagDatafile from a raw JSON string. Useful in unit tests where
/// you want to exercise the evaluator without network access.
/// </summary>
public static class InMemoryDatafileLoader
{
    /// <summary>Parse the given JSON into a FlagDatafile.</summary>
    public static FlagDatafile Load(string json)
    {
        var df = FlagDatafile.Deserialize(json);
        if (df is null) throw new InvalidOperationException("Failed to deserialise datafile JSON.");
        return df;
    }

    /// <summary>Read a file from disk and parse it.</summary>
    public static FlagDatafile LoadFile(string path) => Load(File.ReadAllText(path));
}

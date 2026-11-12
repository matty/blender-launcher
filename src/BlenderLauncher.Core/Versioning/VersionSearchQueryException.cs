namespace BlenderLauncher.Core.Versioning;

/// <summary>
/// Upstream's <c>ValueError</c> from <c>version_matcher.py</c>: an invalid version search query or query part.
/// <see cref="Key"/> is the upstream translation key and <see cref="Arguments"/> its placeholders, so the UI can
/// show the translated text; <see cref="Exception.Message"/> is the English text.
/// </summary>
public sealed class VersionSearchQueryException : FormatException
{
    /// <summary>The text does not match the query grammar. Placeholder: <c>query</c>.</summary>
    public const string InvalidQueryKey = "launching.errors.invalid_query";

    /// <summary>A text position other than <c>^</c>, <c>*</c> or <c>-</c>. Placeholder: <c>value</c>.</summary>
    public const string InvalidPositionKey = "launching.errors.invalid_position";

    /// <summary>A build hash of <c>^</c> or <c>-</c>.</summary>
    public const string HashTemporalKey = "launching.errors.hash_temporal";

    /// <summary>A branch of <c>^</c> or <c>-</c>.</summary>
    public const string BranchTemporalKey = "launching.errors.branch_temporal";

    public VersionSearchQueryException()
        : this(InvalidQueryKey, [("query", "")])
    {
    }

    public VersionSearchQueryException(string message)
        : base(message)
    {
        Key = InvalidQueryKey;
        Arguments = new Dictionary<string, string>();
    }

    public VersionSearchQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
        Key = InvalidQueryKey;
        Arguments = new Dictionary<string, string>();
    }

    private VersionSearchQueryException(string key, (string Name, string Value)[] arguments)
        : base(EnglishText(key, arguments))
    {
        Key = key;
        Arguments = arguments.ToDictionary(a => a.Name, a => a.Value);
    }

    /// <summary>The upstream translation key, one of the <c>*Key</c> constants.</summary>
    public string Key { get; }

    /// <summary>The placeholder values for <see cref="Key"/> (<c>query</c> or <c>value</c>), if any.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; }

    internal static VersionSearchQueryException InvalidQuery(string query) => new(InvalidQueryKey, [("query", query)]);

    internal static VersionSearchQueryException InvalidPosition(string value) => new(InvalidPositionKey, [("value", value)]);

    internal static VersionSearchQueryException HashTemporal() => new(HashTemporalKey, []);

    internal static VersionSearchQueryException BranchTemporal() => new(BranchTemporalKey, []);

    // The English strings of upstream's launching.en.yml, so the message reads well in logs.
    private static string EnglishText(string key, (string Name, string Value)[] arguments)
    {
        var value = arguments.Length > 0 ? arguments[0].Value : "";
        return key switch
        {
            InvalidQueryKey => $"Invalid version search query: {value}",
            InvalidPositionKey => $"{value} must be in [\"^\", \"*\", \"-\"]",
            HashTemporalKey => "build_hash cannot be temporally matched",
            BranchTemporalKey => "branch cannot be temporally matched",
            _ => key,
        };
    }
}

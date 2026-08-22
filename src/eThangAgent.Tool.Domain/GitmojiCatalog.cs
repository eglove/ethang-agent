using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>One entry of the gitmoji catalog.</summary>
public sealed record Gitmoji(string Key, string Emoji, string Description);

/// <summary>
///     Catalog of gitmoji codes, embedded at build time from <c>gitmoji.tsv</c>.
///     Keys are colon-wrapped (<c>:name:</c>) and lookup is exact ordinal matching —
///     bare names, different casing, or unknown keys are rejected.
/// </summary>
public static class GitmojiCatalog
{
    private const string ResourceName = "eThangAgent.Tool.Domain.gitmoji.tsv";

    private static readonly Lazy<IReadOnlyList<Gitmoji>> Entries = new(Load);

    private static readonly Lazy<IReadOnlyDictionary<string, Gitmoji>> ByKey =
        new(() => Entries.Value.ToDictionary(e => e.Key, StringComparer.Ordinal));

    /// <summary>Every catalog entry in file order.</summary>
    public static IReadOnlyList<Gitmoji> All => Entries.Value;

    /// <summary>
    ///     Looks up a gitmoji by its exact colon-wrapped key (ordinal match).
    /// </summary>
    public static Result<Gitmoji> Lookup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var entries = Entries.Value;
        return ByKey.Value.TryGetValue(key, out var gitmoji)
            ? Result<Gitmoji>.Success(gitmoji)
            : Result<Gitmoji>.Failure(new Error("UnknownEmojiKey",
                $"'{key}' is not a known gitmoji key. Keys use the ':name:' format " +
                $"(colon-wrapped, exact ordinal match) — for example: " +
                $"{string.Join(", ", entries.Take(3).Select(e => e.Key))}. " +
                $"The catalog contains {entries.Count} keys."));
    }

    private static IReadOnlyList<Gitmoji> Load()
    {
        var assembly = typeof(GitmojiCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing from " +
                $"'{assembly.GetName().Name}' (packaging defect).");

        using var reader = new StreamReader(stream);
        var entries = new List<Gitmoji>();
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (lineNumber == 1)
            {
                continue; // header row
            }

            var fields = line.Split('\t');
            if (fields.Length != 3 || fields.Any(f => f.Length == 0))
            {
                throw new InvalidOperationException(
                    $"Malformed row {lineNumber} in '{ResourceName}': expected exactly " +
                    "3 non-empty TAB-separated fields (key, emoji, description) " +
                    "(packaging defect).");
            }

            entries.Add(new Gitmoji(fields[0], fields[1], fields[2]));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' contains no data rows (packaging defect).");
        }

        return entries;
    }
}

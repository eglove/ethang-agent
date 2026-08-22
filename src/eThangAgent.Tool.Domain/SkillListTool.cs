using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillListTool : ITool
{
    private const int DescriptionLimit = 60;

    private readonly ISkillCatalog _catalog;
    private readonly ILearnedSkillStore _learned;

    public ToolDefinition Definition { get; } = new(
        "skill_list",
        "List available methodology skills \u2014 built-ins shipped with the app plus skills " +
        "learned earlier \u2014 merged and sorted by name. Takes no parameters: pass an empty " +
        "object {}; any other argument is rejected. Output is one header line " +
        "`[skills: N available]`, then one line per skill: `<name> <builtin|learned> " +
        "v<version>  <description>` with the name padded to 20 characters and the description " +
        "truncated to 60 characters with an appended \u2026 when longer. If a source cannot be " +
        "read, its skills are omitted and a trailing line is appended \u2014 `[warning] built-in " +
        "skills unavailable: <reason>` or `[warning] learned skills unavailable: <reason>` \u2014 " +
        "while the listing itself still succeeds. Errors begin with `Error [Code]:`.",
        []);

    public SkillListTool(ISkillCatalog catalog, ILearnedSkillStore learned)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _learned = learned ?? throw new ArgumentNullException(nameof(learned));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = ParseArguments(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Err(parsed.Error!);

        var warnings = new List<string>();
        var skills = new List<SkillDefinition>();

        var builtIn = await _catalog.ListAsync(ct);
        if (builtIn.IsSuccess)
            skills.AddRange(builtIn.Value!);
        else
            warnings.Add($"[warning] built-in skills unavailable: {builtIn.Error!.Message}");

        var learned = await _learned.ListAsync(ct);
        if (learned.IsSuccess)
            skills.AddRange(learned.Value!);
        else
            warnings.Add($"[warning] learned skills unavailable: {learned.Error!.Message}");

        var lines = new List<string> { $"[skills: {skills.Count} available]" };
        lines.AddRange(skills
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(FormatRow));
        lines.AddRange(warnings);

        return new ToolResult(string.Join("\n", lines), false);
    }

    internal static string SourceLabel(SkillSource source) =>
        source == SkillSource.BuiltIn ? "builtin" : "learned";

    private static string FormatRow(SkillDefinition skill) =>
        $"{skill.Name,-20} {SourceLabel(skill.Source)} v{skill.Version}  {Truncate(skill.Description)}";

    private static string Truncate(string description) =>
        description.Length <= DescriptionLimit
            ? description
            : description[..DescriptionLimit] + '\u2026';

    private static Result<bool> ParseArguments(string jsonArguments)
    {
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Fail(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }
        if (json.ValueKind != JsonValueKind.Object)
            return Fail(new Error("InvalidJsonArguments", "Arguments must be a JSON object."));

        var unknown = json.EnumerateObject().Select(p => p.Name).ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. This tool takes no parameters."));

        return Result<bool>.Success(true);
    }

    private static Result<bool> Fail(Error err) => Result<bool>.Failure(err);

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}

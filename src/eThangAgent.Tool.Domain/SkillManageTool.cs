using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillManageTool : ITool
{
    private readonly ISkillCatalog _catalog;
    private readonly ILearnedSkillStore _learned;
    private readonly Func<DateTimeOffset> _clock;

    public ToolDefinition Definition { get; } = new(
        "skill_manage",
        "Create, update, or delete a learned methodology skill. timeoutSeconds and action are mandatory: " +
        "action is exactly Create, Update, " +
        "or Delete (case-sensitive). name must be lowercase letters, digits, and hyphens, starting " +
        "with a letter or digit (64 chars max). Create requires description and body; " +
        "provenanceSession optionally tags the originating session. Update changes at least one of " +
        "description/body, bumps the version by one, and preserves creation metadata. Delete " +
        "requires confirm to be exactly the boolean true \u2014 deletion permanently removes current " +
        "and history rows and refuses anything else. Built-in skills are authoritative and " +
        "immutable: creating a name that collides with a built-in fails NameCollision; updating or " +
        "deleting a built-in fails BuiltInImmutable. Output is a single annotation line: " +
        "`[skill-manage] created '<name>' v1`, `[skill-manage] updated '<name>' v<N>`, or " +
        "`[skill-manage] deleted '<name>'`. Errors begin with `Error [Code]:`.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("action", ToolParameterType.String,
                "Exactly Create, Update, or Delete (case-sensitive)."),
            new ToolParameter("name", ToolParameterType.String,
                "Skill name: lowercase letters, digits, and hyphens; starts with a letter or digit; 64 chars max."),
            new ToolParameter("description", ToolParameterType.String,
                "Create: required non-empty summary. Update: optional new summary."),
            new ToolParameter("body", ToolParameterType.String,
                "Create: required non-empty skill body. Update: optional new body."),
            new ToolParameter("provenanceSession", ToolParameterType.String,
                "Create only: originating session id recorded for provenance."),
            new ToolParameter("confirm", ToolParameterType.Boolean,
                "Delete only: must be exactly true; deletion is permanent."),
        ],
        ["timeoutSeconds", "action"]);

    public SkillManageTool(ISkillCatalog catalog, ILearnedSkillStore learned, Func<DateTimeOffset> clock)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _learned = learned ?? throw new ArgumentNullException(nameof(learned));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = SkillManageInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        var v = parsed.Value!;
        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token => v.Action switch
        {
            SkillManageAction.Create => CreateAsync(v, token),
            SkillManageAction.Update => UpdateAsync(v, token),
            _ => DeleteAsync(v, token),
        }, ct);
    }

    private async Task<ToolResult> CreateAsync(SkillManageInput input, CancellationToken ct)
    {
        // Built-ins are authoritative: the catalog check comes first so the
        // store is never touched on a colliding name.
        var builtIn = await _catalog.GetAsync(input.Name, ct);
        if (builtIn.IsSuccess)
            return Err(new Error("NameCollision",
                $"'{input.Name}' is a built-in skill and built-ins are authoritative: " +
                "learned skills may never shadow them. Choose a different name."));

        var existing = await _learned.GetAsync(input.Name, ct);
        if (!existing.IsSuccess)
            return Err(existing.Error!);
        if (existing.Value is not null)
            return Err(new Error("SkillExists",
                $"A learned skill named '{input.Name}' already exists. Use action Update to change it."));

        var now = _clock();
        var created = await _learned.CreateAsync(
            new SkillDefinition(input.Name, input.Description!, input.Body!, 1,
                SkillSource.Learned, input.ProvenanceSession, now, now), ct);
        if (!created.IsSuccess)
            return Err(created.Error!);

        var skill = created.Value!;
        return new ToolResult($"[skill-manage] created '{skill.Name}' v{skill.Version}", false);
    }

    private async Task<ToolResult> UpdateAsync(SkillManageInput input, CancellationToken ct)
    {
        // Built-ins are immutable: never consult the store for one.
        var builtIn = await _catalog.GetAsync(input.Name, ct);
        if (builtIn.IsSuccess)
            return Err(BuiltInImmutableError(input.Name));

        var current = await _learned.GetAsync(input.Name, ct);
        if (!current.IsSuccess)
            return Err(current.Error!);
        if (current.Value is null)
            return Err(new Error("SkillNotFound",
                $"No learned skill named '{input.Name}' to update. Use action Create first."));

        // `with` preserves CreatedAt and ProvenanceSessionId by construction.
        var cur = current.Value;
        var updated = cur with
        {
            Description = input.Description ?? cur.Description,
            Body = input.Body ?? cur.Body,
            Version = cur.Version + 1,
            UpdatedAt = _clock(),
        };

        var result = await _learned.UpdateAsync(updated, ct);
        if (!result.IsSuccess)
            return Err(result.Error!);

        var skill = result.Value!;
        return new ToolResult($"[skill-manage] updated '{skill.Name}' v{skill.Version}", false);
    }

    private async Task<ToolResult> DeleteAsync(SkillManageInput input, CancellationToken ct)
    {
        // Built-ins are immutable: never consult the store for one.
        var builtIn = await _catalog.GetAsync(input.Name, ct);
        if (builtIn.IsSuccess)
            return Err(BuiltInImmutableError(input.Name));

        var existing = await _learned.GetAsync(input.Name, ct);
        if (!existing.IsSuccess)
            return Err(existing.Error!);
        if (existing.Value is null)
            return Err(NotFoundDeleteError(input.Name));

        var deleted = await _learned.DeleteAsync(input.Name, ct);
        if (!deleted.IsSuccess)
            return Err(deleted.Error!);
        if (!deleted.Value)
            return Err(NotFoundDeleteError(input.Name));

        return new ToolResult($"[skill-manage] deleted '{input.Name}'", false);
    }

    private static Error BuiltInImmutableError(string name) =>
        new("BuiltInImmutable",
            $"'{name}' is a built-in skill and built-ins are immutable: " +
            "it cannot be updated or deleted.");

    private static Error NotFoundDeleteError(string name) =>
        new("SkillNotFound", $"No learned skill named '{name}' to delete.");

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}

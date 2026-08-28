using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public sealed class SkillManageTool(ISkillCatalog catalog, ILearnedSkillStore learned, Func<DateTimeOffset> clock) : ITool
{
  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
  private readonly ILearnedSkillStore _learned = learned ?? throw new ArgumentNullException(nameof(learned));
  private readonly Func<DateTimeOffset> _clock = clock ?? throw new ArgumentNullException(nameof(clock));

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
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("action", ToolParameterType.Text,
                "Exactly Create, Update, or Delete (case-sensitive)."),
            new ToolParameter("name", ToolParameterType.Text,
                "Skill name: lowercase letters, digits, and hyphens; starts with a letter or digit; 64 chars max."),
            new ToolParameter("description", ToolParameterType.Text,
                "Create: required non-empty summary. Update: optional new summary."),
            new ToolParameter("body", ToolParameterType.Text,
                "Create: required non-empty skill body. Update: optional new body."),
            new ToolParameter("provenanceSession", ToolParameterType.Text,
                "Create only: originating session id recorded for provenance."),
            new ToolParameter("confirm", ToolParameterType.Flag,
                "Delete only: must be exactly true; deletion is permanent."),
      ],
      ["timeoutSeconds", "action"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<SkillManageInput> parsed = SkillManageInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error!));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!budget.IsSuccess)
    {
      return Task.FromResult(Err(budget.Error!));
    }

    SkillManageInput v = parsed.Value!;
    return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token => v.Action switch
    {
      SkillManageAction.Create => CreateAsync(v, token),
      SkillManageAction.Update => UpdateAsync(v, token),
      SkillManageAction.Delete => DeleteAsync(v, token),
      // Unnamed enum values cannot occur.
      _ => throw new InvalidOperationException("Unknown skill_manage action."),
    }, ct);
  }

  private async Task<ToolResult> CreateAsync(SkillManageInput input, CancellationToken ct)
  {
    // Built-ins are authoritative: the catalog check comes first so the
    // store is never touched on a colliding name.
    Result<SkillDefinition> builtIn = await _catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (builtIn.IsSuccess)
    {
      return Err(new DomainError("NameCollision",
          $"'{input.Name}' is a built-in skill and built-ins are authoritative: " +
          "learned skills may never shadow them. Choose a different name."));
    }

    Result<SkillDefinition?> existing = await _learned.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (!existing.IsSuccess)
    {
      return Err(existing.Error!);
    }

    if (existing.ValueOrNull is not null)
    {
      return Err(new DomainError("SkillExists",
          $"A learned skill named '{input.Name}' already exists. Use action Update to change it."));
    }

    DateTimeOffset now = _clock();
    Result<SkillDefinition> created = await _learned.CreateAsync(
        new SkillDefinition(input.Name, input.Description!, input.Body!, 1,
            SkillSource.Learned, input.ProvenanceSession, now, now), ct).ConfigureAwait(false);
    if (!created.IsSuccess)
    {
      return Err(created.Error!);
    }

    SkillDefinition skill = created.Value!;
    return new ToolResult($"[skill-manage] created '{skill.Name}' v{skill.Version}", false);
  }

  private async Task<ToolResult> UpdateAsync(SkillManageInput input, CancellationToken ct)
  {
    // Built-ins are immutable: never consult the store for one.
    Result<SkillDefinition> builtIn = await _catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (builtIn.IsSuccess)
    {
      return Err(BuiltInImmutableError(input.Name));
    }

    Result<SkillDefinition?> current = await _learned.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (!current.IsSuccess)
    {
      return Err(current.Error);
    }

    if (current.ValueOrNull is null)
    {
      return Err(new DomainError("SkillNotFound",
          $"No learned skill named '{input.Name}' to update. Use action Create first."));
    }

    // `with` preserves CreatedAt and ProvenanceSessionId by construction.
    SkillDefinition cur = current.Value;
    SkillDefinition updated = cur with
    {
      Description = input.Description ?? cur.Description,
      Body = input.Body ?? cur.Body,
      Version = cur.Version + 1,
      UpdatedAt = _clock(),
    };

    Result<SkillDefinition> result = await _learned.UpdateAsync(updated, ct).ConfigureAwait(false);
    if (!result.IsSuccess)
    {
      return Err(result.Error);
    }

    SkillDefinition skill = result.Value;
    return new ToolResult($"[skill-manage] updated '{skill.Name}' v{skill.Version}", false);
  }

  private async Task<ToolResult> DeleteAsync(SkillManageInput input, CancellationToken ct)
  {
    // Built-ins are immutable: never consult the store for one.
    Result<SkillDefinition> builtIn = await _catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (builtIn.IsSuccess)
    {
      return Err(BuiltInImmutableError(input.Name));
    }

    Result<SkillDefinition?> existing = await _learned.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (!existing.IsSuccess)
    {
      return Err(existing.Error);
    }

    if (existing.ValueOrNull is null)
    {
      return Err(NotFoundDeleteError(input.Name));
    }

    Result<bool> deleted = await _learned.DeleteAsync(input.Name, ct).ConfigureAwait(false);
    if (!deleted.IsSuccess)
    {
      return Err(deleted.Error);
    }

    bool notFound = !deleted.Value;
    return notFound
        ? Err(NotFoundDeleteError(input.Name))
        : new ToolResult($"[skill-manage] deleted '{input.Name}'", false);
  }

  private static DomainError BuiltInImmutableError(string name) =>
      new("BuiltInImmutable",
          $"'{name}' is a built-in skill and built-ins are immutable: " +
          "it cannot be updated or deleted.");

  private static DomainError NotFoundDeleteError(string name) =>
      new("SkillNotFound", $"No learned skill named '{name}' to delete.");

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}

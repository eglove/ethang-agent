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
      "Create, update, delete, or lint a skill. timeoutSeconds and action are mandatory: " +
      "action is exactly Create, Update, " +
      "Delete, or Lint (case-sensitive). name must be lowercase letters, digits, and hyphens, starting " +
      "with a letter or digit (64 chars max). Create requires description and body; " +
      "provenanceSession optionally tags the originating session. Update changes at least one of " +
      "description/body, bumps the version by one, and preserves creation metadata. Delete " +
      "requires confirm to be exactly the boolean true \u2014 deletion permanently removes current " +
      "and history rows and refuses anything else. Built-in and file skills are authoritative: " +
      "creating a name held by a built-in or file skill fails NameCollision — file skills may " +
      "never be shadowed by learned skills; updating or deleting a name held only as a file " +
      "skill fails SkillNotFound (nothing learned exists to change); updating a truly unknown " +
      "name fails SkillNotFound with 'No learned skill named <name> to update. Use action " +
      "Create first.'; updating or deleting a " +
      "built-in fails BuiltInImmutable. Lint works over ANY skill (built-in, file, or learned) " +
      "and checks its description against the documented trigger rules (third person; what it " +
      "does AND when to use it; front-loaded key terms). Output: `[skill-lint] '<name>' clean`, " +
      "or a `[skill-lint]` header plus one `[lint] <rule>: <message>` line per finding; " +
      "other results are the `[skill-manage]` annotation lines. Errors begin with `Error [Code]:`.",
      [
            new ToolParameter("action", ToolParameterType.Text,
                "Exactly Create, Update, Delete, or Lint (case-sensitive). Lint is read-only."),
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
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!budget.IsSuccess)
    {
      return Task.FromResult(Err(budget.Error));
    }

    SkillManageInput v = parsed.Value;
    return ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => v.Action switch
    {
      SkillManageAction.Create => CreateAsync(v, token),
      SkillManageAction.Update => UpdateAsync(v, token),
      SkillManageAction.Delete => DeleteAsync(v, token),
      SkillManageAction.Lint => LintAsync(v, token),
      // Unnamed enum values cannot occur.
      _ => throw new InvalidOperationException("Unknown skill_manage action."),
    }, ct);
  }

  /// <summary>Lint (vault move 6b): runs the deterministic description linter over
  ///     any skill the catalog or learned store holds (built-in, file, or learned)
  ///     and returns the findings. Read-only: no store writes, no version bump.
  ///     Output: one `[skill-lint]` header line, then one `[lint] &lt;rule&gt;: &lt;message&gt;`
  ///     line per finding - or `[skill-lint] 'name' clean` when it passes.</summary>
  private async Task<ToolResult> LintAsync(SkillManageInput input, CancellationToken ct)
  {
    Result<string> description = await ResolveDescriptionAsync(input.Name, ct).ConfigureAwait(false);
    return description.IsSuccess
        ? LintResult(input.Name, description.Value)
        : Err(description.Error);
  }

  /// <summary>Resolves the description to lint: catalog first (built-in or file),
  ///     then the learned store. A name held nowhere fails with the learned
  ///     lookup's SkillNotFound.</summary>
  private async Task<Result<string>> ResolveDescriptionAsync(string name, CancellationToken ct)
  {
    Result<SkillDefinition> fromCatalog = await _catalog.GetAsync(name, ct).ConfigureAwait(false);
    if (fromCatalog.IsSuccess)
    {
      return Result.Success(fromCatalog.Value.Description);
    }

    Result<SkillDefinition?> fromLearned = await _learned.GetAsync(name, ct).ConfigureAwait(false);
    return fromLearned.IsSuccess && fromLearned.Value is { } learnedSkill
        ? Result.Success(learnedSkill.Description)
        : Result.Failure<string>(ResolveFailure(fromCatalog, fromLearned)
            ?? new DomainError("SkillNotFound", $"No skill named '{name}'."));
  }

  /// <summary>The learned lookup failed, or it succeeded but holds no row: pick the
  ///     error that says which name-resolution path broke.</summary>
  private static DomainError ResolveFailure(
      Result<SkillDefinition> fromCatalog, Result<SkillDefinition?> fromLearned) =>
      fromLearned.IsSuccess
          ? fromCatalog.Error ?? new DomainError("SkillNotFound", "no catalog detail")
          : fromLearned.Error ?? new DomainError("SkillNotFound", "no learned detail");

  private static ToolResult LintResult(string name, string description)
  {
    IReadOnlyList<SkillDescriptionFinding> findings = SkillDescriptionLinter.Lint(description);
    if (findings.Count == 0)
    {
      return new ToolResult($"[skill-lint] '{name}' clean", false);
    }

    string body = string.Join("\n", findings.Select(f => $"[lint] {f.Rule}: {f.Message}"));
    return new ToolResult($"[skill-lint] '{name}' has {findings.Count} finding(s):\n{body}", false);
  }

  private async Task<ToolResult> CreateAsync(SkillManageInput input, CancellationToken ct)
  {
    // Built-ins and file skills are authoritative: the catalog check comes
    // first so the store is never touched on a colliding name.
    Result<SkillDefinition> held = await _catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (held.IsSuccess)
    {
      return Err(new DomainError("NameCollision",
          $"'{input.Name}' is already a built-in or file skill and file skills " +
          "may never be shadowed by learned skills. Choose a different name."));
    }

    Result<SkillDefinition?> existing = await _learned.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (!existing.IsSuccess)
    {
      return Err(existing.Error);
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
      return Err(created.Error);
    }

    SkillDefinition skill = created.Value;
    return new ToolResult($"[skill-manage] created '{skill.Name}' v{skill.Version}", false);
  }

  private async Task<ToolResult> UpdateAsync(SkillManageInput input, CancellationToken ct)
  {
    // A built-in is immutable; a file skill is not a learned skill at all.
    Result<SkillDefinition> held = await _catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (held.IsSuccess)
    {
      return Err(held.Value.Source == SkillSource.BuiltIn
          ? BuiltInImmutableError(input.Name)
          : FileOnlyNotFoundError(input.Name));
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
    // A built-in is immutable; a file skill is not a learned skill at all.
    Result<SkillDefinition> held = await _catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (held.IsSuccess)
    {
      return Err(held.Value.Source == SkillSource.BuiltIn
          ? BuiltInImmutableError(input.Name)
          : FileOnlyNotFoundError(input.Name));
    }

    Result<SkillDefinition?> existing = await _learned.GetAsync(input.Name, ct).ConfigureAwait(false);
    if (!existing.IsSuccess)
    {
      return Err(existing.Error);
    }

    if (existing.ValueOrNull is null)
    {
      return Err(FileOnlyNotFoundError(input.Name));
    }

    Result<bool> deleted = await _learned.DeleteAsync(input.Name, ct).ConfigureAwait(false);
    if (!deleted.IsSuccess)
    {
      return Err(deleted.Error);
    }

    bool notFound = !deleted.Value;
    return notFound
        ? Err(FileOnlyNotFoundError(input.Name))
        : new ToolResult($"[skill-manage] deleted '{input.Name}'", false);
  }

  private static DomainError BuiltInImmutableError(string name) =>
      new("BuiltInImmutable",
          $"'{name}' is a built-in skill and built-ins are immutable: " +
          "it cannot be updated or deleted.");

  /// <summary>Update/Delete reached a name the catalog holds as a file skill —
  /// or no learned skill at all: nothing learned exists to change.</summary>
  private static DomainError FileOnlyNotFoundError(string name) =>
      new("SkillNotFound", $"No learned skill named '{name}'.");

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}

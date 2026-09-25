using eThangAgent.SkillDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Install, update, or uninstall community skills into a configured
/// skill directory (plan #29 task 9). Action-switch shape (the skill_manage
/// pattern): Install requires address; Update requires name + address with
/// force implied; Uninstall requires name. Strict validation; service error
/// codes pass through verbatim.</summary>
public sealed class SkillRegistryTool(SkillRegistryService registry) : ITool
{
  private readonly SkillRegistryService _registry = registry ?? throw new ArgumentNullException(nameof(registry));

  public ToolDefinition Definition { get; } = new(
      "skill_registry",
      "Manage community skills: install from a GitHub repo or a skills.sh entry into a configured skill " +
      "directory, update an installed skill, or uninstall it. timeoutSeconds and action are mandatory: action is " +
      "exactly Install, Update, or Uninstall (case-sensitive). Install requires address (owner/repo, owner/repo/skill, " +
      "an https .git URL, or a skills.sh entry name); optional force replaces an existing file-skill folder; optional " +
      "confirm_findings acknowledges advisory scan findings after the user accepted them - BLOCK-level findings always " +
      "abort the install and are never overridable. Update requires name and address (force is implied). Uninstall " +
      "requires name; when target is omitted both configured directories are searched. target is exactly 'global' or " +
      "'workspace' wherever present. Output lines: '[skill-registry] installed <n> skill(s) into <target>: <names>' / " +
      "'[skill-registry] updated <name> in <target>' / '[skill-registry] removed <name> from <target>'; each confirmed " +
      "advisory finding renders one '[advisory] <Rule>: <file>:<line>' line. Errors begin with `Error [Code]:` - " +
      "BlockedContent, AdvisoryFindings, NameCollision, NotInstalled, NoTarget, AmbiguousUninstall, SkillNotFound, " +
      "InvalidAddress, EntryNotFound.",
      [
        new ToolParameter("action", ToolParameterType.Text, "Exactly Install, Update, or Uninstall (case-sensitive)."),
        new ToolParameter("address", ToolParameterType.Text, "Install/Update: owner/repo, owner/repo/skill, an https .git URL, or a skills.sh entry name."),
        new ToolParameter("name", ToolParameterType.Text, "Update/Uninstall: the installed skill's name."),
        new ToolParameter("target", ToolParameterType.Text, "Optional target scope: exactly 'global' or 'workspace'."),
        new ToolParameter("force", ToolParameterType.Flag, "Install only: replace an existing file-skill folder."),
        new ToolParameter("confirm_findings", ToolParameterType.Flag, "Install only: proceed past advisory scan findings (never past BLOCK)."),
      ],
      ["timeoutSeconds", "action"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return budget.IsSuccess
        ? ToolExecution.RunAsync(input.Name, budget.Value.Timeout, CoreAsync(input), ct)
        : Task.FromResult(Fail(budget.Error));
  }

  private Func<CancellationToken, Task<ToolResult>> CoreAsync(RawToolInput input) => async token =>
  {
    Result<SkillRegistryAction> action = SkillRegistryInputs.ParseAction(budgetArguments(input));
    return action.IsSuccess
        ? await DispatchAsync(action.Value, input, token).ConfigureAwait(false)
        : Fail(action.Error);
  };

  private async Task<ToolResult> DispatchAsync(
      SkillRegistryAction action, RawToolInput input, CancellationToken token) =>
      action switch
      {
        SkillRegistryAction.Install => await InstallAsync(input, token).ConfigureAwait(false),
        SkillRegistryAction.Update => await UpdateAsync(input, token).ConfigureAwait(false),
        SkillRegistryAction.Uninstall => await UninstallAsync(input, token).ConfigureAwait(false),
        // Unnamed enum values cannot occur.
        _ => throw new InvalidOperationException("Unknown skill_registry action."),
      };

  private static System.Text.Json.JsonElement budgetArguments(RawToolInput input) =>
      System.Text.Json.JsonDocument.Parse(input.JsonArguments).RootElement;

  private async Task<ToolResult> InstallAsync(RawToolInput input, CancellationToken ct)
  {
    System.Text.Json.JsonElement args = budgetArguments(input);
    Result<string?> address = SkillRegistryInputs.ParseAddress(args, required: true);
    Result<string?> target = SkillRegistryInputs.ParseTarget(args, required: false);
    if (!address.IsSuccess)
    {
      return Fail(address.Error);
    }

    if (!target.IsSuccess)
    {
      return Fail(target.Error);
    }

    Result<SkillAddress> parsed = SkillAddress.Create(address.Value ?? string.Empty);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error);
    }

    bool force = SkillRegistryInputs.ParseFlag(args, "force");
    bool confirmed = SkillRegistryInputs.ParseFlag(args, "confirm_findings");
    Result<SkillInstallReport> r = await _registry.InstallAsync(parsed.Value, target.Value, force, confirmed, ct).ConfigureAwait(false);
    if (!r.IsSuccess)
    {
      return Fail(r.Error);
    }

    string advisories = string.Join("\n", r.Value.AdvisoryFindings.Select(f => $"[advisory] {f.Rule}: {f.FilePath}:{f.LineNumber}"));
    string line = $"[skill-registry] installed {r.Value.InstalledNames.Count} skill(s) into {TargetLabel(input)}: {string.Join(", ", r.Value.InstalledNames)}";
    return Ok(advisories.Length == 0 ? line : line + "\n" + advisories);
  }

  private async Task<ToolResult> UpdateAsync(RawToolInput input, CancellationToken ct)
  {
    System.Text.Json.JsonElement args = budgetArguments(input);
    Result<string?> name = SkillRegistryInputs.ParseName(args, required: true);
    Result<string?> address = SkillRegistryInputs.ParseAddress(args, required: true);
    Result<string?> target = SkillRegistryInputs.ParseTarget(args, required: false);
    if (!name.IsSuccess)
    {
      return Fail(name.Error);
    }

    if (!address.IsSuccess)
    {
      return Fail(address.Error);
    }

    if (!target.IsSuccess)
    {
      return Fail(target.Error);
    }

    Result<SkillAddress> parsed = SkillAddress.Create(address.Value ?? string.Empty);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error);
    }

    Result<SkillInstallReport> r = await _registry.UpdateAsync(name.Value ?? string.Empty, parsed.Value, target.Value, ct).ConfigureAwait(false);
    if (!r.IsSuccess)
    {
      return Fail(r.Error);
    }

    string advisories = string.Join("\n", r.Value.AdvisoryFindings.Select(f => $"[advisory] {f.Rule}: {f.FilePath}:{f.LineNumber}"));
    string line = $"[skill-registry] updated {name.Value ?? string.Empty} in {TargetLabel(input)}";
    return Ok(advisories.Length == 0 ? line : line + "\n" + advisories);
  }

  private async Task<ToolResult> UninstallAsync(RawToolInput input, CancellationToken ct)
  {
    System.Text.Json.JsonElement args = budgetArguments(input);
    Result<string?> name = SkillRegistryInputs.ParseName(args, required: true);
    Result<string?> target = SkillRegistryInputs.ParseTarget(args, required: false);
    if (!name.IsSuccess)
    {
      return Fail(name.Error);
    }

    if (!target.IsSuccess)
    {
      return Fail(target.Error);
    }

    Result<string> r = await _registry.UninstallAsync(name.Value ?? string.Empty, target.Value, ct).ConfigureAwait(false);
    return r.IsSuccess
        ? Ok($"[skill-registry] removed {name.Value ?? string.Empty} from {TargetLabel(input)}")
        : Fail(r.Error);
  }

  private static string TargetLabel(RawToolInput input)
  {
    Result<string?> target = SkillRegistryInputs.ParseTarget(budgetArguments(input), required: false);
    return target is { IsSuccess: true, Value: not null } ? target.Value : "the resolved target";
  }

  private static ToolResult Ok(string content) => new(content, IsError: false);

  private static ToolResult Fail(DomainError error) => new($"Error [{error.Code}]: {error.Message}", IsError: true);
}
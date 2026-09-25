// Named decisions: skill-directory files are small and local; sync file IO
// inside the async service keeps the pipeline simple without meaningful
// blocking (the DirectorySkillSource precedent). Staging cleanup is
// best-effort by spec (a stale temp dir is never worth failing a tool).
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

/// <summary>The outcome of one install or update (plan #29 task 6).</summary>
public sealed record SkillInstallReport(
    IReadOnlyList<string> InstalledNames,
    string TargetDirectory,
    IReadOnlyList<ScanFinding> AdvisoryFindings,
    bool AdvisoryConfirmed);

/// <summary>Orchestrates the skill registry pipeline (spec #27): resolve the
/// address (skills.sh entries through the JSON API), shallow-clone into OS
/// temp staging, normalize both layouts, scan every staged file (BLOCK
/// aborts unconditionally; advisory findings gate the install behind
/// confirmFindings), refuse name collisions against the catalog (built-ins
/// authoritative; learned rows are not filesystem content), then promote
/// into the resolved target directory. The running hot-reload watcher
/// announces the change with no wiring here.</summary>
public sealed class SkillRegistryService(
    ISkillRegistryAccess registry,
    ISkillsShAccess skillsSh,
    ISkillCatalog catalog,
    Func<string?> defaultTargetResolver,
    Func<IReadOnlyList<string>> configuredDirectoriesResolver)
{
  private const long MaxScanFileBytes = 1024 * 1024;

  public async Task<Result<SkillInstallReport>> InstallAsync(
      SkillAddress address, string? explicitTarget, bool force, bool confirmFindings, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(address);
    Result<SkillAddress> resolved = await ResolveAsync(address, ct).ConfigureAwait(false);
    return resolved.IsSuccess
        ? await CloneAndInstallAsync(resolved.Value, explicitTarget, force, confirmFindings, ct).ConfigureAwait(false)
        : Result.Failure<SkillInstallReport>(resolved.Error);
  }

  /// <summary>Update = the install pipeline with force implied and advisory
  /// findings pre-confirmed (the user approved this skill once — that is
  /// what update means). The named skill must already exist in the target;
  /// the address is given fresh every time (no stored provenance, spec
  /// decision 8).</summary>
  public Task<Result<SkillInstallReport>> UpdateAsync(
      string skillName, SkillAddress address, string? explicitTarget, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(address);
    string targetDir = ResolveTargetDirOrNull(explicitTarget);
    return targetDir.Length == 0
        ? Task.FromResult(ToReport(NoTarget()))
        : UpdateCoreAsync(skillName, address, explicitTarget, targetDir, ct);
  }

  private Task<Result<SkillInstallReport>> UpdateCoreAsync(
      string skillName, SkillAddress address, string? explicitTarget, string targetDir, CancellationToken ct) =>
      Directory.Exists(Path.Combine(targetDir, skillName))
          ? InstallAsync(address, explicitTarget, force: true, confirmFindings: true, ct)
          : Task.FromResult(ToReport(Result.Failure<string>(new DomainError("NotInstalled",
              $"Skill '{skillName}' is not installed in the resolved target; use install first."))));

  private static Result<SkillInstallReport> ToReport(Result<string> failure)
  {
    DomainError error = failure.Error ?? new DomainError("Unknown", "target resolution failed");
    return Result.Failure<SkillInstallReport>(error);
  }

  /// <summary>Removes one installed skill folder. With an explicit target the
  /// path is exact; without one both configured directories are searched:
  /// exactly one hit removes it, two hits are ambiguous, zero is not found.</summary>
  public async Task<Result<string>> UninstallAsync(string skillName, string? explicitTarget, CancellationToken ct = default)
  {
    if (explicitTarget is not null)
    {
      string dir = ResolveTargetDirOrNull(explicitTarget);
      return dir.Length == 0
          ? NoTarget()
          : await RemoveExactAsync(Path.Combine(dir, skillName), ct).ConfigureAwait(false);
    }

    IReadOnlyList<string> dirs = configuredDirectoriesResolver();
    List<string> hits = [.. dirs.Select(d => Path.Combine(d, skillName)).Where(Directory.Exists)];
    return hits.Count switch
    {
      0 => Result.Failure<string>(new DomainError("SkillNotFound",
          $"No installed skill named '{skillName}' was found in any configured directory.")),
      1 => await RemoveExactAsync(hits[0], ct).ConfigureAwait(false),
      _ => Result.Failure<string>(new DomainError("AmbiguousUninstall",
          $"'{skillName}' exists in multiple configured directories: {string.Join("; ", hits)}. Name the target directory explicitly.")),
    };
  }

  public async Task<Result<IReadOnlyList<SkillsShEntry>>> SearchAsync(string query, CancellationToken ct = default)
  {
    Result<string> json = await skillsSh.FetchSearchAsync(query, ct).ConfigureAwait(false);
    return json.IsSuccess
        ? SkillsShSearchParser.ParseSearch(json.Value)
        : Result.Failure<IReadOnlyList<SkillsShEntry>>(json.Error);
  }

  private async Task<Result<SkillInstallReport>> CloneAndInstallAsync(
      SkillAddress address, string? explicitTarget, bool force, bool confirmFindings, CancellationToken ct)
  {
    Result<string> target = ResolveTarget(explicitTarget);
    if (!target.IsSuccess)
    {
      return Result.Failure<SkillInstallReport>(target.Error);
    }

    string staging = Path.Combine(Path.GetTempPath(), "ethang-skill-install-" + Guid.NewGuid().ToString("N"));
    Result<string> clone = await registry.CloneAsync(new CloneRequest(address.ToCloneUrl(), staging), ct).ConfigureAwait(false);
    if (!clone.IsSuccess)
    {
      return Result.Failure<SkillInstallReport>(clone.Error);
    }

    try
    {
      Result<IReadOnlyList<StagedSkill>> layout = RegistryLayoutNormalizer.Normalize(clone.Value, address.SubSkill, address.Repo);
      if (!layout.IsSuccess)
      {
        return Result.Failure<SkillInstallReport>(layout.Error);
      }

      Result<IReadOnlyList<ScanFinding>> scan = ScanStaged(layout.Value);
      if (!scan.IsSuccess)
      {
        return Result.Failure<SkillInstallReport>(scan.Error);
      }

      IReadOnlyList<ScanFinding> advisory = scan.Value;
      if (advisory.Count > 0 && !confirmFindings)
      {
        return Result.Failure<SkillInstallReport>(new DomainError("AdvisoryFindings",
            "Advisory findings require user confirmation before install:\n" + RenderFindings(advisory) +
            "\nRetry with confirm_findings=true after the user has seen and accepted them."));
      }

      Result<SkillInstallReport> gate = await CheckCollisionsAsync(layout.Value, target.Value, force, ct).ConfigureAwait(false);
      if (!gate.IsSuccess)
      {
        return gate;
      }

      List<string> installed = [.. layout.Value.Select(s => s.Name)];
      foreach (PromoteRequest request in layout.Value.Select(s => new PromoteRequest(s.SourcePath, target.Value, force)))
      {
        Result<string> promoted = await registry.PromoteAsync(request, ct).ConfigureAwait(false);
        if (!promoted.IsSuccess)
        {
          return Result.Failure<SkillInstallReport>(promoted.Error);
        }
      }

      return Result.Success(new SkillInstallReport(installed, target.Value, advisory, AdvisoryConfirmed: advisory.Count > 0));
    }
    finally
    {
      TryDeleteStaging(staging);
    }
  }

  private async Task<Result<SkillInstallReport>> CheckCollisionsAsync(
      IReadOnlyList<StagedSkill> staged, string targetDirectory, bool force, CancellationToken ct)
  {
    Result<IReadOnlyList<SkillDefinition>> catalogList = await catalog.ListAsync(ct).ConfigureAwait(false);
    if (!catalogList.IsSuccess)
    {
      return Result.Failure<SkillInstallReport>(catalogList.Error);
    }

    foreach ((StagedSkill stagedSkill, SkillDefinition? held) in
        staged.Select(skill => (stagedSkill: skill, held: catalogList.Value.FirstOrDefault(s => s.Name == skill.Name))))
    {
      if (held is null)
      {
        continue;
      }

      if (held.Source != SkillSource.File)
      {
        return Result.Failure<SkillInstallReport>(new DomainError("NameCollision",
            $"'{stagedSkill.Name}' collides with a {held.Source.ToString().ToUpperInvariant()} skill — built-ins are authoritative and learned rows are not filesystem content."));
      }
    }

    return staged.FirstOrDefault(skill => Directory.Exists(Path.Combine(targetDirectory, skill.Name))) is StagedSkill existing && !force
        ? Result.Failure<SkillInstallReport>(new DomainError("NameCollision",
            $"'{existing.Name}' already exists in the target directory; retry with force to replace it."))
        : Result.Success(new SkillInstallReport([], targetDirectory, [], AdvisoryConfirmed: false));

  }

  private static Result<IReadOnlyList<ScanFinding>> ScanStaged(IReadOnlyList<StagedSkill> staged)
  {
    List<ScanFinding> advisory = [];
    foreach (string file in staged.SelectMany(s => Directory.EnumerateFiles(s.SourcePath, "*", SearchOption.AllDirectories)))
    {
      string relative = Path.GetRelativePath(FindOwner(staged, file), file);
      FileInfo info = new(file);
      if (info.Length > MaxScanFileBytes)
      {
        advisory.Add(new ScanFinding(FindingSeverity.Advisory, "FileTooLarge", relative, 0));
        continue;
      }

      byte[] bytes = File.ReadAllBytes(file);
      if (SkillContentScanner.LooksBinary(Path.GetExtension(file), bytes.AsSpan(0, Math.Min(bytes.Length, 8 * 1024))))
      {
        advisory.Add(new ScanFinding(FindingSeverity.Advisory, "BinarySkipped", relative, 0));
        continue;
      }

      string text = File.ReadAllText(file);
      ScanResult result = SkillContentScanner.ScanFile(relative, text);
      if (result.HasBlock)
      {
        ScanFinding block = result.Findings.First(f => f.Severity == FindingSeverity.Block);
        return Result.Failure<IReadOnlyList<ScanFinding>>(new DomainError("BlockedContent",
            "Install aborted: BLOCK-level content finding. This content must never enter the model's context.\n" +
            RenderFindings([block]) + $"\n(File: {relative})"));
      }

      advisory.AddRange(result.Findings);
    }

    return Result.Success<IReadOnlyList<ScanFinding>>(advisory);
  }

  private static string FindOwner(IReadOnlyList<StagedSkill> staged, string file) =>
      staged.First(s => file.StartsWith(s.SourcePath, StringComparison.Ordinal)).SourcePath;

  private async Task<Result<SkillAddress>> ResolveAsync(SkillAddress address, CancellationToken ct)
  {
    if (address.Kind != SkillAddressKind.SkillsShEntry)
    {
      return Result.Success(address);
    }

    Result<IReadOnlyList<SkillsShEntry>> entries = await SearchAsync(address.Raw, ct).ConfigureAwait(false);
    if (!entries.IsSuccess)
    {
      return Result.Failure<SkillAddress>(entries.Error);
    }

    SkillsShEntry? best = entries.Value.Count > 0 ? entries.Value[0] : null;
    return best is null
        ? Result.Failure<SkillAddress>(new DomainError("EntryNotFound", $"No skills.sh entry named '{address.Raw}' was found."))
        : SkillAddress.Create(best.Address);
  }

  private Result<string> ResolveTarget(string? explicitTarget)
  {
    if (explicitTarget is not null)
    {
      return ResolveDirectory(explicitTarget);
    }

    string? preferred = defaultTargetResolver();
    return preferred is not null
        ? ResolveDirectory(preferred)
        : NoTarget();
  }

  private string ResolveTargetDirOrNull(string? explicitTarget)
  {
    Result<string> r = ResolveTarget(explicitTarget);
    return r.IsSuccess ? r.Value : string.Empty;
  }

  private static Result<string> NoTarget() =>
      Result.Failure<string>(new DomainError("NoTarget",
          "No install target: pass target (global|workspace) or set the skill_registry:default_target preference. Both are unset."));

  private Result<string> ResolveDirectory(string scope)
  {
    IReadOnlyList<string> dirs = configuredDirectoriesResolver();
    return dirs.Count == 0
        ? NoTarget()
        : DirectoryForScope(dirs, scope);
  }

  private static Result<string> DirectoryForScope(IReadOnlyList<string> dirs, string scope) =>
      scope == "global" ? Result.Success(dirs[0]) : Result.Success(dirs[^1]);

  private async Task<Result<string>> RemoveExactAsync(string path, CancellationToken ct)
  {
    return Directory.Exists(path)
        ? await registry.RemoveAsync(path, ct).ConfigureAwait(false)
        : Result.Failure<string>(new DomainError("SkillNotFound", $"No installed skill folder at '{path}'."));
  }

  private static string RenderFindings(IReadOnlyList<ScanFinding> findings) =>
      string.Join("\n", findings.Select(f => $"[scan] {f.Severity} {f.Rule}: {f.FilePath}:{f.LineNumber}"));

  private static void TryDeleteStaging(string staging)
  {
    if (!Directory.Exists(staging))
    {
      return;
    }

    try
    {
      Directory.Delete(staging, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup (named decision)
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup (named decision)
    }
  }
}

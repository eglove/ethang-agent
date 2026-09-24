using System.Security;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

// Named decision (CA1849): skill-directory files are small and local; sync APIs
// inside the async tool wrapper keep the code simple without meaningful blocking.
#pragma warning disable CA1849 // Call async methods when in an async method
namespace eThangAgent.FileSystem.ACL;

/// <summary>File-system adapter for <see cref="ISkillDirectorySource" />: reads
///     agentskills.io skill directories from disk. Each immediate subdirectory
///     containing a SKILL.md file (matched case-sensitively, the agentskills.io
///     convention) is one skill, as is a SKILL.md directly at the directory root
///     (a single-skill directory). Hidden folders (leading '.') are skipped
///     silently — they are working data, not skills. Expected I/O failures
///     degrade: one unreadable or unparseable SKILL.md becomes a diagnostic;
///     a scan-level I/O failure becomes a named <see cref="DomainError" />. A
///     directory that exists but holds no skills loads successfully with no
///     skills and no diagnostics — empty is not an error.</summary>
public sealed class DirectorySkillSource : ISkillDirectorySource
{
  private const string SkillFileName = "SKILL.md";

  /// <summary>Loads every skill under <paramref name="directoryPath" />. Subdirectories
  ///     are scanned alphabetically by folder name; the root SKILL.md, when present,
  ///     is loaded last. Diagnostics accumulate in scan order. One clock read per
  ///     load: every skill in the load shares the same CreatedAt/UpdatedAt.</summary>
  public Task<Result<SkillDirectoryLoad>> ListAsync(string directoryPath, CancellationToken ct = default)
  {
    if (!Directory.Exists(directoryPath))
    {
      return Task.FromResult(Result.Failure<SkillDirectoryLoad>(
          new DomainError("DirectoryNotFound", $"Skill directory does not exist: {directoryPath}")));
    }

    DateTimeOffset timestamp = DateTimeOffset.UtcNow;
    try
    {
      return Task.FromResult(Scan(directoryPath, timestamp));
    }
    catch (IOException ex)
    {
      return Task.FromResult(ScanFailed(directoryPath, ex));
    }
    catch (UnauthorizedAccessException ex)
    {
      return Task.FromResult(ScanFailed(directoryPath, ex));
    }
    catch (SecurityException ex)
    {
      return Task.FromResult(ScanFailed(directoryPath, ex));
    }
  }

  private static Result<SkillDirectoryLoad> Scan(string directoryPath, DateTimeOffset timestamp)
  {
    List<SkillDefinition> skills = [];
    List<string> diagnostics = [];

    foreach (string folder in EnumerateVisibleSubdirectories(directoryPath))
    {
      if (!HasCaseSensitiveFile(folder, SkillFileName))
      {
        diagnostics.Add($"no SKILL.md in {Path.GetFileName(folder)}, skipped");
        continue;
      }

      SkillFileOutcome outcome = LoadSkillFile(folder, Path.Combine(folder, SkillFileName), timestamp);
      if (outcome.Skill is not null)
      {
        skills.Add(outcome.Skill);
      }

      diagnostics.AddRange(outcome.Diagnostics);
    }

    if (HasCaseSensitiveFile(directoryPath, SkillFileName))
    {
      SkillFileOutcome outcome =
          LoadSkillFile(directoryPath, Path.Combine(directoryPath, SkillFileName), timestamp);
      if (outcome.Skill is not null)
      {
        skills.Add(outcome.Skill);
      }

      diagnostics.AddRange(outcome.Diagnostics);
    }

    return Result.Success(new SkillDirectoryLoad(skills, diagnostics));
  }

  private static Result<SkillDirectoryLoad> ScanFailed(string directoryPath, Exception ex) =>
      Result.Failure<SkillDirectoryLoad>(
          new DomainError("DirectoryLoadFailed", $"Skill directory could not be scanned: {directoryPath}: {ex.Message}"));

  private static IEnumerable<string> EnumerateVisibleSubdirectories(string directoryPath) =>
      Directory.EnumerateDirectories(directoryPath)
          .Where(f => !Path.GetFileName(f).StartsWith('.'))
          .OrderBy(f => f, StringComparer.Ordinal);

  /// <summary>File-name matching is case-sensitive (agentskills.io convention);
  ///     a Windows existence probe would match any casing, so the folder is
  ///     enumerated and names compared ordinally.</summary>
  private static bool HasCaseSensitiveFile(string folder, string fileName) =>
      Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path) == fileName);

  private static SkillFileOutcome LoadSkillFile(string folder, string skillPath, DateTimeOffset timestamp)
  {
    string text;
    try
    {
      text = File.ReadAllText(skillPath);
    }
    catch (IOException ex)
    {
      return new SkillFileOutcome(null, [$"invalid skill file {skillPath}: {ex.Message}"]);
    }
    catch (UnauthorizedAccessException ex)
    {
      return new SkillFileOutcome(null, [$"invalid skill file {skillPath}: {ex.Message}"]);
    }

    Result<ParsedSkill> parsed = SkillMarkdown.Parse(text);
    if (!parsed.IsSuccess)
    {
      return new SkillFileOutcome(null, [$"invalid skill file {skillPath}: {parsed.Error.Message}"]);
    }

    ParsedSkill skill = parsed.Value;
    SkillDefinition definition = new(
        skill.Name,
        skill.Description,
        skill.Body,
        skill.Version ?? 1,
        SkillSource.File,
        ProvenanceSessionId: null,
        timestamp,
        timestamp,
        skill.Manual,
        Origin: folder);
    return new SkillFileOutcome(definition, [.. skill.Warnings.Select(w => $"{skill.Name}: {w}")]);
  }

  private sealed record SkillFileOutcome(SkillDefinition? Skill, IReadOnlyList<string> Diagnostics);
}
#pragma warning restore CA1849 // Call async methods when in an async method

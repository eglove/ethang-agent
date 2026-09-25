using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>One skill folder inside a staged clone, ready to scan and promote
/// (spec #27 task 3). Name is the installed folder name; SourcePath is the
/// staged folder to copy.</summary>
public sealed record StagedSkill(string Name, string SourcePath);

/// <summary>Install layout rules (spec #27 decision 7): a skills/ directory
/// with valid subfolders wins; otherwise a root SKILL.md makes the repo ONE
/// skill named after the repo; a sub-skill address resolves exactly that
/// folder (skills/ first, then the staged root). Names are validated, never
/// renamed: an invalid folder name fails the whole install.</summary>
public static class RegistryLayoutNormalizer
{
  private const string SkillFileName = "SKILL.md";
  private const int MaxNameLength = 64;

  public static Result<IReadOnlyList<StagedSkill>> Normalize(string stagedRoot, string? subSkillFolder, string repoName) =>
      Directory.Exists(stagedRoot)
          ? Route(stagedRoot, subSkillFolder, repoName)
          : Fail("InvalidLayout", $"Staged repository root does not exist: {stagedRoot}");

  private static Result<IReadOnlyList<StagedSkill>> Route(string stagedRoot, string? subSkillFolder, string repoName) =>
      subSkillFolder is not null
          ? ResolveSubSkill(stagedRoot, subSkillFolder)
          : ResolveWholeRepo(stagedRoot, repoName);

  private static Result<IReadOnlyList<StagedSkill>> ResolveSubSkill(string stagedRoot, string subSkillFolder)
  {
    string nested = Path.Combine(stagedRoot, "skills", subSkillFolder);
    string flat = Path.Combine(stagedRoot, subSkillFolder);
    return HasSkillMd(nested)
        ? Single(subSkillFolder, nested)
        : FlatOrMissing(subSkillFolder, flat);
  }

  private static Result<IReadOnlyList<StagedSkill>> FlatOrMissing(string subSkillFolder, string flat) =>
      HasSkillMd(flat)
          ? Single(subSkillFolder, flat)
          : Result.Failure<IReadOnlyList<StagedSkill>>(
              new DomainError("SkillFolderNotFound", $"No skill folder '{subSkillFolder}' in the repository (looked in skills/ and the repository root)."));

  private static Result<IReadOnlyList<StagedSkill>> ResolveWholeRepo(string stagedRoot, string repoName)
  {
    List<StagedSkill> staged = [];
    string skillsDir = Path.Combine(stagedRoot, "skills");
    foreach (string folder in VisibleSubdirectories(skillsDir))
    {
      if (!HasSkillMd(folder))
      {
        continue;
      }

      string name = Path.GetFileName(folder);
      if (!IsValidSkillName(name))
      {
        return Fail("InvalidLayout", $"Skill folder name '{name}' is not a valid skill name (lowercase letters, digits, hyphens; starts with a letter or digit; 64 characters max) — it must be renamed at the source, not silently installed.");
      }

      staged.Add(new StagedSkill(name, folder));
    }

    return staged.Count > 0
        ? Result.Success<IReadOnlyList<StagedSkill>>(staged)
        : RootOrEmpty(stagedRoot, repoName);
  }

  private static Result<IReadOnlyList<StagedSkill>> RootOrEmpty(string stagedRoot, string repoName) =>
      HasSkillMd(stagedRoot)
          ? Single(repoName, stagedRoot)
          : Fail("InvalidLayout", "No installable skills found: no skills/ subfolders with SKILL.md and no SKILL.md at the repository root.");

  private static Result<IReadOnlyList<StagedSkill>> Single(string name, string path) =>
      !IsValidSkillName(name)
          ? Fail("InvalidLayout", $"Skill name '{name}' is not valid (lowercase letters, digits, hyphens; starts with a letter or digit; 64 characters max).")
          : Result.Success<IReadOnlyList<StagedSkill>>([new StagedSkill(name, path)]);

  private static IEnumerable<string> VisibleSubdirectories(string directory) =>
      Directory.Exists(directory)
          ? Directory.EnumerateDirectories(directory)
              .Where(f => !Path.GetFileName(f).StartsWith('.'))
              .OrderBy(f => f, StringComparer.Ordinal)
          : [];

  /// <summary>SKILL.md matching is case-sensitive (the agentskills.io and
  /// DirectorySkillSource convention); a Windows existence probe would match
  /// any casing, so names are compared ordinally.</summary>
  private static bool HasSkillMd(string folder) =>
      Directory.Exists(folder) &&
      Directory.EnumerateFiles(folder).Any(p => Path.GetFileName(p) == SkillFileName);

  private static bool IsValidSkillName(string name) =>
      name.Length is >= 1 and <= MaxNameLength &&
      char.IsLetterOrDigit(name[0]) &&
      name.All(c => char.IsLetterOrDigit(c) || c == '-');

  private static Result<IReadOnlyList<StagedSkill>> Fail(string code, string message) =>
      Result.Failure<IReadOnlyList<StagedSkill>>(new DomainError(code, message));
}

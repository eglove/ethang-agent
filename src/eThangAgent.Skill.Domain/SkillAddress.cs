using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>The source kinds the skill registry addresses (spec #27).</summary>
public enum SkillAddressKind
{
  OwnerRepo,
  OwnerRepoSkill,
  GitUrl,
  SkillsShEntry,
}

/// <summary>A parsed skill-registry address (spec #27 task 1). Grammar:
/// owner/repo, owner/repo/skill, an http(s) git URL, or a dot-free single
/// token (a skills.sh entry, resolved over the network before cloning).
/// Create is total: everything else fails InvalidAddress with the reason.</summary>
public sealed record SkillAddress(SkillAddressKind Kind, string Owner, string Repo, string? SubSkill, string Host, string Raw)
{
  private const int MaxRawLength = 300;
  private const int MaxTokenLength = 100;
  private const string DefaultHost = "github.com";

  public static Result<SkillAddress> Create(string? raw) =>
      string.IsNullOrWhiteSpace(raw)
          ? Fail("Address must be a non-empty string.")
          : Build(raw.Trim());

  private static Result<SkillAddress> Build(string trimmed) =>
      trimmed.Length > MaxRawLength
          ? Fail($"Address exceeds {MaxRawLength} characters.")
          : Route(trimmed);

  private static Result<SkillAddress> Route(string trimmed) =>
      IsUrl(trimmed) ? ParseUrl(trimmed) : ParseSegments(trimmed);

  /// <summary>The HTTPS clone URL. skills.sh entries are a programmer-error
  /// path here: they must be resolved through the registry fetch first.</summary>
  public Uri ToCloneUrl()
  {
    return Kind == SkillAddressKind.SkillsShEntry
        ? throw new InvalidOperationException(
            "A skills.sh entry must be resolved to a GitHub address before cloning.")
        : ToCloneUrlCore();
  }

  private Uri ToCloneUrlCore()
  {
    return Kind == SkillAddressKind.GitUrl && Host.Length == 0
        ? new Uri(Raw)
        : HttpsCloneUri();
  }

  private string DefaultCloneUrl() => $"https://{DefaultHost}/{Owner}/{Repo}.git";

  private Uri HttpsCloneUri() => new(Kind == SkillAddressKind.GitUrl
      ? $"https://{Host}/{Owner}/{Repo}.git"
      : DefaultCloneUrl());

  private static bool IsUrl(string trimmed) =>
      trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
      trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
      trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

  private static Result<SkillAddress> ParseUrl(string trimmed)
  {
    int schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal) + 3;
    string remainder = trimmed[schemeEnd..];
    if (remainder.StartsWith('/'))
    {
      return FileUrlSegments(trimmed, remainder);
    }

    int firstSlash = remainder.IndexOf('/', StringComparison.Ordinal);
    return firstSlash <= 0
        ? Fail("URL is missing a host or path.")
        : UrlSegments(trimmed, remainder[..firstSlash], remainder[(firstSlash + 1)..]);
  }

  /// <summary>file:/// URLs carry no host: the repository name is the last
  /// path segment. Whole-path character validation is skipped (local paths
  /// legitimately contain spaces, colons, and unicode); the derived repo
  /// folder name still validates against the skill-name token rule.</summary>
  private static Result<SkillAddress> FileUrlSegments(string trimmed, string path)
  {
    string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length < 1)
    {
      return Fail("file URL path must end with a repository folder name.");
    }

    string repo = segments[^1];
    repo = repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;
    return !IsValidToken(repo)
        ? Fail($"Repository folder '{repo}' is not a valid skill-name source.")
        : Result.Success(new SkillAddress(SkillAddressKind.GitUrl, string.Empty, repo, SubSkill: null, string.Empty, trimmed));
  }

  private static Result<SkillAddress> UrlSegments(string trimmed, string host, string path)
  {
    string[] segments = path.Split('/');
    return segments.Length != 2 || segments.Any(string.IsNullOrEmpty)
        ? Fail("URL path must be exactly owner/repo with no trailing slash.")
        : UrlAddress(trimmed, host, segments[0], segments[1]);
  }

  private static Result<SkillAddress> UrlAddress(string trimmed, string host, string owner, string repo) =>
      !IsValidToken(host) || !IsValidToken(owner) || !IsValidToken(repo)
          ? Fail("URL host or path segments contain invalid characters.")
          : Result.Success(new SkillAddress(SkillAddressKind.GitUrl, owner, StripGitSuffix(repo), SubSkill: null, host, trimmed));

  private static Result<SkillAddress> ParseSegments(string trimmed)
  {
    string[] parts = trimmed.Split('/');
    return parts.Any(string.IsNullOrEmpty)
        ? Fail("Address contains an empty segment.")
        : SegmentsByCount(trimmed, parts);
  }

  private static Result<SkillAddress> SegmentsByCount(string trimmed, string[] parts) =>
      parts.Length switch
      {
        1 => SingleToken(trimmed, parts[0]),
        2 => OwnerRepo(trimmed, parts),
        3 => SubSkillAddress(trimmed, parts),
        _ => Fail("Address has too many segments (owner/repo/skill is the deepest form)."),
      };

  private static Result<SkillAddress> SingleToken(string trimmed, string token) =>
      token.Contains('.', StringComparison.Ordinal)
          ? Fail($"'{token}' is not a valid address: a single token may not contain dots (not a domain, not a path).")
          : EntryLengthGate(trimmed, token);

  private static Result<SkillAddress> EntryLengthGate(string trimmed, string token) =>
      token.Length > MaxTokenLength
          ? Fail($"Entry token exceeds {MaxTokenLength} characters.")
          : Result.Success(new SkillAddress(SkillAddressKind.SkillsShEntry, string.Empty, string.Empty, SubSkill: null, "skills.sh", trimmed));

  private static Result<SkillAddress> OwnerRepo(string trimmed, string[] parts)
  {
    string invalid = parts.FirstOrDefault(p => !IsValidToken(p)) ?? parts[0];
    return !IsValidToken(parts[0]) || !IsValidToken(parts[1])
        ? Fail($"Segment '{invalid}' contains invalid characters.")
        : Result.Success(new SkillAddress(SkillAddressKind.OwnerRepo, parts[0], StripGitSuffix(parts[1]), SubSkill: null, DefaultHost, trimmed));
  }

  private static Result<SkillAddress> SubSkillAddress(string trimmed, string[] parts) =>
      !IsValidToken(parts[2]) || parts[2] is "." or ".."
          ? Fail($"Skill segment '{parts[2]}' is not a valid folder name.")
          : Result.Success(new SkillAddress(SkillAddressKind.OwnerRepoSkill, parts[0], StripGitSuffix(parts[1]), parts[2], DefaultHost, trimmed));

  /// <summary>Tokens allow letters, digits, dot, underscore, hyphen; reject
  /// dot-only names ('.', '..') and anything over 100 characters.</summary>
  private static bool IsValidToken(string token) =>
      token.Length is >= 1 and <= MaxTokenLength &&
      token is not ("." or "..") &&
      token.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

  private static string StripGitSuffix(string repo) =>
      repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

  private static Result<SkillAddress> Fail(string message) =>
      Result.Failure<SkillAddress>(new DomainError("InvalidAddress", message));
}
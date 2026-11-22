using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What one submitted message resolves to: the text the transcript shows, the notices
///     for references that resolved to nothing, and the annotation that names the message's
///     references to the model. Nothing is appended beneath the message and nothing is read: a
///     reference is a pointer, and what it points at is the model's to open.</summary>
internal sealed record ReferenceResolution(
    string MessageText,
    IReadOnlyList<string> Notices,
    string Annotation);

/// <summary>Resolves every <c>@</c> reference in a submitted message to the thing it names: a
///     catalog skill, or the path a workspace file resolves to.
///     <para>NOTHING IS READ. A reference is a pointer the user put in their message, and
///     whether the model needs what it points at is the model's judgement - it may already hold
///     the file, or the skill's body, from an earlier turn, and a harness that fetched it anyway
///     would spend the user's tokens re-delivering what the conversation already carries. The
///     resolution therefore produces an annotation naming each reference and what it resolved
///     to, and stops there; the read tool and skill_view are the only things that fetch.</para>
///     <para>A QUOTED path (<c>@"C:\..."</c>) is granted to the session, so the same message
///     that names an external file can also have it opened when the model chooses to. The
///     grant is the quoted path's own effect - a bare path in prose grants nothing - and it is
///     read-only, because naming a file says what to look at and never that it may be
///     written.</para>
///     <para>A reference that resolves to nothing posts a NOTICE and is skipped: the message
///     still sends, because the user asked for the message and a name this session cannot
///     resolve is information about it rather than a reason to withhold it.</para></summary>
internal sealed class ReferenceResolver(
    ISkillCatalog? skills,
    string workspaceRoot,
    SessionPathGrants? grants = null,
    IPathResolver? trustedRoots = null)
{
  private readonly ISkillCatalog? _skills = skills;
  private readonly SessionPathGrants? _grants = grants;
  private readonly WorkspacePathResolver _resolver = new(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
  private readonly IPathResolver? _trustedRoots = trustedRoots;

  public async Task<ReferenceResolution> ResolveAsync(string input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    List<string> notices = [];
    List<string> references = [];

    // The grant is made BEFORE anything is resolved, so a quoted path is reachable by the very
    // message that quoted it - and by every later message, which is what makes it a session
    // grant rather than a one-shot resolution.
    List<string> quoted = ReferenceTokenParser.QuotedPaths(input);
    foreach (string path in quoted)
    {
      _grants?.Grant(path);
    }

    foreach (string name in Names(input))
    {
      Result<string> skill = await SkillLineAsync(name, ct).ConfigureAwait(true);
      if (skill.IsSuccess)
      {
        references.Add(skill.Value);
        continue;
      }

      // AN AMBIGUOUS NAME IS THE USER'S TO SETTLE, not a fall-through: two skills carry the
      // name, so resolving it as a file would silently pick a third thing the user never
      // named. A name NO skill carries is different - it may be a file - so only that falls
      // through.
      if (string.Equals(skill.Error.Code, "AmbiguousSkill", StringComparison.Ordinal))
      {
        notices.Add($"Error [{skill.Error.Code}]: {skill.Error.Message}");
        continue;
      }

      Result<string> file = FileLine(name, granted: false);
      if (!file.IsSuccess)
      {
        notices.Add(file.Error.Message);
        continue;
      }

      references.Add(file.Value);
    }

    // Quoted paths resolve as FILES only: the quotes say 'this is a path', so a quoted word
    // that happens to match a skill name is still a path and must not resolve to a skill.
    foreach (string path in quoted)
    {
      Result<string> file = FileLine(path, granted: true);
      if (!file.IsSuccess)
      {
        notices.Add(file.Error.Message);
        continue;
      }

      references.Add(file.Value);
    }

    return new ReferenceResolution(
        ReferenceTokenParser.Unescape(input), notices, Annotation(references));
  }

  /// <summary>Every unescaped <c>@</c> token in the message, in order, without its trigger.</summary>
  public static List<string> Names(string input)
  {
    List<string> names = [];
    int i = 0;
    while (i < input.Length)
    {
      if (input[i] != '@')
      {
        i++;
        continue;
      }

      if (i + 1 < input.Length && input[i + 1] == '@')
      {
        i += 2; // escaped literal: never resolved
        continue;
      }

      int end = i + 1;
      while (end < input.Length && IsTokenCharacter(input[end]))
      {
        end++;
      }

      if (end > i + 1 && (i == 0 || IsWordBoundary(input[i - 1])))
      {
        names.Add(input[(i + 1)..end]);
      }

      i = end;
    }

    return names;
  }

  /// <summary>One skill reference's annotation line, or the failure that says no skill carries
  ///     the name. The catalog is consulted for the name and the description only: the body is
  ///     what skill_view is for, and the reference does not fetch it.</summary>
  private async Task<Result<string>> SkillLineAsync(string name, CancellationToken ct)
  {
    if (_skills is null)
    {
      return Result.Failure<string>(new DomainError("SkillNotFound", $"No skill named '{name}'."));
    }

    Result<IReadOnlyList<SkillDefinition>> listed = await _skills.ListAsync(ct).ConfigureAwait(true);
    if (!listed.IsSuccess)
    {
      return Result.Failure<string>(listed.Error);
    }

    List<SkillDefinition> matches = [.. listed.Value
        .Where(skill => string.Equals(skill.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))];
    if (matches.Count == 0)
    {
      return Result.Failure<string>(new DomainError("SkillNotFound",
          $"No skill named '{name.Trim()}' is in the catalog."));
    }

    if (matches.Count > 1)
    {
      return Result.Failure<string>(new DomainError("AmbiguousSkill",
          $"'{name.Trim()}' matches multiple skills: {string.Join(", ", matches.Select(m => m.Name))}. Name one exactly."));
    }

    SkillDefinition skill = matches[0];
    return Result.Success($"@{name} → skill '{skill.Name}': {skill.Description}");
  }

  /// <summary>One file reference's annotation line, or the notice that says why it names
  ///     nothing this session can reach. Nothing is read: the line names the path and leaves
  ///     the reading to the model.</summary>
  private Result<string> FileLine(string name, bool granted)
  {
    Result<string> resolved = ResolveReference(name);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<string>(resolved.Error.Code == "PathOutsideWorkspace"
          ? new DomainError("ReferenceOutsideWorkspace",
              $"Error [ReferenceOutsideWorkspace]: '@{name}' resolves outside this workspace, "
              + "and only a quoted path grants a location beyond it.")
          : resolved.Error);
    }

    string path = resolved.Value;
    return Result.Success(granted
        ? $"@{name} → {path} (granted read-only for this session)"
        : $"@{name} → {path}");
  }

  /// <summary>The model-facing annotation naming the message's references, or empty when it
  ///     names none. It states what the references ARE and that fetching them is the model's
  ///     call, because the message carries no content: an annotation that demanded a read would
  ///     spend a turn re-delivering what the conversation may already hold.</summary>
  private static string Annotation(List<string> references)
  {
    if (references.Count == 0)
    {
      return string.Empty;
    }

    StringBuilder sb = new();
    _ = sb.Append("[references] this message names ")
        .Append(references.Count.ToString(CultureInfo.InvariantCulture))
        .Append(references.Count == 1 ? " reference" : " references")
        .AppendLine("; fetch one only if you need it - it may already be in your context. "
            + "A file is read with the read tool, a skill with skill_view.");
    foreach (string line in references)
    {
      _ = sb.AppendLine(line);
    }

    return sb.ToString().TrimEnd();
  }

  /// <summary>Resolves one reference through the workspace first, then the session's quoted-path
  ///     grants, so an external path works only because the user quoted it. The trusted roots
  ///     are consulted last, because they are what the READ TOOLS resolve against: a reference
  ///     the tools could open must not be refused here.</summary>
  private Result<string> ResolveReference(string name)
  {
    Result<string> resolved = _resolver.Resolve(name);
    if (resolved.IsSuccess)
    {
      return resolved;
    }

    if (_trustedRoots is not null)
    {
      Result<string> trusted = _trustedRoots.Resolve(name);
      if (trusted.IsSuccess)
      {
        return trusted;
      }
    }

    IReadOnlyList<TrustedDirectory> granted = _grants?.List ?? [];
    return granted.Count == 0
        ? resolved
        : new TrustedDirectoryPathResolver(_resolver, granted, enforceWritePolicy: false).Resolve(name);
  }

  private static bool IsWordBoundary(char value)
      => char.IsWhiteSpace(value) || value is '(' or '[' or '"' or '@';

  private static bool IsTokenCharacter(char value)
      => char.IsLetterOrDigit(value) || value is '-' or '_' or '.' or '/' or '\\';
}

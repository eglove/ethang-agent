using eThangAgent.Agent.Application;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What one submitted message resolves to: the text the transcript shows, the
///     read-tool pages appended beneath it, and the notices for references that resolved to
///     nothing. A notice never stops the message: the user asked for the message to be sent,
///     and a reference that named nothing is information about the message, not a reason to
///     withhold it.</summary>
internal sealed record ReferenceInjectionResult(
    string MessageText,
    IReadOnlyList<string> AppendedBlocks,
    IReadOnlyList<string> Notices,
    int ReferenceCount,
    int EstimatedTokens);

/// <summary>Resolves every <c>@</c> reference in a submitted message: a catalog skill name
///     wins over a workspace file, and a file renders through the read tool's own output format
///     for one page of lines. The freshness ledger is deliberately NOT touched: it is the
///     agent's per-turn memory, reset at turn start, so a record made here would be cleared
///     before the model ever read from it.
///     <para>A QUOTED path (<c>@"C:\..."</c>) is granted to the session before it is read, so
///     the same message that names an external file can also have it opened. The grant is the
///     quoted path's own effect - a bare path in prose grants nothing - and it is read-only,
///     because naming a file says what to look at and never that it may be written.</para>
///     <para>A reference that resolves to nothing posts a NOTICE and is skipped. It used to
///     refuse the whole message, which made a typo or a stale path cost the user everything
///     they had typed; the message now sends and the notice says what was left out.</para></summary>
internal sealed class ReferenceInjector(
    IFileSystemAccess files,
    SkillInvocationService? skills,
    string workspaceRoot,
    SessionPathGrants? grants = null)
{
  private const int PageLines = 1000;

  /// <summary>The rough characters-per-token ratio the composer's cost readout divides by.</summary>
  public const int CharactersPerToken = 4;

  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly SkillInvocationService? _skills = skills;
  private readonly SessionPathGrants? _grants = grants;
  private readonly WorkspacePathResolver _resolver = new(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));

  public async Task<ReferenceInjectionResult> InjectAsync(string input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    List<string> blocks = [];
    List<string> notices = [];
    int references = 0;

    // The grant is made BEFORE anything is read, so a quoted path is reachable by the very
    // message that quoted it - and by every later message, which is what makes it a session
    // grant rather than a one-shot resolution.
    List<string> quoted = ReferenceTokenParser.QuotedPaths(input);
    foreach (string path in quoted)
    {
      _grants?.Grant(path);
    }

    foreach (string name in Names(input))
    {
      Result<string> skill = await SkillBlockAsync(name, ct).ConfigureAwait(true);
      if (skill.IsSuccess)
      {
        blocks.Add(skill.Value);
        references++;
        continue;
      }

      Result<string> file = await FileBlockAsync(name, ct).ConfigureAwait(true);
      if (!file.IsSuccess)
      {
        notices.Add(file.Error.Message);
        continue;
      }

      blocks.Add(file.Value);
      references++;
    }

    // Quoted paths resolve as FILES only: the quotes say 'this is a path', so a quoted word
    // that happens to match a skill name is still a path and must not invoke anything.
    foreach (string path in quoted)
    {
      Result<string> file = await FileBlockAsync(path, ct).ConfigureAwait(true);
      if (!file.IsSuccess)
      {
        notices.Add(file.Error.Message);
        continue;
      }

      blocks.Add(file.Value);
      references++;
    }

    int characters = blocks.Sum(b => b.Length);
    return new ReferenceInjectionResult(
        ReferenceTokenParser.Unescape(input), blocks, notices, references, characters / CharactersPerToken);
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

  private async Task<Result<string>> SkillBlockAsync(string name, CancellationToken ct)
  {
    if (_skills is null)
    {
      return Result.Failure<string>(new DomainError("SkillNotFound", $"No skill named '{name}'."));
    }

    Result<SkillInvocation> invoked = await _skills.InvokeAsync(name, null, ct).ConfigureAwait(true);
    return invoked.IsSuccess
        ? Result.Success(invoked.Value.SystemLine)
        : Result.Failure<string>(invoked.Error);
  }

  /// <summary>One reference's read output, or the notice that says why it is absent. A path
  ///     outside the workspace is not a fault either: the notice names the refusal so the
  ///     model and the user learn the session's reach from it.</summary>
  private async Task<Result<string>> FileBlockAsync(string name, CancellationToken ct)
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
    Result<FileRead> read = await _files.ReadLinesAsync(path, 1, PageLines, ct).ConfigureAwait(true);
    if (!read.IsSuccess)
    {
      return Result.Failure<string>(new DomainError("ReferenceNotFound",
          $"Error [ReferenceNotFound]: '@{name}' does not exist in this workspace."));
    }

    FileRead file = read.Value;
    string block = ReadOutputRenderer.Render(path, 1, PageLines, file, new FileCoverage([]), forced: false);
    return Result.Success(block);
  }

  /// <summary>Resolves one reference through the workspace first, then the session's quoted-path
  ///     grants, so an external path works only because the user quoted it.</summary>
  private Result<string> ResolveReference(string name)
  {
    Result<string> resolved = _resolver.Resolve(name);
    IReadOnlyList<TrustedDirectory> granted = _grants?.List ?? [];
    return resolved.IsSuccess || granted.Count == 0
        ? resolved
        : new TrustedDirectoryPathResolver(_resolver, granted, enforceWritePolicy: false).Resolve(name);
  }

  private static bool IsWordBoundary(char value)
    => char.IsWhiteSpace(value) || value is '(' or '[' or '"' or '@';

  private static bool IsTokenCharacter(char value)
    => char.IsLetterOrDigit(value) || value is '-' or '_' or '.' or '/' or '\\';
}

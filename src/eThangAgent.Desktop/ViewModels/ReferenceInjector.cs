using eThangAgent.Agent.Application;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>What one submitted message resolves to: the text the transcript shows, the
///     read-tool pages appended beneath it, and the refusal that stops the send.</summary>
internal sealed record ReferenceInjectionResult(
    bool IsSuccess,
    string MessageText,
    IReadOnlyList<string> AppendedBlocks,
    string? FailureNotice,
    int ReferenceCount,
    int EstimatedTokens);

/// <summary>Resolves every <c>@</c> reference in a submitted message: a catalog skill name
///     wins over a workspace file, and a file renders through the read tool's own output format
///     for one page of lines. The freshness ledger is deliberately NOT touched: it is the
///     agent's per-turn memory, reset at turn start, so a record made here would be cleared
///     before the model ever read from it.</summary>
internal sealed class ReferenceInjector(
    IFileSystemAccess files,
    SkillInvocationService? skills,
    string workspaceRoot)
{
  private const int PageLines = 1000;

  /// <summary>The rough characters-per-token ratio the composer's cost readout divides by.</summary>
  public const int CharactersPerToken = 4;

  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly SkillInvocationService? _skills = skills;
  private readonly WorkspacePathResolver _resolver = new(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));

  public async Task<ReferenceInjectionResult> InjectAsync(string input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    List<string> blocks = [];
    int references = 0;
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
        return new ReferenceInjectionResult(false, ReferenceTokenParser.Unescape(input), [], file.Error.Message, references, 0);
      }

      blocks.Add(file.Value);
      references++;
    }

    int characters = blocks.Sum(b => b.Length);
    return new ReferenceInjectionResult(
        true, ReferenceTokenParser.Unescape(input), blocks, null, references, characters / CharactersPerToken);
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

  private async Task<Result<string>> FileBlockAsync(string name, CancellationToken ct)
  {
    Result<string> resolved = _resolver.Resolve(name);
    if (!resolved.IsSuccess)
    {
      return Result.Failure<string>(resolved.Error.Code == "PathOutsideWorkspace"
          ? new DomainError("ReferenceOutsideWorkspace",
              $"Error [ReferenceOutsideWorkspace]: '@{name}' resolves outside this workspace.")
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

  private static bool IsWordBoundary(char value)
    => char.IsWhiteSpace(value) || value is '(' or '[' or '"' or '@';

  private static bool IsTokenCharacter(char value)
    => char.IsLetterOrDigit(value) || value is '-' or '_' or '.' or '/' or '\\';
}

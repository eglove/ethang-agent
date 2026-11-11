using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Lsp;

/// <summary>Applies a rename edit set all-or-nothing over the file seams. Every target is
/// resolved, read, and rewritten in memory before the first write; a write that fails
/// partway restores the files already written. A position past the end of the text is a
/// refusal, never a clamp.
/// <para>Known limitation: <see cref="IFileWriteAccess.WriteFileAsync"/> writes UTF-8
/// without a BOM, so a file that carried a BOM loses it when a rename rewrites it. The
/// BOM is stripped before offsets are computed (so line and character positions match the
/// text the server saw) and is not restored on write.</para></summary>
public sealed class LspRenameApplier(IFileSystemAccess read, IFileWriteAccess write, string workspaceRoot) : ILspRenameApplier
{
  private const string BinSegment = "bin";
  private const string ObjSegment = "obj";
  private const char ByteOrderMark = '\uFEFF';

  private readonly IFileSystemAccess _read = read ?? throw new ArgumentNullException(nameof(read));
  private readonly IFileWriteAccess _write = write ?? throw new ArgumentNullException(nameof(write));
  private readonly WorkspacePathResolver _resolver =
      new(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));

  public async Task<Result<LspRenameApplied>> ApplyAsync(IReadOnlyList<LspRenameEdit> edits, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(edits);
    if (edits.Count == 0)
    {
      return Fail<LspRenameApplied>("NoEdits", "The rename produced no edits; nothing was applied.");
    }

    Result<Dictionary<string, List<LspRenameEdit>>> grouped = GroupByResolvedPath(edits);
    if (!grouped.IsSuccess)
    {
      return Result.Failure<LspRenameApplied>(grouped.Error);
    }

    Dictionary<string, string> originals = [with(StringComparer.OrdinalIgnoreCase)];
    Dictionary<string, string> rewritten = [with(StringComparer.OrdinalIgnoreCase)];
    foreach (KeyValuePair<string, List<LspRenameEdit>> file in grouped.Value)
    {
      Result<byte[]> bytes = await _read.ReadBytesAsync(file.Key, ct).ConfigureAwait(false);
      if (!bytes.IsSuccess)
      {
        return Result.Failure<LspRenameApplied>(bytes.Error);
      }

      string text = Decode(bytes.Value);
      Result<string> applied = ApplyToText(text, file.Value, file.Key);
      if (!applied.IsSuccess)
      {
        return Result.Failure<LspRenameApplied>(applied.Error);
      }

      originals[file.Key] = text;
      rewritten[file.Key] = applied.Value;
    }

    List<string> paths = [.. grouped.Value.Keys];
    List<string> written = [];
    for (int index = 0; index < paths.Count; index++)
    {
      string path = paths[index];
      Result<FileWriteOutcome> outcome = await _write
          .WriteFileAsync(path, rewritten[path], overwrite: true, ct)
          .ConfigureAwait(false);
      if (!outcome.IsSuccess)
      {
        return await RestoreAsync(written, originals, outcome.Error, ct).ConfigureAwait(false);
      }

      written.Add(path);
    }

    return Result.Success(new LspRenameApplied(written, edits.Count));
  }

  private Result<Dictionary<string, List<LspRenameEdit>>> GroupByResolvedPath(IReadOnlyList<LspRenameEdit> edits)
  {
    Dictionary<string, List<LspRenameEdit>> grouped = [with(StringComparer.OrdinalIgnoreCase)];
    foreach (LspRenameEdit edit in edits)
    {
      Result<string> resolved = ResolveTarget(_resolver, edit.Path);
      if (!resolved.IsSuccess)
      {
        return Result.Failure<Dictionary<string, List<LspRenameEdit>>>(resolved.Error);
      }

      if (!grouped.TryGetValue(resolved.Value, out List<LspRenameEdit>? fileEdits))
      {
        fileEdits = [];
        grouped[resolved.Value] = fileEdits;
      }

      fileEdits.Add(edit);
    }

    return Result.Success(grouped);
  }

  private static Result<string> ResolveTarget(WorkspacePathResolver resolver, string path)
  {
    Result<string> resolved = resolver.Resolve(path);
    return !resolved.IsSuccess || !IsExcluded(resolved.Value)
        ? resolved
        : Result.Failure<string>(new DomainError("PathExcluded",
            $"'{resolved.Value}' is under a build-output directory (bin or obj) and is never renamed."));
  }

  private static bool IsExcluded(string fullPath)
  {
    string[] segments = fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    return segments.Any(s => string.Equals(s, BinSegment, StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, ObjSegment, StringComparison.OrdinalIgnoreCase));
  }

  private static string Decode(byte[] bytes)
  {
    string text = Encoding.UTF8.GetString(bytes);
    return text.Length > 0 && text[0] == ByteOrderMark ? text[1..] : text;
  }

  private static Result<string> ApplyToText(string text, List<LspRenameEdit> edits, string path)
  {
    List<(int Start, int End, string NewText)> spans = [with(edits.Count)];
    foreach (LspRenameEdit edit in edits)
    {
      int start = OffsetOf(text, edit.Line, edit.Character);
      int end = OffsetOf(text, edit.EndLine, edit.EndCharacter);
      if (start < 0 || end < 0)
      {
        return Fail<string>("InvalidPosition",
            $"The edit at line {edit.Line}, character {edit.Character} is past the end of '{path}'.");
      }

      if (end < start)
      {
        return Fail<string>("InvalidPosition",
            $"The edit at line {edit.Line}, character {edit.Character} ends before it starts in '{path}'.");
      }

      spans.Add((start, end, edit.NewText));
    }

    spans.Sort((left, right) => right.Start.CompareTo(left.Start));
    StringBuilder builder = new(text);
    int nextStart = text.Length + 1;
    foreach ((int start, int end, string newText) in spans)
    {
      if (end > nextStart)
      {
        return Fail<string>("OverlappingEdits", $"Two edits overlap in '{path}'.");
      }

      _ = builder.Remove(start, end - start).Insert(start, newText);
      nextStart = start;
    }

    return Result.Success(builder.ToString());
  }

  /// <summary>Absolute index of a 0-based (line, character) position, or -1 when the
  ///     position is past the end of the text. A character position may not cross the
  ///     line's terminator.</summary>
  private static int OffsetOf(string text, int line, int character)
  {
    if (line < 0 || character < 0)
    {
      return -1;
    }

    int index = 0;
    for (int current = 0; current < line; current++)
    {
      int newline = text.IndexOf('\n', index, StringComparison.Ordinal);
      if (newline < 0)
      {
        return -1;
      }

      index = newline + 1;
    }

    int terminator = text.IndexOf('\n', index, StringComparison.Ordinal);
    int lineEnd = terminator < 0 ? text.Length : terminator;
    int contentEnd = lineEnd > index && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
    return character > contentEnd - index ? -1 : index + character;
  }

  private async Task<Result<LspRenameApplied>> RestoreAsync(
      IReadOnlyList<string> written, Dictionary<string, string> originals, DomainError failure, CancellationToken ct)
  {
    List<string> unrestored = [];
    foreach (string path in written)
    {
      Result<FileWriteOutcome> restore = await _write
          .WriteFileAsync(path, originals[path], overwrite: true, ct)
          .ConfigureAwait(false);
      if (!restore.IsSuccess)
      {
        unrestored.Add(path);
      }
    }

    return unrestored.Count == 0
        ? Result.Failure<LspRenameApplied>(failure)
        : Fail<LspRenameApplied>("RestoreFailed",
            $"{failure.Code}: {failure.Message} Restoring the already-written files also failed for: {string.Join(", ", unrestored)}.");
  }

  private static Result<T> Fail<T>(string code, string message)
      => Result.Failure<T>(new DomainError(code, message));
}

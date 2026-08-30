using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ReadTool(IPathResolver resolver, IFileSystemAccess files) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  public ToolDefinition Definition { get; } = new(
      "read",
      "Read a range of lines from a text file. timeoutSeconds, path, startLine, and endLine are all mandatory; line numbers are 1-based and inclusive. Output begins with an annotation line in [brackets] — it is metadata, not file content. Each content line is prefixed with its line number and →; the number and arrow are never part of the file. Never reproduce line numbers or arrows when creating or editing files. Cite line numbers as shown when referencing locations. If endLine exceeds the file length it is clamped and a [warning] is appended. Maximum range: 1000 lines per call.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("startLine", ToolParameterType.WholeNumber, "First line to read (1-based, inclusive).", Minimum: 1),
            new ToolParameter("endLine", ToolParameterType.WholeNumber, "Last line to read (1-based, inclusive).", Minimum: 1),
      ]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ReadToolInput> parsed = ReadToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    ReadToolInput args = parsed.Value;

    Result<string> resolved = _resolver.Resolve(args.Path);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        ReadAsync(args, resolved.Value, token), ct);
  }

  private async Task<ToolResult> ReadAsync(ReadToolInput args, string path, CancellationToken ct)
  {
    Result<FileRead> read = await _files.ReadLinesAsync(path, args.StartLine, args.EndLine, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    FileRead file = read.Value;
    if (file.LastLineRead == 0)
    {
      return Err(new DomainError("StartLineBeyondEof",
          $"'startLine' {args.StartLine} exceeds file length ({file.TotalLines} lines)."));
    }

    bool clamped = file.TotalLines < args.EndLine;
    int last = clamped ? file.TotalLines : args.EndLine;
    int width = last.ToString(CultureInfo.InvariantCulture).Length;
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[read {path} lines {args.StartLine}-{last} of {file.TotalLines} total]");
    foreach ((string? text, int i) in file.Lines.Select((t, i) => (t, i)))
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{(args.StartLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {text}");
    }

    if (clamped)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"[warning] endLine {args.EndLine} exceeded file length ({file.TotalLines}); clamped");
    }
    else
    {
      sb.Length -= Environment.NewLine.Length;  // trim trailing newline
    }

    return new ToolResult(sb.ToString(), false);
  }

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}

using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ReadTool : ITool
{
    private readonly IFileSystemAccess _files;

    public ToolDefinition Definition { get; } = new(
        "read",
        "Read a range of lines from a text file. timeoutSeconds, path, startLine, and endLine are all mandatory; line numbers are 1-based and inclusive. Output begins with an annotation line in [brackets] — it is metadata, not file content. Each content line is prefixed with its line number and →; the number and arrow are never part of the file. Never reproduce line numbers or arrows when creating or editing files. Cite line numbers as shown when referencing locations. If endLine exceeds the file length it is clamped and a [warning] is appended. Maximum range: 1000 lines per call.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.String, "Path to the file to read."),
            new ToolParameter("startLine", ToolParameterType.Integer, "First line to read (1-based, inclusive).", Minimum: 1),
            new ToolParameter("endLine", ToolParameterType.Integer, "Last line to read (1-based, inclusive).", Minimum: 1),
        ]);

    public ReadTool(IFileSystemAccess files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = ReadToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Error(parsed.Error!));
        var args = parsed.Value!;

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Error(budget.Error!));

        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            ReadAsync(args, token), ct);
    }

    private async Task<ToolResult> ReadAsync(ReadToolInput args, CancellationToken ct)
    {
        var read = await _files.ReadLinesAsync(args.Path, args.StartLine, args.EndLine, ct);
        if (!read.IsSuccess)
            return Error(read.Error!);

        var file = read.Value!;
        if (file.LastLineRead == 0)
            return Error(new Error("StartLineBeyondEof",
                $"'startLine' {args.StartLine} exceeds file length ({file.TotalLines} lines)."));

        var clamped = file.TotalLines < args.EndLine;
        var last = clamped ? file.TotalLines : args.EndLine;
        var width = last.ToString().Length;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[read {args.Path} lines {args.StartLine}-{last} of {file.TotalLines} total]");
        foreach (var (text, i) in file.Lines.Select((t, i) => (t, i)))
            sb.AppendLine($"{(args.StartLine + i).ToString().PadLeft(width)}→ {text}");
        if (clamped)
            sb.Append($"[warning] endLine {args.EndLine} exceeded file length ({file.TotalLines}); clamped");
        else
            sb.Length -= Environment.NewLine.Length;  // trim trailing newline

        return new ToolResult(sb.ToString(), false);
    }

    private static ToolResult Error(Error error) => new(
        $"Error [{error.Code}]: {error.Message}", true);
}

using System.Text;

namespace eThangAgent.ToolDomain;

public static class ExecResultFormatter
{
    public static ToolResult Format(ExecRunResult run, ExecOptions options, string? artifactPath)
    {
        if (run.Status != ExecRunStatus.Completed)
            return ErrorRun(run, options);

        var sb = new StringBuilder();
        var overCap = run.Output.Length > options.MaxOutputChars;
        if (overCap)
        {
            var half = options.MaxOutputChars / 2;
            sb.Append(run.Output[..half]);
            sb.AppendLine();
            sb.AppendLine($"[exec: output truncated — showing first {half} and last {half} of {run.Output.Length} characters]");
            if (artifactPath is not null)
                sb.AppendLine($"[exec:artifact {artifactPath}]");
            sb.Append(run.Output[^half..]);
        }
        else
        {
            sb.Append(run.Output);
            if (artifactPath is not null)
                sb.AppendLine().Append($"[exec:artifact {artifactPath}]");
        }

        foreach (var line in run.ErrorLines)
            sb.AppendLine().Append($"exec error [ScriptError]: {line}");

        return new ToolResult(sb.ToString(), run.ErrorLines.Count > 0);
    }

    public static ToolResult ParseErrors(IReadOnlyList<ExecParseError> errors, int maxParseErrors)
    {
        var sb = new StringBuilder("exec error [ExecParseError]: program failed validation.");
        foreach (var e in errors.Take(maxParseErrors))
            sb.Append($"\nline {e.Line}, col {e.Column}: {e.Message}");
        var hidden = errors.Count - Math.Min(errors.Count, maxParseErrors);
        if (hidden > 0)
            sb.Append($"\n[{hidden} more parse error(s) not shown]");
        return new ToolResult(sb.ToString(), true);
    }

    private static ToolResult ErrorRun(ExecRunResult run, ExecOptions options)
    {
        var code = run.Status switch
        {
            ExecRunStatus.Timeout => "ExecTimeout",
            ExecRunStatus.Cancelled => "ExecCancelled",
            _ => "ExecEngineFailure",
        };
        var sb = new StringBuilder($"exec error [{code}]: {run.ErrorMessage}");
        foreach (var line in run.ErrorLines)
            sb.Append($"\nexec error [ScriptError]: {line}");
        if (run.Output.Length > 0)
        {
            sb.AppendLine();
            sb.Append(ClampHead(run.Output, options.MaxErrorChars));
        }
        return new ToolResult(sb.ToString(), true);
    }

    private static string ClampHead(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + $"\n[exec: partial output truncated at {maxChars} characters]";
    }
}

using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

public static class ExecResultFormatter
{
  public static ToolResult Format(ExecRunResult run, ExecOptions options, string? artifactPath, string? title = null)
  {
    ArgumentNullException.ThrowIfNull(run);
    ArgumentNullException.ThrowIfNull(options);
    if (run.Status != ExecRunStatus.Completed)
    {
      return ErrorRun(run, options);
    }

    StringBuilder sb = new();
    bool overCap = run.Output.Length > options.MaxOutputChars;
    if (overCap)
    {
      int half = options.MaxOutputChars / 2;
      _ = sb.Append(run.Output[..half]);
      _ = sb.Append('\n');
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"[exec: output truncated — showing first {half} and last {half} of {run.Output.Length} characters]");
      if (artifactPath is not null)
      {
        _ = sb.Append('\n');
        _ = sb.Append(CultureInfo.InvariantCulture, $"[exec:artifact {artifactPath}]");
      }

      _ = sb.Append(run.Output[^half..]);
    }
    else
    {
      _ = sb.Append(run.Output);
      if (artifactPath is not null)
      {
        if (sb.Length > 0)
        {
          _ = sb.Append('\n');
        }

        _ = sb.Append(CultureInfo.InvariantCulture, $"[exec:artifact {artifactPath}]");
      }
    }

    foreach (string line in run.ErrorLines)
    {
      // Separators only join existing content: an empty-output run must not start
      // with a stray newline (the ^M-leading transcript rows this formatter once
      // produced).
      if (sb.Length > 0)
      {
        _ = sb.Append('\n');
      }

      _ = sb.Append(CultureInfo.InvariantCulture, $"exec error [ScriptError]: {line}");
    }

    return new ToolResult(sb.ToString(), run.ErrorLines.Count > 0, title);
  }

  public static ToolResult ParseErrors(IReadOnlyList<ExecParseError> errors, int maxParseErrors,
      IReadOnlyList<string>? hints = null)
  {
    ArgumentNullException.ThrowIfNull(errors);
    StringBuilder sb = new("exec error [ExecParseError]: program failed validation.");
    if (hints is { Count: > 0 })
    {
      foreach (string hint in hints)
      {
        _ = sb.Append(CultureInfo.InvariantCulture, $"\n{hint}");
      }
    }

    foreach (ExecParseError? e in errors.Take(maxParseErrors))
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\nline {e.Line}, col {e.Column}: {e.Message}");
    }

    int hidden = errors.Count - Math.Min(errors.Count, maxParseErrors);
    if (hidden > 0)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n[{hidden} more parse error(s) not shown]");
    }

    return new ToolResult(sb.ToString(), true);
  }

  private static ToolResult ErrorRun(ExecRunResult run, ExecOptions options)
  {
    string code = run.Status switch
    {
      ExecRunStatus.Timeout => "ExecTimeout",
      ExecRunStatus.Cancelled => "ExecCancelled",
      ExecRunStatus.Completed => throw new InvalidOperationException("Format must not be called on a completed run."),
      ExecRunStatus.EngineFailure => "ExecEngineFailure",
      _ => "ExecEngineFailure",
    };
    StringBuilder sb = new($"exec error [{code}]: {run.ErrorMessage}");
    foreach (string line in run.ErrorLines)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\nexec error [ScriptError]: {line}");
    }

    if (run.Output.Length > 0)
    {
      _ = sb.Append('\n');
      _ = sb.Append(ClampHead(run.Output, options.MaxErrorChars));
    }
    return new ToolResult(sb.ToString(), true);
  }

  private static string ClampHead(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars] + $"\n[exec: partial output truncated at {maxChars} characters]";
}

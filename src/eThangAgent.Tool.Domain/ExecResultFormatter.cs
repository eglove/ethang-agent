using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

public static class ExecResultFormatter
{
  public static ToolResult Format(ExecRunResult run, string? title = null)
      => Format(run, title, null);

  public static ToolResult Format(ExecRunResult run, string? title, ExecProgram? program)
  {
    ArgumentNullException.ThrowIfNull(run);
    if (run.Status != ExecRunStatus.Completed)
    {
      return ErrorRun(run);
    }

    StringBuilder sb = new(run.Output);

    // A completed run with no output is legitimate (the script returns void) — but
    // when the script dispatched nested tool calls, the likeliest cause is a result
    // discarded by a bare `Tools.Invoke(...);` statement. Append a visible hint so
    // the model re-issues with the capture pattern instead of reading blank as
    // success: two real sessions lost the nested result this way.
    if (run.Output.Length == 0 && run.NestedDispatchCount > 0)
    {
      if (sb.Length > 0)
      {
        _ = sb.Append('\n');
      }

      _ = sb.Append(CultureInfo.InvariantCulture,
          $"[exec: empty output — {run.NestedDispatchCount} nested tool call(s) ran; if one was a bare statement, its result was discarded. Capture it: var r = Tools.Invoke(...); then use or return r.]");
    }

    AppendChangeAnnotation(sb, run);
    AppendImageAnnotation(sb, run);
    AppendWorkspaceAnnotation(sb, run, program);

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

    return new ToolResult(sb.ToString(), run.ErrorLines.Count > 0, title, AttachedImages(run));
  }

  /// <summary>The images a nested dispatch attached (issue #288): exec is the ONLY
  ///     top-level tool, so the run's collected images ride THIS result as image parts
  ///     and the text says how many arrived. Without the line the model sees an
  ///     annotation it never asked for and cannot tell whether the picture came
  ///     through; without the parts the image surface would silently disappear along
  ///     with the other six doors. A dispatch past the per-result bound is DROPPED and
  ///     NAMED — a silent loss would be the same lie as never carrying them.</summary>
  private static void AppendImageAnnotation(StringBuilder sb, ExecRunResult run)
  {
    if (run.Images.Count == 0 && run.DroppedImageCount == 0)
    {
      return;
    }

    if (sb.Length > 0)
    {
      _ = sb.Append('\n');
    }

    if (run.Images.Count > 0)
    {
      string types = string.Join(", ", run.Images.Select(i => i.MediaType).Distinct(StringComparer.Ordinal));
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"[exec: {run.Images.Count} image(s) attached to this result as image parts — {types}]");
    }

    if (run.DroppedImageCount > 0)
    {
      if (run.Images.Count > 0)
      {
        _ = sb.Append('\n');
      }

      _ = sb.Append(CultureInfo.InvariantCulture,
          $"[exec: {run.DroppedImageCount} image(s) DROPPED — at most {ImageLimits.MaxPerMessage} images ride one result; split the run, or view them one at a time with Tools.read_image]");
    }
  }

  /// <summary>The images to attach: null when the run carried none, so a text-only
  ///     result keeps its legacy shape.</summary>
  private static IReadOnlyList<ToolResultImage>? AttachedImages(ExecRunResult run) =>
      run.Images.Count > 0 ? run.Images : null;

  /// <summary>The change report (issue #283): what the program actually changed in the
  ///     workspace, as a fact rather than advice. A run that changed nothing says so
  ///     explicitly - that is the case the report exists for, because a read-modify-write
  ///     whose anchor did not match writes the file back unchanged and used to report
  ///     success. Absent entirely when the host supplied no snapshot seam, so a legacy
  ///     host's output is byte-identical to before.</summary>
  private static void AppendChangeAnnotation(StringBuilder sb, ExecRunResult run)
  {
    if (run.Changes is null)
    {
      return;
    }

    if (sb.Length > 0)
    {
      _ = sb.Append('\n');
    }

    _ = sb.Append(run.Changes.Render());
  }

  /// <summary>Surfaces the workspace-vs-launch-directory distinction when the program
  ///     references <c>Directory.GetCurrentDirectory()</c>: that call returns the APP's
  ///     launch directory, not the session workspace, and a script that reads it for
  ///     workspace files silently gets the wrong directory. The annotation states both
  ///     paths so the model sees the mismatch instead of a silent wrong answer
  ///     (session 4ebb01ae retrospective). Skipped when the engine supplied no snapshot
  ///     (legacy fakes) or when the launch directory IS the workspace — there the
  ///     distinction is moot.</summary>
  private static void AppendWorkspaceAnnotation(StringBuilder sb, ExecRunResult run, ExecProgram? program)
  {
    if (program is null || run.WorkspaceRoot is null || run.LaunchDirectory is null)
    {
      return;
    }

    if (!program.Text.Contains("CurrentDirectory", StringComparison.Ordinal))
    {
      return;
    }

    bool sameDirectory = string.Equals(
        run.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        run.LaunchDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
    if (sameDirectory)
    {
      return;
    }

    if (sb.Length > 0)
    {
      _ = sb.Append('\n');
    }

    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[exec: workspace note — this program references Directory.GetCurrentDirectory(), which returns the app's launch directory ({run.LaunchDirectory}), NOT the session workspace ({run.WorkspaceRoot}). Build workspace paths from Workspace (Path.Combine(Workspace, 'notes.txt')) or use ResolvePath('notes.txt') - a relative path in a raw File.*/Directory.* call resolves HERE, not at the workspace.]");
  }

  public static ToolResult ParseErrors(IReadOnlyList<ExecParseError> errors,
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

    foreach (ExecParseError? e in errors)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\nline {e.Line}, col {e.Column}: {e.Message}");
    }

    return new ToolResult(sb.ToString(), true);
  }

  private static ToolResult ErrorRun(ExecRunResult run)
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
      _ = sb.Append(run.Output);
    }

    // A run that faulted after an image-bearing dispatch keeps what it collected: the
    // extract already happened, and hiding it would make the model redo the work.
    return new ToolResult(sb.ToString(), true, Images: AttachedImages(run));
  }
}

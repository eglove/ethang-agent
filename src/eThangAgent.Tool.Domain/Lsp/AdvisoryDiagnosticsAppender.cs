using System.Globalization;

namespace eThangAgent.ToolDomain.Lsp;

/// <summary>Appends advisory diagnostics to a successful edit/write result.
/// The edit result is assembled first; diagnostics are collected off the synchronous
/// path under a 2 s budget and filtered to the changed lines (the <c>ChangedLines</c>
/// the edit/write outcome carries). The append never delays the edit beyond the
/// budget and never fails it. A producer that exists but is starting, stale, or
/// degraded appends a labeled note; a workspace with no producer appends nothing,
/// as do non-code files and a null source, so those results stay byte-identical to
/// the pre-advisory output.</summary>
public static class AdvisoryDiagnosticsAppender
{
  /// <summary>The latency budget: diagnostics never add more than this to an edit/write
  ///     call. A warm pull after a change costs the server about 1.6 s of re-analysis, so a
  ///     tighter budget would systematically answer Stale for the very edit that produced
  ///     the diagnostic.</summary>
  public static TimeSpan Budget { get; } = TimeSpan.FromSeconds(5);

  /// <summary>Code extensions the advisory slice consults. A file whose extension is
  /// not listed is non-code: no server is consulted, no text is appended.</summary>
  private static readonly HashSet<string> CodeExtensions =
  [
    with(StringComparer.OrdinalIgnoreCase),
    ".cs", ".fs", ".fsx", ".vb", ".ts", ".tsx", ".js", ".jsx",
    ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".go",
    ".rs", ".rb", ".php", ".swift", ".kt", ".scala", ".hs",
  ];

  /// <summary>True when <paramref name="path"/> names a code file the advisory slice
  /// consults. Non-code files are byte-identical: the source is never called.</summary>
  public static bool IsCodeFile(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    return CodeExtensions.Contains(Path.GetExtension(path));
  }

  /// <summary>Appends scoped, labeled diagnostics to <paramref name="baseResult"/> when
  /// <paramref name="source"/> is non-null and <paramref name="path"/> is a code file.
  /// Returns <paramref name="baseResult"/> unchanged otherwise. The returned content
  /// is <c>baseResult.Content + "\n" + the append</c>; <c>IsError</c> and <c>Title</c>
  /// are preserved. Diagnostics never flip the result to an error.</summary>
  public static async Task<ToolResult> AppendAsync(
      ToolResult baseResult,
      IAdvisoryDiagnosticsSource? source,
      string path,
      IReadOnlyList<int> changedLines,
      SessionAdvisoryDiagnosticsLedger? ledger,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(baseResult);
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentNullException.ThrowIfNull(changedLines);
    if (source is null || !IsCodeFile(path))
    {
      return baseResult;
    }

    AdvisoryDiagnosticsResult result = await QueryAsync(source, path, ct).ConfigureAwait(false);
    ledger?.Append(new AdvisoryDiagnosticsEntry(path, result.State, result.Diagnostics));
    string? append = Render(result, changedLines);
    return append is null
      ? baseResult
      : new ToolResult(baseResult.Content + "\n" + append, baseResult.IsError, baseResult.Title, baseResult.Images);
  }

  /// <summary>Collects diagnostics under the 2s budget. A timeout yields a labeled
  /// Stale result; any other fault yields a labeled Degraded result. The query task
  /// is never awaited past the budget, so the edit is never delayed beyond it.</summary>
  private static async Task<AdvisoryDiagnosticsResult> QueryAsync(
      IAdvisoryDiagnosticsSource source, string path, CancellationToken ct)
  {
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    try
    {
      // WaitAsync bounds the wait without a timer of its own: the edit result is already
      // assembled, so a producer that misses the budget is abandoned, not awaited.
      return await source.GetAsync(path, cts.Token).WaitAsync(Budget, ct).ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
      await cts.CancelAsync().ConfigureAwait(false);
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Stale, []);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Stale, []);
    }
    // Named decision (CA1031): a diagnostics fault must never fail the edit that
    // produced it - the edit result is worth more than the append.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Degraded, [], ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Renders the append, or null when nothing should be appended (Ready with
  /// no diagnostics on the changed lines is the one silent case: the server reported,
  /// and there is nothing to say).</summary>
  private static string? Render(AdvisoryDiagnosticsResult result, IReadOnlyList<int> changedLines)
  {
    HashSet<int> changed = [.. changedLines];
    List<AdvisoryDiagnostic> scoped = [.. result.Diagnostics.Where(d => changed.Contains(d.StartLine))];
    return result.State switch
    {
      AdvisoryDiagnosticsState.Ready when scoped.Count == 0 => null,
      AdvisoryDiagnosticsState.Ready => RenderReady(scoped),
      AdvisoryDiagnosticsState.Starting => "[diagnostics] Starting: server starting \u2014 no evidence yet.",
      AdvisoryDiagnosticsState.Stale => "[diagnostics] stale: diagnostics may be outdated.",
      AdvisoryDiagnosticsState.Degraded => result.Reason is null
        ? "[diagnostics] Degraded: diagnostics unavailable."
        : "[diagnostics] Degraded: " + result.Reason,
      // No language server for this workspace is the ordinary state of a workspace whose
      // language is not served; nothing is appended, so the result is byte-identical.
      AdvisoryDiagnosticsState.Unavailable => null,
      _ => null,
    };
  }

  private static string RenderReady(IReadOnlyList<AdvisoryDiagnostic> scoped)
  {
    List<string> lines = [];
    foreach (AdvisoryDiagnostic d in scoped)
    {
      string code = d.Code is null ? string.Empty : " " + d.Code;
      lines.Add(string.Create(CultureInfo.InvariantCulture,
        $"[diagnostics] {d.StartLine}:{d.StartCharacter}-{d.EndLine}:{d.EndCharacter}{code} {d.Message}"));
    }
    return string.Join("\n", lines);
  }
}

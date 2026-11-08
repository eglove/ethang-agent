using eThangAgent.Lsp.ACL;

// JSON002 fires only in the format/IDE host; the pragma pair is the repo's named pattern.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>A diagnostics report describes the document VERSION the server analyzed, and
///     the store must not present a report for other content as evidence about this one.
///     The rule is the protocol's, not a language's: the version rides the publish only
///     because the client advertised versionSupport, and the comparison names no
///     diagnostic code, so it holds for every server that honours it.</summary>
public class LspDiagnosticsVersionTests
{
  private const string Uri = "file:///ws/a.txt";

  private static string Push(int? version, string code)
  {
    string versionField = version is null ? string.Empty : ",\"version\":" + version.Value;
    return "{\"uri\":\"" + Uri + "\"" + versionField
        + ",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"code\":\""
        + code + "\",\"message\":\"m\"}]}";
  }

  [Fact]
  public void ReportForTheCurrentVersion_IsEvidence()
  {
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(Push(2, "X1"));

    LspDiagnosticsResult result = store.GetForVersion(Uri, 2);

    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Single(result.Diagnostics);
  }

  [Fact]
  public void ReportForADifferentVersion_IsNotEvidence()
  {
    // The defect this rule exists for: a server publishes for the snapshot it analyzed,
    // often before the analysis that would make the report meaningful has run. Presenting
    // that as Ready tells the caller its own content says something it does not.
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(Push(1, "X1"));

    LspDiagnosticsResult result = store.GetForVersion(Uri, 2);

    Assert.Equal(LspDiagnosticsState.Starting, result.State);
    Assert.Empty(result.Diagnostics);
  }

  [Fact]
  public void ReportWithoutAVersion_StandsAsItAlwaysDid()
  {
    // A server that omits the version yields no comparison, so the report is neither
    // trusted nor discarded on a guess - it is treated exactly as before this rule.
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(Push(null, "X1"));

    LspDiagnosticsResult result = store.GetForVersion(Uri, 7);

    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Single(result.Diagnostics);
  }

  [Fact]
  public void UnknownCurrentVersion_StandsAsItAlwaysDid()
  {
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(Push(1, "X1"));

    LspDiagnosticsResult result = store.GetForVersion(Uri, null);

    Assert.Equal(LspDiagnosticsState.Ready, result.State);
  }

  [Fact]
  public void NoReportAtAll_IsStarting_NotClean()
  {
    LspDiagnosticStore store = new();

    LspDiagnosticsResult result = store.GetForVersion(Uri, 1);

    Assert.Equal(LspDiagnosticsState.Starting, result.State);
  }

  [Fact]
  public void ALaterReportForTheCurrentVersion_ReplacesTheStaleOne()
  {
    LspDiagnosticStore store = new();
    store.OnPublishDiagnostics(Push(1, "X1"));
    Assert.Equal(LspDiagnosticsState.Starting, store.GetForVersion(Uri, 2).State);

    store.OnPublishDiagnostics(Push(2, "X2"));

    LspDiagnosticsResult result = store.GetForVersion(Uri, 2);
    Assert.Equal(LspDiagnosticsState.Ready, result.State);
    Assert.Equal("X2", Assert.Single(result.Diagnostics).Code);
  }
}

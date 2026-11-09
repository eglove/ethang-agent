using System.Text.Json;
using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

// Issue #361: the whole path - edit -> app -> daemon pipe -> language server ->
// model-visible tool result - exercised end to end, plus the two invariants that
// keep the advisory slice advisory: verification gates behave identically with and
// without a language server, and a non-programming workspace starts neither a
// daemon nor a server.
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class DiagnosticsEndToEndE2ETests
{
  private const string TypeErrorMessage = "Cannot implicitly convert type 'string' to 'int'";

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>Records the document-sync notifications the daemon forwards, so a test
  ///     proves the daemon's whole connection loop is live rather than a request-only
  ///     stub.</summary>
  private sealed class RecordingDocumentSync : ILspDocumentSync
  {
    public List<string> Calls { get; } = [];

    public Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("open:" + uri);
      return Task.CompletedTask;
    }

    public Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
    {
      Calls.Add("change:" + uri);
      return Task.CompletedTask;
    }

    public Task DidCloseAsync(string uri, CancellationToken ct = default)
    {
      Calls.Add("close:" + uri);
      return Task.CompletedTask;
    }
  }

  /// <summary>A daemon serving one workspace in-process: a document-sync forwarder is
  ///     attached (as the production daemon always does) and the diagnostics service
  ///     answers from a store the test seeds.</summary>
  private sealed class DaemonHarness : IDisposable
  {
    private readonly CancellationTokenSource _cts = new(TestCeiling.Value);
    private readonly LspDaemonServer _server;
    private readonly Task _serve;

    public DaemonHarness(string workspaceId, string workspaceRoot, LspDiagnosticStore store)
    {
      LspDiagnosticsService diagnostics = new(null, store);
      WorkspaceId = workspaceId;
      PipeName = LspPipeNames.ForWorkspaceId(workspaceId);
      DocumentSync = new RecordingDocumentSync();
      LspDocumentSync forwarder = new(workspaceId, workspaceRoot, "csharp", [".cs"], DocumentSync);
      _server = new LspDaemonServer(PipeName, workspaceId, () => Task.CompletedTask);
      _server.AttachForwarder(forwarder);
      _server.AttachDiagnosticsService(diagnostics);
      _serve = Task.Run(async () => await _server.ServeUntilClosedAsync(_cts.Token).ConfigureAwait(false), _cts.Token);
    }

    public string WorkspaceId { get; }

    public string PipeName { get; }

    public RecordingDocumentSync DocumentSync { get; }

    /// <summary>Waits until the pipe accepts a connection, so the app under test never
    ///     races the daemon's listen backlog.</summary>
    public async Task WaitUntilListeningAsync()
    {
      for (int attempt = 0; attempt < 200; attempt++)
      {
        try
        {
          using LspPipeClient probe = await LspPipeClient.ConnectAsync(
              PipeName, WorkspaceId, "probe", _cts.Token).ConfigureAwait(true);
          return;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
          await Task.Delay(25, _cts.Token).ConfigureAwait(true);
        }
      }

      throw new InvalidOperationException("the test daemon never accepted a connection");
    }

    public void Dispose()
    {
      _cts.Cancel();
      _serve.WaitAsync(TestCeiling.Value, CancellationToken.None).GetAwaiter().GetResult();
      _server.Dispose();
      _cts.Dispose();
    }
  }

  [Fact]
  public async Task EditIntroducingTypeError_ReturnsScopedDiagnosticInToolResult()
  {
    string ws = TestTempRoot.CreateDirectory("ethang-lsp-e2e").FullName;
    string wsId = "e2e-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);

      // The server's report is 0-based on the wire; the daemon reports it 1-based and
      // the edit's changed lines are 1-based too, so line 3 is the changed line.
      LspDiagnosticStore store = new();
      string uri = LspUriMapper.ToUri(Path.Combine(ws, "a.cs"));
      store.OnPublishDiagnostics(
          "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":2,\"character\":10},\"end\":{\"line\":2,\"character\":21}},\"severity\":1,\"code\":\"CS0029\",\"message\":\"" + TypeErrorMessage + "\"}]}");
      using DaemonHarness daemon = new(wsId, ws, store);
      await daemon.WaitUntilListeningAsync().ConfigureAwait(true);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("e1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = \"one\";"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("introduce a type error").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

      string toolResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[^1]);
      Assert.Contains("[diagnostics]", toolResult, StringComparison.Ordinal);
      Assert.Contains("CS0029", toolResult, StringComparison.Ordinal);
      Assert.Contains("3:10-3:21", toolResult, StringComparison.Ordinal);
      Assert.Contains(TypeErrorMessage, toolResult, StringComparison.Ordinal);
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  /// <summary>The same path against the REAL bundled language server, opt-in by trait so
  ///     the default suite stays deterministic: it needs a built daemon executable and a
  ///     machine where the server can analyze a workspace. Run it with
  ///     --filter-trait "Category=RealLspServer".</summary>
  [Fact]
  [Trait("Category", "OptIn")]
  [Trait("Category", "RealLspServer")]
  public async Task RealBundledServer_EditIntroducingTypeError_ReturnsScopedDiagnostic()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "with a built daemon and a resolvable Roslyn server.");

    string ws = TestTempRoot.CreateDirectory("ethang-lsp-real").FullName;
    string wsId = "real-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(ws, "a.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n", Ct).ConfigureAwait(true);
      // A solution file, not just a project: the server's solution/open request rejects a
      // project path, and only a loaded compilation produces COMPILER diagnostics.
      await File.WriteAllTextAsync(Path.Combine(ws, "a.slnx"),
          "<Solution>\n  <Project Path=\"a.csproj\" />\n</Solution>\n", Ct).ConfigureAwait(true);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      // The FIRST edit starts the daemon, which must load the solution before it can report
      // anything, so that edit honestly carries a Starting note rather than a false clean.
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("r1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = \"one\";"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("introduce a type error").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

      string firstResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[^1]);
      Assert.Contains("[diagnostics]", firstResult, StringComparison.Ordinal);

      // The type error stays in the file, so a later edit is answered from the compilation
      // the first turn caused to exist. The load runs dotnet restore and a build host, which
      // outlasts one edit, so the file is touched again until the server has a compilation -
      // a Starting note means it does not yet, which is the honest answer at that moment.
      string toolResult = string.Empty;
      string current = "one";
      for (int attempt = 0; attempt < 10; attempt++)
      {
        string next = string.Equals(current, "one", StringComparison.Ordinal) ? "two" : "one";
        _ = host.Mock.ReturnsForModel(E2E.SessionModel,
            E2E.ExecToolCall("r2", E2E.ExecProgram(EditProgram(
                Path.Combine(ws, "a.cs"),
                "int x = \"" + current + "\";",
                "int x = \"" + next + "\";"))),
            E2E.RawCompletion("edited again"));
        await host.Vm.RunTurnAsync("touch the file again").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);
        current = next;
        toolResult = AllToolOutputs(host.Mock.RequestBodies[^1])[^1];
        if (toolResult.Contains("CS0029", StringComparison.Ordinal))
        {
          break;
        }

        await Task.Delay(2000, Ct).ConfigureAwait(true);
      }

      Assert.Contains("[diagnostics]", toolResult, StringComparison.Ordinal);
      Assert.Contains("CS0029", toolResult, StringComparison.Ordinal);
      Assert.Contains("3:", toolResult, StringComparison.Ordinal);
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  [Fact]
  public async Task GateParity_SameSequenceWithAndWithoutDaemon_ProducesIdenticalOutcomes()
  {
    (int withoutGate, List<string> withoutOutputs) = await RunParitySequenceAsync(withDaemon: false).ConfigureAwait(true);
    (int withGate, List<string> withOutputs) = await RunParitySequenceAsync(withDaemon: true).ConfigureAwait(true);

    Assert.Equal(withoutGate, withGate);
    Assert.Equal(withoutOutputs.Count, withOutputs.Count);
    for (int i = 0; i < withoutOutputs.Count; i++)
    {
      Assert.Equal(WithoutDiagnostics(withoutOutputs[i]), WithoutDiagnostics(withOutputs[i]));
    }

    // The daemon run must actually have produced diagnostics; otherwise the parity above
    // is two identical LSP-absent runs and proves nothing.
    Assert.Contains(withOutputs, o => o.Contains("[diagnostics]", StringComparison.Ordinal));
    Assert.DoesNotContain(withoutOutputs, o => o.Contains("[diagnostics]", StringComparison.Ordinal));
  }

  private static async Task<(int GateMessages, List<string> ToolOutputs)> RunParitySequenceAsync(bool withDaemon)
  {
    string ws = TestTempRoot.CreateDirectory("ethang-lsp-parity").FullName;
    string wsId = "parity-" + Guid.NewGuid().ToString("N");
    using DaemonHarness? daemon = withDaemon ? new DaemonHarness(wsId, ws, SeededStore(ws)) : null;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);
      if (daemon is not null)
      {
        await daemon.WaitUntilListeningAsync().ConfigureAwait(true);
      }

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(
          workspaceRoot: ws,
          workspaceId: wsId,
          lspDaemonExePath: withDaemon ? null : Path.Combine(ws, "absent", "eThangAgent.Lsp.Host.exe")).ConfigureAwait(true);

      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("p1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = \"one\";"))),
          E2E.ToolCall("p2", "exec", E2E.ExecProgram("File.WriteAllText(Path.Combine(Workspace, \"note.txt\"), \"v1\");")),
          E2E.RawCompletion("edited"),
          E2E.ToolCall("p3", "exec", E2E.ExecProgram("return \"ok\";")),
          E2E.RawCompletion("done"));
      await host.Vm.RunTurnAsync("edit and run").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);
      await host.Vm.RunTurnAsync("verify").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

      List<string> messages = await ReadAllMessagesAsync(host.DatabasePath).ConfigureAwait(true);
      int gateMessages = messages.Count(m => m.Contains("[verification gate]", StringComparison.Ordinal));
      List<string> toolOutputs = [.. AllToolOutputs(host.Mock.RequestBodies[^1]).Select(o => Normalize(o, ws))];
      return (gateMessages, toolOutputs);
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  /// <summary>A code workspace with a resolvable daemon executable starts the daemon
  ///     process on the first edit, proving the production start path (not an in-process
  ///     harness) is what an edit reaches.</summary>
  [Fact]
  public async Task CodeWorkspace_EditStartsTheDaemonProcess()
  {
    string ws = TestTempRoot.CreateDirectory("ethang-lsp-start").FullName;
    string wsId = "start-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(ws, "a.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n", Ct).ConfigureAwait(true);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("s1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = 2;"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("edit the file").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

      // ATTRIBUTED BY WORKSPACE, never by a global process count: a daemon another test
      // module started for a workspace of its own is not this workspace's daemon, and counting
      // every process by image name made this assertion depend on what else the machine ran.
      // Joining with a start delegate that refuses proves a listener already existed.
      await AssertDaemonListeningAsync(wsId, ws).ConfigureAwait(true);
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  [Fact]
  public async Task NonProgrammingWorkspace_EditProducesNoLspOutputAndStartsNoDaemon()
  {
    string ws = TestTempRoot.CreateDirectory("ethang-lsp-vault").FullName;
    string wsId = "vault-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "notes.md"), "# notes\n\nold line\n", Ct).ConfigureAwait(true);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("n1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "notes.md"), "old line", "new line"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("edit the note").WaitAsync(TestCeiling.Value, Ct).ConfigureAwait(true);

      string toolResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[^1]);
      Assert.DoesNotContain("[diagnostics]", toolResult, StringComparison.Ordinal);
      // No daemon is listening for THIS workspace: a start delegate that refuses is never
      // reached, so a null answer means nothing was there to join.
      await AssertNoDaemonListeningAsync(wsId, ws).ConfigureAwait(true);
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  /// <summary>Asserts a daemon is already listening for the workspace by JOINING it. The
  ///     start delegate refuses, so a connection can only come from a daemon that was already
  ///     running - which is what an edit starting one has to leave behind.</summary>
  private static async Task AssertDaemonListeningAsync(string workspaceId, string workspaceRoot)
  {
    LspDaemonConnection? joined = await LspDaemonConnection.ConnectAsync(
        workspaceId, workspaceRoot, "e2e-probe",
        () => throw new InvalidOperationException("no daemon was started for this workspace"),
        Ct).ConfigureAwait(true);
    try
    {
      Assert.NotNull(joined);
    }
    finally
    {
      if (joined is not null)
      {
        await joined.DisposeAsync().ConfigureAwait(true);
      }
    }
  }

  /// <summary>Asserts NO daemon is listening for the workspace: the same join, with the same
  ///     refusing start delegate, has nothing to reach.</summary>
  private static async Task AssertNoDaemonListeningAsync(string workspaceId, string workspaceRoot)
  {
    LspDaemonConnection? joined = await LspDaemonConnection.ConnectAsync(
        workspaceId, workspaceRoot, "e2e-probe",
        () => throw new InvalidOperationException("no daemon was started for this workspace"),
        Ct).ConfigureAwait(true);
    try
    {
      Assert.Null(joined);
    }
    finally
    {
      if (joined is not null)
      {
        await joined.DisposeAsync().ConfigureAwait(true);
      }
    }
  }

  /// <summary>Removes a temp workspace, tolerating the brief window in which a watcher or a
  ///     just-killed process still holds a handle in it.</summary>
  private static void DeleteWorkspace(string workspaceRoot)
  {
    // Best effort, and deliberately ONE attempt: a watcher or a just-killed process can still
    // hold a handle here, and debris under the run root is swept by TestTempRoot.Delete at the
    // end of the run. Retrying against a clock would spend the whole ceiling on a delete that
    // is not the thing under test.
    try
    {
      Directory.Delete(workspaceRoot, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
      // Left for the run-root sweep.
    }
  }

  /// <summary>A diagnostics store already holding the type error the edit will introduce,
  ///     so the daemon answers a Ready result instead of Starting.</summary>
  private static LspDiagnosticStore SeededStore(string workspaceRoot)
  {
    LspDiagnosticStore store = new();
    string uri = LspUriMapper.ToUri(Path.Combine(workspaceRoot, "a.cs"));
    store.OnPublishDiagnostics(
        "{\"uri\":\"" + uri + "\",\"diagnostics\":[{\"range\":{\"start\":{\"line\":2,\"character\":10},\"end\":{\"line\":2,\"character\":21}},\"severity\":1,\"code\":\"CS0029\",\"message\":\"" + TypeErrorMessage + "\"}]}");
    return store;
  }

  /// <summary>An exec program that performs ONE edit through the nested tool surface and
  ///     returns the tool's own result, so the model-visible output is exactly what the
  ///     edit tool produced (issue #288: exec is the only top-level tool).</summary>
  private static string EditProgram(string path, string oldText, string replacement)
  {
    string args = $"new {{ path = {Literal(path)}, old = {Literal(oldText)}, "
        + $"replacement = {Literal(replacement)}, occurrences = 1 }}";
    return "return Tools.edit(" + args + ");";
  }

  /// <summary>A C# string literal for one value, so an edit program is generated without
  ///     hand-escaping the text the tool must match verbatim.</summary>
  private static string Literal(string value) => JsonSerializer.Serialize(value);

  /// <summary>Every tool result in one request body, in order.</summary>
  private static List<string> AllToolOutputs(string body)
  {
    List<string> outputs = [];
    using JsonDocument doc = JsonDocument.Parse(body);
    foreach (JsonElement item in doc.RootElement.GetProperty("input").EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Object
          || !item.TryGetProperty("type", out JsonElement type)
          || type.GetString() != "function_call_output"
          || !item.TryGetProperty("output", out JsonElement output))
      {
        continue;
      }

      outputs.Add(output.ValueKind == JsonValueKind.String
          ? output.GetString() ?? string.Empty
          : string.Join("\n", output.EnumerateArray()
              .Where(p => p.TryGetProperty("type", out JsonElement pt) && pt.GetString() == "input_text")
              .Select(p => p.GetProperty("text").GetString() ?? string.Empty)));
    }

    return outputs;
  }

  /// <summary>One tool result with the workspace root normalized. Advisory diagnostics
  ///     are NOT stripped: the parity claim is that the non-diagnostics text is identical,
  ///     and that is asserted separately, so a diagnostics difference cannot hide here.</summary>
  private static string Normalize(string text, string workspaceRoot)
    => text
        .Replace(workspaceRoot, "<ws>", StringComparison.OrdinalIgnoreCase)
        .Replace(Directory.GetCurrentDirectory(), "<cwd>", StringComparison.OrdinalIgnoreCase)
        .Replace(AppContext.BaseDirectory, "<base>", StringComparison.OrdinalIgnoreCase)
        .Replace(Path.GetFileName(workspaceRoot), "<wsname>", StringComparison.OrdinalIgnoreCase);

  /// <summary>The non-diagnostics text of one tool result: the part the parity claim covers.</summary>
  private static string WithoutDiagnostics(string text)
    => string.Join("\n", text.Split('\n').Where(line => !line.StartsWith("[diagnostics]", StringComparison.Ordinal)));

  private static async Task<List<string>> ReadAllMessagesAsync(string dbPath)
  {
    List<string> contents = [];
    using Microsoft.Data.Sqlite.SqliteConnection conn = new("Data Source=" + dbPath);
    await conn.OpenAsync(Ct).ConfigureAwait(true);
    using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT content FROM agent_messages";
    using Microsoft.Data.Sqlite.SqliteDataReader reader = await cmd.ExecuteReaderAsync(Ct).ConfigureAwait(true);
    while (await reader.ReadAsync(Ct).ConfigureAwait(true))
    {
      contents.Add(reader.GetString(0));
    }

    return contents;
  }
}

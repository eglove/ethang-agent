using System.Diagnostics;
using System.Text.Json;
using eThangAgent.Lsp.ACL;

namespace eThangAgent.Desktop.Tests;

// Issue #361: the whole path - edit -> app -> daemon pipe -> language server ->
// model-visible tool result - exercised end to end, plus the two invariants that
// keep the advisory slice advisory: verification gates behave identically with and
// without a language server, and a non-programming workspace starts neither a
// daemon nor a server.
[Collection("Desktop E2E")]
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
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));
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
      _serve.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
      _server.Dispose();
      _cts.Dispose();
    }
  }

  [Fact]
  public async Task EditIntroducingTypeError_ReturnsScopedDiagnosticInToolResult()
  {
    string ws = Directory.CreateTempSubdirectory("ethang-lsp-e2e").FullName;
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

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("e1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = \"one\";"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("introduce a type error").WaitAsync(TimeSpan.FromSeconds(90), Ct).ConfigureAwait(true);

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
  [Trait("Category", "RealLspServer")]
  public async Task RealBundledServer_EditIntroducingTypeError_ReturnsScopedDiagnostic()
  {
    if (Environment.GetEnvironmentVariable("ETHANG_LSP_REAL_SERVER") is not "1")
    {
      Assert.Skip("Opt-in: set ETHANG_LSP_REAL_SERVER=1 with a built daemon and a resolvable OmniSharp.");
    }

    string ws = Directory.CreateTempSubdirectory("ethang-lsp-real").FullName;
    string wsId = "real-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(ws, "a.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n", Ct).ConfigureAwait(true);

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("r1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = \"one\";"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("introduce a type error").WaitAsync(TimeSpan.FromSeconds(120), Ct).ConfigureAwait(true);

      string toolResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[^1]);
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
    string ws = Directory.CreateTempSubdirectory("ethang-lsp-parity").FullName;
    string wsId = "parity-" + Guid.NewGuid().ToString("N");
    using DaemonHarness? daemon = withDaemon ? new DaemonHarness(wsId, ws, SeededStore(ws)) : null;
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);
      if (daemon is not null)
      {
        await daemon.WaitUntilListeningAsync().ConfigureAwait(true);
      }

      using E2E.HostHarness host = new();
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
      await host.Vm.RunTurnAsync("edit and run").WaitAsync(TimeSpan.FromSeconds(90), Ct).ConfigureAwait(true);
      await host.Vm.RunTurnAsync("verify").WaitAsync(TimeSpan.FromSeconds(90), Ct).ConfigureAwait(true);

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
    string ws = Directory.CreateTempSubdirectory("ethang-lsp-start").FullName;
    string wsId = "start-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "a.cs"), "class A\n{\n  int x = 1;\n}\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(ws, "a.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n", Ct).ConfigureAwait(true);
      int before = Process.GetProcessesByName("eThangAgent.Lsp.Host").Length;

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("s1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "a.cs"), "int x = 1;", "int x = 2;"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("edit the file").WaitAsync(TimeSpan.FromSeconds(120), Ct).ConfigureAwait(true);

      int after = Process.GetProcessesByName("eThangAgent.Lsp.Host").Length;
      Assert.True(after > before, "the edit did not start the workspace's language-server daemon");
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  [Fact]
  public async Task NonProgrammingWorkspace_EditProducesNoLspOutputAndStartsNoDaemon()
  {
    string ws = Directory.CreateTempSubdirectory("ethang-lsp-vault").FullName;
    string wsId = "vault-" + Guid.NewGuid().ToString("N");
    try
    {
      await File.WriteAllTextAsync(Path.Combine(ws, "notes.md"), "# notes\n\nold line\n", Ct).ConfigureAwait(true);
      int daemonsBefore = Process.GetProcessesByName("eThangAgent.Lsp.Host").Length;

      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws, workspaceId: wsId).ConfigureAwait(true);
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("n1", E2E.ExecProgram(EditProgram(Path.Combine(ws, "notes.md"), "old line", "new line"))),
          E2E.RawCompletion("edited"));
      await host.Vm.RunTurnAsync("edit the note").WaitAsync(TimeSpan.FromSeconds(90), Ct).ConfigureAwait(true);

      string toolResult = E2E.GetLastToolMessage(host.Mock.RequestBodies[^1]);
      Assert.DoesNotContain("[diagnostics]", toolResult, StringComparison.Ordinal);
      Assert.Equal(daemonsBefore, Process.GetProcessesByName("eThangAgent.Lsp.Host").Length);
    }
    finally
    {
      DeleteWorkspace(ws);
    }
  }

  /// <summary>Removes a temp workspace, tolerating the brief window in which a watcher or a
  ///     just-killed process still holds a handle in it.</summary>
  private static void DeleteWorkspace(string workspaceRoot)
  {
    for (int attempt = 0; attempt < 20; attempt++)
    {
      try
      {
        Directory.Delete(workspaceRoot, recursive: true);
        return;
      }
      catch (IOException)
      {
        Thread.Sleep(50);
      }
      catch (UnauthorizedAccessException)
      {
        Thread.Sleep(50);
      }
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

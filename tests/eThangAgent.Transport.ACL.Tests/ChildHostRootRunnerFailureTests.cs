using System.Globalization;
using eThangAgent.ChildHost;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The host's refusal must name the REAL cause. The previous build swallowed
///     the exception that broke the host's session container and answered every turn
///     with "this conversation host is not bound to a root session" — a statement about
///     the BINDING that was never the problem. The actual failure was a database the
///     host could not open (locked), and the reader was left hunting a wiring fault
///     that did not exist (2026-10-22). These tests pin the two distinct causes: no
///     session id, and a container that cannot be built.</summary>
public class ChildHostRootRunnerFailureTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

  [Fact]
  public async Task NoSessionId_SettlesWithTheBindingReason()
  {
    // The settings name no root session: the host must not guess which conversation a
    // turn belongs to, and the refusal must say THAT is the reason.
    (Result<string> result, List<RootTurnEvent> events) = await RunRootTurnAsync(
        /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":\"sk-or-test\",\"BaseUrl\":\"http://openrouter.test\"},\"SubAgents\":{}}");

    Assert.True(result.IsSuccess, result.Error?.Message);
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("RootTurnUnavailable", settle.Payload, StringComparison.Ordinal);
    Assert.Contains("not bound to a root session", settle.Payload, StringComparison.Ordinal);
    Assert.True(settle.Payload.Contains("the host settings named no root session id", StringComparison.Ordinal),
        "PAYLOAD: " + settle.Payload);
  }

  [Fact]
  public async Task ContainerThatCannotBeBuilt_SettlesWithTheBuildReason()
  {
    // A relative workspace root is a named startup error in the host's container
    // build. The turn must carry THAT reason, not a claim about the binding.
    (Result<string> result, List<RootTurnEvent> events) = await RunRootTurnAsync(
        /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":\"sk-or-test\",\"BaseUrl\":\"http://openrouter.test\"},\"SubAgents\":{},\"WorkspaceRoot\":\"relative/ws\"}");

    Assert.True(result.IsSuccess, result.Error?.Message);
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("RootTurnUnavailable", settle.Payload, StringComparison.Ordinal);
    Assert.Contains("WorkspaceRoot must be an absolute path", settle.Payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ContainerBuild_While_The_App_Holds_A_Write_Lock_Still_Builds()
  {
    // THE pin for the 2026-10-22 incident: the host opened the app's database with a
    // MIGRATION pass, and a migration WRITES (it applies the pending steps) - so it
    // took a write lock on a file the app may be writing. On a busy database that
    // write timed out with "database is locked", the host's container build failed,
    // and the failure surfaced as EVERY root turn being refused with "this
    // conversation host is not bound to a root session" - a claim about the binding
    // that was never the problem.
    // The host is a GUEST in the app's database: the app owns the schema, so the
    // guest opens the file as it finds it and never waits on the app's lock.
    string dbPath = Path.Combine(Path.GetTempPath(), "ethang-lock-" + Guid.NewGuid().ToString("N") + ".db");
    _ = new AppDatabase(dbPath); // the owner creates it and migrates it
    await StampVersionAsync(dbPath, 3, TestContext.Current.CancellationToken).ConfigureAwait(true);
    string settings = Path.Combine(Path.GetTempPath(), "ethang-lock-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings,
        /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":\"sk-or-test\",\"BaseUrl\":\"http://openrouter.test\"},\"SubAgents\":{},\"SessionId\":\"" + Guid.NewGuid().ToString() + "\"}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings, dbPath);

    using SqliteConnection owner = new(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
    await owner.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using System.Data.Common.DbTransaction held = await owner.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using (SqliteCommand write = owner.CreateCommand())
    {
      write.Transaction = (SqliteTransaction)held;
      write.CommandText = "INSERT INTO app_preferences (key, value, updated_at) VALUES ('k', 'v', 'now');";
      _ = await write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    RootRunHost? runner = await server.ResolveRootHostForTest();
    elapsed.Stop();

    Assert.NotNull(runner);
    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
        "the host's container build must not wait on the app's write lock (took " + elapsed.Elapsed + ").");
    // The guest did not migrate the app's schema either: the owner owns it.
    Assert.Equal(3, await VersionAsync(dbPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  /// <summary>Stamps a schema version onto a database file (the shape a database left
  ///     behind by an older build carries).</summary>
  private static async Task StampVersionAsync(string path, int version, CancellationToken ct)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
    await connection.OpenAsync(ct).ConfigureAwait(false);
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the value is a constant test-only integer, never input.
#pragma warning disable CA2100
    command.CommandText = $"PRAGMA user_version = {version};";
#pragma warning restore CA2100
    _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>The schema version a database file carries.</summary>
  private static async Task<int> VersionAsync(string path, CancellationToken ct)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
    await connection.OpenAsync(ct).ConfigureAwait(false);
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
  }

  /// <summary>Drives one root turn against the REAL ChildHostServer over a real pipe
  ///     and returns the settle plus the events it produced.</summary>
  private static async Task<(Result<string> Result, List<RootTurnEvent> Events)> RunRootTurnAsync(
      string settingsJson)
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-rootfailure-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, settingsJson, TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings,
        Path.Combine(Path.GetTempPath(), "ethang-rootfailure-" + Guid.NewGuid().ToString("N") + ".db"));
    string pipeName = "ethang-rootfailure-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await using NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
#pragma warning restore CA2007
    server.AttachTransport(hostSide);
    // The serve loop runs until the pipe closes (its own contract) — never awaited here.
    _ = server.ServeAsync();

    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-failure", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (result, events);
  }
}

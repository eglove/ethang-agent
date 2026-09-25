
namespace eThangAgent.Desktop.Tests;

/// <summary>End-to-end proof of the deterministic verification gates (spec #24):
///     a turn that changes files without fresh verification carries the
///     '[verification gate]' System nudge in the persisted transcript.</summary>
[Collection("Desktop E2E")]
public class VerificationGateE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  [Fact]
  public async Task TurnGate_NudgeAppears_AfterUnverifiedEditTurn()
  {
    string ws = Directory.CreateTempSubdirectory("ethang-verify-gate").FullName;
    try
    {
      // The gate needs a git repo: a non-repo workspace stands down by design.
      System.Diagnostics.ProcessStartInfo psi = new("git", "init " + ws)
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      };
      using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi)!;
      await proc.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      using E2E.HostHarness host = new();
      _ = await host.StartAsync(workspaceRoot: ws);

      // Turn 1: the model writes a file through exec - a real changed file.
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("vg1", E2E.ExecProgram(
              "File.WriteAllText(Path.Combine(Workspace, \"note.txt\"), \"v1\");")),
          RawCompletion("wrote the file"));
      await host.Vm.RunTurnAsync("edit a file").WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

      // The nudge persists as a System message in the transcript.
      List<string> messages = await ReadAllMessagesAsync(host.DatabasePath).ConfigureAwait(true);
      Assert.Contains(messages, m => m.Contains("[verification gate]", StringComparison.Ordinal));
    }
    finally
    {
      Directory.Delete(ws, recursive: true);
    }
  }

  private static async Task<List<string>> ReadAllMessagesAsync(string dbPath)
  {
    List<string> contents = [];
    using Microsoft.Data.Sqlite.SqliteConnection conn = new("Data Source=" + dbPath);
    await conn.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT content FROM agent_messages";
    using Microsoft.Data.Sqlite.SqliteDataReader reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
    {
      contents.Add(reader.GetString(0));
    }

    return contents;
  }
}

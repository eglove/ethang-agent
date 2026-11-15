using eThangAgent.Desktop.ViewModels;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>An oversize tool result is REFUSED to the model instead of killing the turn.
///     <para>THE DEFECT THIS PINS: a real 48 MB file read produced a tool result that could
///     not cross the app-to-host seam, so the turn died with a frame-size fault and the
///     model's context was already wrecked by content it could not use. The refusal below
///     arrives as an ordinary tool error, which the model can act on.</para>
///     <para>THE FILE IS CREATED BY THE TEST AND DELETED BY IT: a committed 48 MB fixture is
///     not acceptable, and a fake tool would not prove the REAL read path produces the
///     refusal.</para></summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class ToolResultRefusalE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  /// <summary>A read of a file far over the model-facing budget reaches the model as
  ///     <c>Error [ToolResultTooLarge]:</c>, and the turn continues to an answer.</summary>
  [Fact]
  public async Task AnOversizeFileRead_IsRefusedToTheModel_AndTheTurnContinues()
  {
    DirectoryInfo ws = TestTempRoot.CreateDirectory("ethang-e2e-oversize");
    FileInfo big = new(Path.Combine(ws.FullName, "huge.txt"));
    try
    {
      // 48 MB of ASCII: the observed case, and far over the 2 MiB model budget.
      byte[] chunk = new byte[1024 * 1024];
      Array.Fill(chunk, (byte)'x');
      await using (FileStream stream = big.Create())
      {
        for (int i = 0; i < 48; i++)
        {
          await stream.WriteAsync(chunk, TestContext.Current.CancellationToken)
              .ConfigureAwait(true);
        }
      }

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = new();
#pragma warning restore CA2007
      host.ProviderRetry = new RetryPolicy(maxAttempts: 1);
      host.ClaimHostsForCurrentTest();
      _ = await host.StartAsync(workspaceRoot: ws.FullName).ConfigureAwait(true);

      // The model reads the whole file through the REAL read path, then answers once it
      // reads the refusal. The exec program is the tool this harness registers; what matters
      // is that the content it returns is the 48 MB file's own bytes.
      string program = "return File.ReadAllText(Path.Combine(Workspace, \"huge.txt\"));";
      _ = host.Mock.ReturnsForModel(E2E.SessionModel,
          E2E.ExecToolCall("call-1", E2E.ExecProgram(program)),
          RawCompletion("that file is too big to read whole"));

      await host.Vm.RunTurnAsync("read huge.txt").WaitAsync(TestCeiling.Value,
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // The refusal is what the tool card shows, and no seam fault appears anywhere.
      Assert.Contains(host.Vm.Transcript.Entries.OfType<ToolResultEntry>(),
          entry => entry.FullContent.Contains("ToolResultTooLarge", StringComparison.Ordinal));
      Assert.DoesNotContain(host.Vm.Transcript.Notices,
          notice => notice.Contains("exceeds the 4194304-byte seam limit", StringComparison.Ordinal));

      // The turn continued: the model answered after the refusal.
      Assert.Contains(host.Vm.Transcript.Entries.OfType<AssistantTextEntry>(),
          entry => entry.Text.Contains("too big to read whole", StringComparison.Ordinal));
    }
    finally
    {
      // The test owns the file: a leak would accumulate 48 MB per run.
      if (big.Exists)
      {
        big.Delete();
      }

      Assert.False(big.Exists, "the oversize fixture must not be left behind");
      ws.Delete(true);
    }
  }
}

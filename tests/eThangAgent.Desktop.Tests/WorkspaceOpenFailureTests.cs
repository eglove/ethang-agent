using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Opening a workspace must never take the app down. The 2026-10-21 crash: the
///     host lookup dereferenced a null repo root, the exception escaped the open path,
///     and the Avalonia dispatcher — which has no handler for it — killed the process.
///     Every fault on this path becomes a NAMED error the shell surfaces in a dialog.</summary>
public sealed class WorkspaceOpenFailureTests
{
  [Fact]
  public async Task OpenAgent_With_A_Faulting_Session_Factory_Does_Not_Throw()
  {
    MainViewModel shell = new((_, _) => throw new InvalidOperationException("the host lookup exploded"));

    Exception? failure = await Record.ExceptionAsync(() =>
        shell.OpenAgentAsync(@"C:\work\demo", "openrouter")).ConfigureAwait(true);

    Assert.Null(failure);
  }

  [Fact]
  public async Task OpenAgent_With_A_Faulting_Session_Factory_Reports_A_Named_Code()
  {
    MainViewModel shell = new((_, _) => throw new InvalidOperationException("the host lookup exploded"));

    Result<AgentTabViewModel> result = await shell
        .OpenAgentAsync(@"C:\work\demo", "openrouter").ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("SessionOpenFailed", result.Error.Code);
    // The cause rides along: the operator needs to know WHAT failed.
    Assert.Contains("the host lookup exploded", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void TheApp_Output_Carries_The_Conversation_Host()
  {
    // A published app has no repo to probe, so the host must ship NEXT TO the app -
    // the same rule the computer-use broker follows.
    string host = Path.Combine(AppContext.BaseDirectory, "eThangAgent.ChildHost.exe");

    Assert.True(File.Exists(host),
        "the conversation host must ship next to the app: a published app has no repo to resolve it from.");
  }
}

using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class NewAgentViewModelTests
{
  private static readonly IReadOnlyList<ProviderOption> Options =
      [new("openrouter", "OpenRouter"), new("zai", "z.ai")];

  [Fact]
  public void PreferredProvider_IsPreSelected()
  {
    NewAgentViewModel vm = new(Options, "zai");

    Assert.Equal("zai", vm.SelectedProvider!.Id);
    Assert.False(vm.CanOpen); // no workspace chosen yet
  }

  [Fact]
  public void UnknownPreferredProvider_FallsBackToFirst()
      => Assert.Equal("openrouter", new NewAgentViewModel(Options, "anthropic").SelectedProvider!.Id);

  [Fact]
  public void CanOpen_RequiresBothProviderAndWorkspace()
  {
    NewAgentViewModel vm = new(Options, "openrouter");
    Assert.False(vm.CanOpen);

    vm.SetWorkspaceRoot(@"C:\work\demo");

    Assert.True(vm.CanOpen);
  }

  [Fact]
  public void SetWorkspaceRoot_IgnoresCancelledPick()
  {
    NewAgentViewModel vm = new(Options, "openrouter");
    vm.SetWorkspaceRoot(@"C:\work\demo");

    vm.SetWorkspaceRoot(" ");

    Assert.Equal(@"C:\work\demo", vm.WorkspaceRoot);
  }

  [Fact]
  public void ChooseWorkspaceCommand_RaisesWorkspaceRequested()
  {
    NewAgentViewModel vm = new(Options, "openrouter");
    bool raised = false;
    vm.WorkspaceRequested += (_, _) => raised = true;

    vm.ChooseWorkspaceCommand.Execute(null);

    Assert.True(raised);
  }

  [Fact]
  public void OpenCommand_RaisesChoice_WithSelectedPair()
  {
    NewAgentViewModel vm = new(Options, "zai");
    NewAgentChoice? captured = null;
    vm.OpenRequested += (_, choice) => captured = choice;
    vm.SetWorkspaceRoot(@"C:\work\demo");

    vm.OpenCommand.Execute(null);

    Assert.NotNull(captured);
    Assert.Equal("zai", captured!.ProviderId);
    Assert.Equal(@"C:\work\demo", captured.WorkspaceRoot);
  }

  [Fact]
  public void EmptyProviderList_IsRejected()
      => _ = Assert.Throws<ArgumentException>(() => new NewAgentViewModel([], "openrouter"));
}

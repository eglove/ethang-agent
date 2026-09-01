using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Headless tests of the REAL Links window and its shell menu entry: the
///     load-populated lists, the link-then-revoke click flow through the actual
///     controls, and the rail button (tab-gated like Model/Effort). These exist
///     because a view-layer desync is invisible to pure view-model tests.</summary>
public class LinksWindowTests
{
  private static async Task<(LinksWindow Window, LinksViewModel Vm)> ShowWindow()
  {
    LinksWindow window = new(
        static ct => Task.FromResult(Result.Success<IReadOnlyList<AgentDomain.AgentRecord>>([AgentRecord()])),
        new AgentDomain.AgentLinkRegistry());
    window.Show();
    LinksViewModel vm = (LinksViewModel)window.DataContext!;
    ListBox candidates = window.GetControl<ListBox>("CandidateList");
    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (vm.IsLoading && DateTimeOffset.UtcNow < deadline)
    {
      await Task.Delay(10).ConfigureAwait(true);
      Dispatcher.UIThread.RunJobs();
    }

    if (vm.IsLoading)
    {
      throw new InvalidOperationException("links candidates did not settle within 10s");
    }

    Assert.Null(vm.LoadError);
    Assert.Equal(1, candidates.ItemCount);
    return (window, vm);
  }

  private static AgentDomain.AgentRecord AgentRecord() => AgentDomain.AgentRecord.Spawned(
      AgentDomain.AgentId.NewId(), null, 0, "test/model", "worker", "prompt",
      new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

  [AvaloniaFact]
  public async Task Link_Through_The_Real_Controls_Creates_A_Consented_Link()
  {
    (LinksWindow window, LinksViewModel vm) = await ShowWindow().ConfigureAwait(true);
    ListBox candidates = window.GetControl<ListBox>("CandidateList");
    ListBox links = window.GetControl<ListBox>("LinkList");
    TextBox name = window.GetControl<TextBox>("LinkNameBox");

    candidates.SelectedItem = vm.Candidates[0];
    _ = name.Focus();
    window.KeyTextInput("peer");
    Dispatcher.UIThread.RunJobs();
    window.GetControl<Button>("LinkButton").Command!.Execute(null);

    Assert.Null(vm.LinkError);
    LinkRow row = Assert.Single(vm.Links);
    Assert.Equal("peer", row.Name);
    Assert.Equal(1, links.ItemCount);

    // And back off: select + revoke removes it from the same registry.
    links.SelectedItem = vm.Links[0];
    window.GetControl<Button>("RevokeButton").Command!.Execute(null);
    Assert.Empty(vm.Links);
  }

  [AvaloniaFact]
  public void Shell_Rail_Carries_A_Tab_Gated_Links_Entry()
  {
    MainWindow shell = new(new MainViewModel((_, _) => Task.FromResult(Result.Failure<Composition.AgentSession>(
        new DomainError("NoFactory", "unused")))));
    shell.Show();

    Button links = shell.GetControl<Button>("LinksMenuItem");
    Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(links) as string));
    Assert.False(links.IsVisible); // no tab selected: hidden like Model/Effort
    Assert.False(links.Command!.CanExecute(null));
  }

  [Fact]
  public void MainWindow_Subscribes_LinksRequested_And_Routes_Through_The_Shell_Surface()
  {
    string codeBehind = File.ReadAllText(Path.Combine(RepoRoot(), "src", "eThangAgent.Desktop", "Views", "MainWindow.axaml.cs"));
    Assert.Contains("LinksRequested", codeBehind, StringComparison.Ordinal);
    Assert.Contains("ShowLinksDialogAsync", codeBehind, StringComparison.Ordinal);
    // The dialog must be built from the shell surface (selected tab's registry + store),
    // not from anything the window owns — the resolve"invoke chain the handoff names.
    Assert.Contains("LinksCatalogLoader", codeBehind, StringComparison.Ordinal);
    Assert.Contains("SelectedLinksRegistry", codeBehind, StringComparison.Ordinal);
  }

  private static string RepoRoot()
  {
    DirectoryInfo? dir = new(typeof(LinksWindowTests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }
    return (dir ?? throw new InvalidOperationException("slnx not found above the test assembly")).FullName;
  }
}

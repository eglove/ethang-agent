using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>An app-level warning reaches the shell's own chrome (issue #382).
///     <para>The RSS watchdog's sustained-breach escalation is about the APP, so it cannot
///     land in a session transcript: the user may have no tab open, or a different one
///     selected. Before this the monitor appended audit rows that nothing read - it reported
///     a problem it was structurally unable to surface.</para></summary>
[Trait("Category", "Process")]
public class AppWarningTests
{
  /// <summary>The bar is ABSENT until there is something to say, so an ordinary session
  ///     renders exactly the chrome it did before.</summary>
  [AvaloniaFact]
  public void The_Warning_Bar_Is_Hidden_Until_A_Warning_Arrives()
  {
    (MainViewModel vm, MainWindow window) = Show();
    Border bar = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Name == "AppWarningBar");
    Assert.False(bar.IsVisible, "a shell with no warning must render no warning bar");
    window.Close();
    GC.KeepAlive(vm);
  }

  /// <summary>The escalation is visible, and it says what the user needs to act on.</summary>
  [AvaloniaFact]
  public void A_Warning_Reaches_The_Shell_And_Reads_As_A_Sentence()
  {
    (MainViewModel vm, MainWindow window) = Show();
    vm.WarnApp("The app is using 6221 MB of memory and has stayed above its 4096 MB limit for 5 checks in a row.");
    Dispatcher.UIThread.RunJobs();

    Border bar = window.GetVisualDescendants().OfType<Border>()
        .First(b => b.Name == "AppWarningBar");
    Assert.True(bar.IsVisible, "a raised warning must be on screen");
    TextBlock text = window.GetVisualDescendants().OfType<TextBlock>()
        .First(t => t.Name == "AppWarningText");
    Assert.Contains("6221 MB", text.Text ?? string.Empty, StringComparison.Ordinal);
    window.Close();
    GC.KeepAlive(vm);
  }

  private static (MainViewModel Vm, MainWindow Window) Show()
  {
    MainViewModel vm = new((root, provider) => Task.FromResult(
        Result.Success(TestFixtures.FakeSessionFor(TestFixtures.CreateViewModel()))));
    MainWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return (vm, window);
  }
}

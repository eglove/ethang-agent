using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Status-bar session id: the VM exposes it, the view shows a short form
///     with the full id as tooltip and copies on click.</summary>
public class SessionIdStatusTests
{
  [Fact]
  public void Vm_Exposes_Session_Id_And_Short_Form()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();
    Guid id = vm.SessionId;
    Assert.NotEqual(Guid.Empty, id);
    Assert.Equal(8, vm.SessionIdShort.Length);
    Assert.StartsWith(id.ToString()[..8], vm.SessionIdShort, StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public void Status_Bar_Shows_Short_Id_With_Full_Tooltip_And_Copies_On_Click()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    TextBlock idText = view.GetControl<TextBlock>("SessionIdText");
    Assert.Equal(vm.SessionIdShort, idText.Text);
    Assert.Equal(vm.SessionIdFull, ToolTip.GetTip(idText) as string);
    Button copy = view.GetControl<Button>("CopySessionId");
    IClipboard? clip = TopLevel.GetTopLevel(view)?.Clipboard;
    Assert.NotNull(clip);
    // Raise the Button's own Click routed event - the exact event a real click
    // raises - so the view's handler wiring is exercised end to end.
    copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    // SetTextAsync is async - poll briefly for the value to land.
    string? got = null;
    for (int i = 0; i < 20 && got is null; i++)
    {
      got = ClipboardExtensions.TryGetTextAsync(clip).GetAwaiter().GetResult();
      if (got is null)
      {
        Task.Delay(50).Wait();
        Dispatcher.UIThread.RunJobs();
      }
    }
    Assert.Equal(vm.SessionId.ToString(), got);
  }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Status-bar session id: ONE button shows the "Sess. Id" label with a copy
///     glyph, the full id as tooltip, copies on click, and flashes a checkmark on
///     success before reverting.</summary>
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
    Button copy = view.GetControl<Button>("SessionIdButton");
    Assert.Equal(vm.SessionIdFull, ToolTip.GetTip(copy) as string);
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

  [AvaloniaFact]
  public void Session_Id_Button_Carries_Label_And_Copy_Glyph()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    Button copy = view.GetControl<Button>("SessionIdButton");
    string label = (copy.Content as string) ?? string.Empty;
    Assert.Contains("Sess. Id", label, StringComparison.Ordinal);
    Assert.Contains(SessionIdStatusLabel.CopyGlyph.ToString(), label, StringComparison.Ordinal);
    Assert.Equal(SessionIdStatusLabel.Default, label);
  }

  [AvaloniaFact]
  public void Session_Id_Button_Flashes_Checkmark_After_Copy_Then_Reverts()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    Button copy = view.GetControl<Button>("SessionIdButton");
    copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    // Drain only down to Normal priority: the success content is set on the
    // clipboard continuation; the revert is posted at Background and must not run yet.
    bool sawCheckmark = false;
    for (int i = 0; i < 20 && !sawCheckmark; i++)
    {
      Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
      string now = (copy.Content as string) ?? string.Empty;
      sawCheckmark = now.Contains(SessionIdStatusLabel.SuccessGlyph.ToString(), StringComparison.Ordinal);
      if (!sawCheckmark)
      {
        Task.Delay(50).Wait();
      }
    }

    Assert.True(sawCheckmark, $"expected a checkmark flash, content was '{copy.Content}'");
    // Draining everything restores the default label.
    Dispatcher.UIThread.RunJobs();
    Assert.Equal(SessionIdStatusLabel.Default, copy.Content);
  }
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

public class AgentViewTests
{
  [AvaloniaFact]
  public void Typing_And_Enter_Sends_User_Message_To_Transcript()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();

    // Named controls live in the AgentView's own name scope.
    AgentView view = (AgentView)window.Content;

    TextBox input = view.GetControl<TextBox>("InputBox");
    _ = input.Focus();
    input.Text = "hello agent";
    window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

    Assert.Contains(vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "hello agent");
  }
}

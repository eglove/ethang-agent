using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

public class AgentViewTests
{
    [AvaloniaFact]
    public void Typing_And_Enter_Sends_User_Message_To_Transcript()
    {
        var vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
        var window = new Window { Content = new AgentView { DataContext = vm } };
        window.Show();

        // Named controls live in the AgentView's own name scope.
        var view = (AgentView)window.Content!;

        var input = view.GetControl<TextBox>("InputBox");
        input.Focus();
        input.Text = "hello agent";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Contains(vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "hello agent");
    }

    [AvaloniaFact]
    public void Slash_Opens_Autocomplete_Listing_Three_Commands()
    {
        var vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
        var window = new Window { Content = new AgentView { DataContext = vm } };
        window.Show();

        // Named controls live in the AgentView's own name scope.
        var view = (AgentView)window.Content!;

        var input = view.GetControl<TextBox>("InputBox");
        var popup = view.GetControl<Popup>("CommandPopup");
        var list = view.GetControl<ListBox>("CommandList");
        input.Focus();
        input.Text = "/";

        Assert.True(popup.IsOpen);
        Assert.Equal(3, list.ItemCount);
    }

    [AvaloniaFact]
    public void Escape_Dismisses_Autocomplete()
    {
        var vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
        var window = new Window { Content = new AgentView { DataContext = vm } };
        window.Show();

        // Named controls live in the AgentView's own name scope.
        var view = (AgentView)window.Content!;

        var input = view.GetControl<TextBox>("InputBox");
        var popup = view.GetControl<Popup>("CommandPopup");
        input.Focus();
        input.Text = "/e";
        Assert.True(popup.IsOpen); // /exit matches prefix "e"

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(popup.IsOpen);
    }
}
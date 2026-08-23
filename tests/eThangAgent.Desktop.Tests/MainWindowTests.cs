using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Typing_And_Enter_Sends_User_Message_To_Transcript()
    {
        var vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
        var window = new MainWindow(vm);
        window.Show();

        var input = window.GetControl<TextBox>("InputBox");
        input.Focus();
        input.Text = "hello agent";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Contains(vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "hello agent");
    }

    [AvaloniaFact]
    public void Slash_Opens_Autocomplete_Listing_Three_Commands()
    {
        var vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
        var window = new MainWindow(vm);
        window.Show();

        var input = window.GetControl<TextBox>("InputBox");
        var popup = window.GetControl<Popup>("CommandPopup");
        var list = window.GetControl<ListBox>("CommandList");
        input.Focus();
        input.Text = "/";

        Assert.True(popup.IsOpen);
        Assert.Equal(3, list.ItemCount);
    }

    [AvaloniaFact]
    public void Escape_Dismisses_Autocomplete()
    {
        var vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
        var window = new MainWindow(vm);
        window.Show();

        var input = window.GetControl<TextBox>("InputBox");
        var popup = window.GetControl<Popup>("CommandPopup");
        input.Focus();
        input.Text = "/e";
        Assert.True(popup.IsOpen); // /exit matches prefix "e"

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(popup.IsOpen);
    }
}
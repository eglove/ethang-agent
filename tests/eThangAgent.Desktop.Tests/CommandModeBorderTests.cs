using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;

namespace eThangAgent.Desktop.Tests;

/// <summary>Command-mode affordance: an input line starting with '!' must be visibly
///     marked as a shell command before Enter — the amber border (and placeholder swap).
///     Pins the AXAML style class contract, not just the code-behind toggle: the class
///     alone is invisible if no style matches it (the regression this pins).</summary>
public class CommandModeBorderTests
{
  private static (AgentView View, TextBox Input) Show()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(marshalToUIThread: true);
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    return (view, view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "InputBox"));
  }

  private static Border? TemplateBorder(TextBox input)
      => input.GetVisualDescendants().OfType<Border>()
          .FirstOrDefault(b => b.Name == "PART_BorderElement");

  [AvaloniaFact]
  public void TypingExclamation_TogglesCommandClass_AndAmberBorder()
  {
    (AgentView _, TextBox input) = Show();

    input.Text = "! git status";
    Dispatcher.UIThread.RunJobs();

    Assert.True(input.Classes.Contains("command"), "command class must be applied");
    Border? border = TemplateBorder(input);
    Assert.NotNull(border);
    Assert.Equal(Color.Parse("#FFB454"), ((ISolidColorBrush)border.BorderBrush!).Color);
  }

  [AvaloniaFact]
  public void ClearingOrPlainText_RevertsClassAndBorder()
  {
    (AgentView _, TextBox input) = Show();

    input.Text = "! x";
    Dispatcher.UIThread.RunJobs();
    input.Text = "hello";
    Dispatcher.UIThread.RunJobs();

    Assert.DoesNotContain("command", input.Classes);
    Border? border = TemplateBorder(input);
    Assert.NotNull(border);
    Assert.NotEqual(Color.Parse("#FFB454"), ((ISolidColorBrush)border.BorderBrush!).Color);
  }

  [AvaloniaFact]
  public void FocusedInput_TypingExclamation_KeepsAmberBorder()
  {
    // The user sees command mode WHILE TYPING — the box has keyboard focus, and
    // Fluent's TextBox theme sets PART_BorderElement's brush directly in :focus/:pointerover
    // states, which overrides a value carried through the TemplateBinding. Pin that the
    // amber survives focus.
    (AgentView _, TextBox input) = Show();
    _ = input.Focus();
    Dispatcher.UIThread.RunJobs();

    input.Text = "! git status";
    Dispatcher.UIThread.RunJobs();

    Assert.True(input.IsFocused, "precondition: the input must be focused");
    Border? border = TemplateBorder(input);
    Assert.NotNull(border);
    Assert.Equal(Color.Parse("#FFB454"), ((ISolidColorBrush)border.BorderBrush!).Color);
  }

  [AvaloniaFact]
  public void Placeholder_SwapsToShellHint_InCommandMode()
  {
    (AgentView _, TextBox input) = Show();

    input.Text = "! x";
    Dispatcher.UIThread.RunJobs();

    Assert.Equal("Run a shell command (!)", input.PlaceholderText);
  }
}

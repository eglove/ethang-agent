using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The composer's reference wiring in the chat view: typing '@' opens the popup, a
///     commit closes it for good, the CARET feeds the popup (moving out of a token closes it),
///     Enter commits while the popup shows and sends only once it is closed, a refused send
///     keeps the input, and the readout counts the references the message names.</summary>
[Trait("Category", "Process")]
public class ReferenceComposerViewTests
{
  private const string Root = @"C:\work\demo";

  private static readonly ReferenceOption[] Catalog =
  [
    new("grill", "Interview the user", ReferenceKind.Skill, "@grill"),
    new("notes.txt", "", ReferenceKind.File, "@notes.txt"),
    new("new", "Start a new session", ReferenceKind.Command, "/new"),
  ];

  private sealed class Harness(
      AgentSessionViewModel vm, Window window, AgentView view, TextBox input, List<string> refreshes)
  {
    public AgentSessionViewModel Vm { get; } = vm;

    public Window Window { get; } = window;

    public AgentView View { get; } = view;

    public TextBox Input { get; } = input;

    public List<string> Refreshes { get; } = refreshes;
  }

  /// <summary>Hosts an AgentView over a session whose popup catalog is the two-entry catalog
  ///     plus the closed command registry, and whose resolver names the workspace's paths.</summary>
  private static Harness Show()
  {
    List<string> refreshes = [];
    AgentSessionViewModel vm = new(
        static (command, ct, callbacks, onNotice) => Task.FromResult(Result.Success("ack")),
        new RecordingLifecycle(new StubStore()),
        AgentId.NewId(),
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = Root,
          ReferenceCatalogSource = (trigger, query) => [.. Catalog
              .Where(o => trigger == ReferenceTrigger.Command
                  ? o.Kind == ReferenceKind.Command
                  : o.Kind != ReferenceKind.Command)
              .Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))],
          References = new ReferenceResolver(null, Root),
          ReferenceIndexRefresh = () =>
          {
            refreshes.Add("refresh");
            return Task.CompletedTask;
          },
        });
    Window window = new() { Content = new AgentView { DataContext = vm } };
    window.Show();
    AgentView view = (AgentView)window.Content;
    TextBox input = view.GetControl<TextBox>("InputBox");
    return new Harness(vm, window, view, input, refreshes);
  }

  /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds, bounded so a
  ///     wiring regression fails instead of hanging the suite.</summary>
  private static async Task SettleAsync(Func<bool> condition)
  {
    for (int attempt = 0; attempt < 200 && !condition(); attempt++)
    {
      Dispatcher.UIThread.RunJobs();
      await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  [AvaloniaFact]
  public void TypingAtOpensThePopupAndAcceptingClosesItForGood()
  {
    Harness h = Show();
    h.Input.Text = "hello @gr";
    h.Input.CaretIndex = 9;
    Dispatcher.UIThread.RunJobs();

    Assert.True(h.Vm.ReferenceCompletions!.IsOpen);
    h.View.AcceptCompletionsForTest();
    Dispatcher.UIThread.RunJobs();

    Assert.Equal("hello @grill ", h.Input.Text);
    Assert.False(h.Vm.ReferenceCompletions.IsOpen);
  }

  [AvaloniaFact]
  public void MovingTheCaretOutOfTheTokenClosesThePopup()
  {
    Harness h = Show();
    h.Input.Text = "see @gr now";
    h.Input.CaretIndex = 7;
    Dispatcher.UIThread.RunJobs();
    Assert.True(h.Vm.ReferenceCompletions!.IsOpen);

    h.Input.CaretIndex = 0;
    Dispatcher.UIThread.RunJobs();

    Assert.False(h.Vm.ReferenceCompletions.IsOpen);
  }

  [AvaloniaFact]
  public void EnterWhileThePopupIsOpenCommitsInsteadOfSending()
  {
    Harness h = Show();
    _ = h.Input.Focus();
    h.Input.Text = "hello @grill";
    h.Input.CaretIndex = 12;
    Dispatcher.UIThread.RunJobs();
    Assert.True(h.Vm.ReferenceCompletions!.IsOpen);

    h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Dispatcher.UIThread.RunJobs();

    Assert.Equal("hello @grill ", h.Input.Text);
    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is UserMessageEntry);
  }

  [AvaloniaFact]
  public void EnterWithThePopupClosedSends()
  {
    Harness h = Show();
    _ = h.Input.Focus();
    h.Input.Text = "hello there";
    h.Input.CaretIndex = 11;
    Dispatcher.UIThread.RunJobs();
    Assert.False(h.Vm.ReferenceCompletions!.IsOpen);

    h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

    Assert.Contains(h.Vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "hello there");
    Assert.Equal("", h.Input.Text);
  }

  [AvaloniaFact]
  public async Task AReferenceOutsideTheWorkspacePostsANoticeAndTheMessageSends()
  {
    // A reference that resolves to nothing is information, not a refusal: the box clears, the
    // message goes, and the notice says what was left out.
    Harness h = Show();
    _ = h.Input.Focus();
    h.Input.Text = "read @../outside.cs please";
    h.Input.CaretIndex = 26;
    Dispatcher.UIThread.RunJobs();

    h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    await SettleAsync(() => h.Input.Text.Length == 0).ConfigureAwait(true);

    Assert.Equal("", h.Input.Text);
    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is UserMessageEntry u && u.Text == "read @../outside.cs please");
    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is NoticeEntry n && n.Text.Contains("ReferenceOutsideWorkspace", StringComparison.Ordinal));
  }

  [AvaloniaFact]
  public async Task TheReadoutCountsTheReferences()
  {
    Harness h = Show();
    h.Input.Text = "check @notes.txt";
    Dispatcher.UIThread.RunJobs();
    await SettleAsync(() => h.Vm.ReferenceCountLabel.Length > 0).ConfigureAwait(true);

    Assert.Equal("1 reference", h.Vm.ReferenceCountLabel);
    TextBlock count = h.View.GetControl<TextBlock>("ReferenceCountText");
    Assert.Equal(h.Vm.ReferenceCountLabel, count.Text);
  }

  [AvaloniaFact]
  public async Task ACommittedReferenceSendsTheMessageWithThePathNamed()
  {
    // Committing a reference puts the path in the message; sending it names that path to the
    // model. Nothing is read, so no tool card appears beneath the bubble.
    Harness h = Show();
    _ = h.Input.Focus();
    h.Input.Text = "check @not";
    h.Input.CaretIndex = 11;
    Dispatcher.UIThread.RunJobs();
    Assert.True(h.Vm.ReferenceCompletions!.IsOpen);

    h.View.AcceptCompletionsForTest();
    Dispatcher.UIThread.RunJobs();
    Assert.Equal("check @notes.txt ", h.Input.Text);
    Assert.False(h.Vm.ReferenceCompletions.IsOpen);

    h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    await SettleAsync(() => h.Vm.Transcript.Entries.Any(e => e is UserMessageEntry)).ConfigureAwait(true);

    Assert.Contains(h.Vm.Transcript.Entries,
        e => e is UserMessageEntry u && u.Text == "check @notes.txt");
    Assert.DoesNotContain(h.Vm.Transcript.Entries, e => e is ToolResultEntry);
    Assert.Equal("", h.Input.Text);
  }

  [AvaloniaFact]
  public void FocusingTheInputRefreshesTheFileIndex()
  {
    Harness h = Show();
    h.Refreshes.Clear();

    _ = h.Input.Focus();
    Dispatcher.UIThread.RunJobs();

    Assert.NotEmpty(h.Refreshes);
  }
}

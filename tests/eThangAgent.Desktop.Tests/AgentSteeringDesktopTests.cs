using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>While a turn runs, input steers instead of being dropped: it posts to the
/// session inbox and echoes in the transcript; stopping (the Stop button —
/// <see cref="AgentSessionViewModel.RequestStop"/>) hard-cancels the turn and interrupts
/// the session's sub-agents. All awaits are bounded — every gate has a reachable settle path.</summary>
public class AgentSteeringDesktopTests
{
  private static (AgentSessionViewModel Vm, AgentId RootId, RecordingLifecycle Lifecycle) Build(
      TurnRunner runner, BoundedAgentMailbox inbox, TestFixtures.StubAgentRuntime runtime)
  {
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentId rootId = AgentId.NewId();
    AgentSessionViewModel vm = new(
        runner, lifecycle, rootId, new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          Inbox = inbox,
          ChildRuntime = runtime,
        });
    return (vm, rootId, lifecycle);
  }

  [Fact]
  public async Task BusySubmit_PostsToInbox_AndEchoesInTranscript()
  {
    BoundedAgentMailbox inbox = new();
    TestFixtures.ParkingRunner park = new();
    (AgentSessionViewModel? vm, AgentId _, RecordingLifecycle _) = Build(park.RunAsync, inbox, new TestFixtures.StubAgentRuntime());

    Task turnTask = vm.SubmitAsync("first question");
    await park.Started.ConfigureAwait(true);
    Assert.True(vm.IsBusy);

    _ = vm.SubmitAsync("also check the config"); // busy: must steer, not drop

    Assert.Equal("also check the config", Assert.Single(TakeQueued(inbox)));
    _ = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[^1]);
    Assert.True(vm.MessageCount >= 2);

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  [Fact]
  public async Task StopWhileBusy_CancelsTurn_AndInterruptsTheSessionSubtree()
  {
    TestFixtures.StubAgentRuntime runtime = new();
    TestFixtures.ParkingRunner park = new();
    (AgentSessionViewModel? vm, AgentId rootId, RecordingLifecycle _) =
        Build(park.RunAsync, new BoundedAgentMailbox(), runtime);

    Task turnTask = vm.SubmitAsync("long running work");
    await park.Started.ConfigureAwait(true);

    vm.RequestStop(); // the Stop button's entry point
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();

    Assert.False(vm.IsBusy);
    Assert.True(park.ObservedToken.IsCancellationRequested);
    // Scoped teardown: exactly one subtree interrupt rooted at THIS session's root id —
    // never a blanket (null-id) Interrupt, which in remote-host mode cancels every
    // active child in the host, including other sessions' children.
    AgentId interruptedRoot = Assert.Single(runtime.SubtreeInterrupts);
    Assert.Equal(rootId, interruptedRoot);
    Assert.Equal(0, runtime.InterruptAllCount);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [TurnCancelled]", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task StopWhenIdle_ShowsNotice_AndDoesNotInterruptChildren()
  {
    TestFixtures.StubAgentRuntime runtime = new();
    (AgentSessionViewModel? vm, AgentId _, RecordingLifecycle _) =
        Build((_, _, _, _) => Task.FromResult(Result.Success("ok")),
            new BoundedAgentMailbox(), runtime);

    await vm.SubmitAsync("quick turn");
    await vm.WaitForTurnAsync();

    vm.RequestStop();
    await vm.WaitForTurnAsync();

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("No active turn to stop.", notice.Text, StringComparison.Ordinal);
    Assert.Equal(0, runtime.InterruptAllCount);
  }

  [Fact]
  public async Task SteeringWithoutInbox_ShowsNoInboxError()
  {
    TestFixtures.ParkingRunner park = new();
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentSessionViewModel vm = new(
        park.RunAsync, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo" });

    Task turnTask = vm.SubmitAsync("work");
    await park.Started.ConfigureAwait(true);
    _ = vm.SubmitAsync("steer attempt");

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [NoInbox]", notice.Text, StringComparison.Ordinal);

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  /// <summary>Issue #189 B4/B5: a refused post (full mailbox) renders a notice
  ///     naming the reason and produces NO user entry - nothing claims to be queued.</summary>
  /// <summary>The user's reported scramble, driven through the SESSION surface rather than
  ///     the transcript directly: a message typed mid-stream stays pinned at the bottom while
  ///     the turn keeps streaming thoughts and running tools, and the turn's output lands
  ///     ABOVE it in arrival order. Before the fix the tool cards appended BELOW the pinned
  ///     echo while the next iteration's thoughts were inserted ABOVE it, so a later thought
  ///     rendered above an earlier tool card.</summary>
  [Fact]
  public async Task MidStreamSteer_KeepsTurnOutputAboveThePinnedEcho_InArrivalOrder()
  {
    BoundedAgentMailbox inbox = new();
    TestFixtures.ParkingRunner park = new();
    (AgentSessionViewModel? vm, AgentId _, RecordingLifecycle _) =
        Build(park.RunAsync, inbox, new TestFixtures.StubAgentRuntime());

    Task turnTask = vm.SubmitAsync("first question");
    await park.Started.ConfigureAwait(true);

    // The turn streams a thought, then the user steers mid-stream.
    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.Reasoning("considering the config"));
    _ = vm.SubmitAsync("also check the config");

    // The turn keeps going: a tool call, its result, then the next iteration's thoughts.
    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.ToolCallEvent("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}"));
    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.ToolResultEvent("read", "12 lines", "1: first", false));
    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.Reasoning("now checking the next file"));
    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.Delta("more text"));

    // The echo is still LAST and still pending - the model has not drained it.
    UserMessageEntry pinned = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[^1]);
    Assert.True(pinned.Pending);
    Assert.Equal("also check the config", pinned.Text);

    // Everything above it is in ARRIVAL order: the tool call did not fall below the echo.
    // The transcript always opens with the session's bootstrap context line, which is not
    // turn output and is skipped here.
    List<TranscriptEntry> above = [.. vm.Transcript.Entries.Skip(1).Take(vm.Transcript.Entries.Count - 2)];
    _ = Assert.IsType<BootstrapEntry>(vm.Transcript.Entries[0]);
    Assert.Collection(above,
        e => Assert.Equal("first question", Assert.IsType<UserMessageEntry>(e).Text),
        e => Assert.Equal("considering the config", Assert.IsType<ReasoningEntry>(e).Text),
        e => Assert.Equal("read", Assert.IsType<ToolCallEntry>(e).Name),
        e => Assert.Equal("read", Assert.IsType<ToolResultEntry>(e).Name),
        e => Assert.Equal("now checking the next file", Assert.IsType<ReasoningEntry>(e).Text),
        e => Assert.Equal("more text", Assert.IsType<AssistantTextEntry>(e).Text));

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  [Fact]
  public async Task SteeringIntoFullInbox_ShowsRefusalNotice_AndNoUserEntry()
  {
    TestFixtures.ParkingRunner park = new();
    BoundedAgentMailbox inbox = new(capacity: 1);
    (AgentSessionViewModel? vm, AgentId _, RecordingLifecycle _) = Build(park.RunAsync, inbox, new TestFixtures.StubAgentRuntime());

    Task turnTask = vm.SubmitAsync("first question");
    await park.Started.ConfigureAwait(true);
    _ = vm.SubmitAsync("queued once");   // accepted: 1/1
    _ = vm.SubmitAsync("overflow message"); // refused: full

    // The refusal lands ABOVE the pinned echo, not at the end: while a steering echo is
    // pending it stays the transcript's LAST entry so the turn's own output flows above it
    // (issue #253 B3's pin invariant). The contract asserted here is the one the issue
    // states - the refusal is VISIBLE and nothing claims the refused text was queued - so
    // the assertion finds the notice wherever the pin put it instead of assuming the end.
    NoticeEntry notice = Assert.Single(vm.Transcript.Entries.OfType<NoticeEntry>(),
        e => e.Text.Contains("Error [MailboxFull]", StringComparison.Ordinal));
    Assert.Contains("not queued", notice.Text, StringComparison.Ordinal);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is UserMessageEntry u && u.Text == "overflow message");
    // The accepted message is the one still pinned at the bottom.
    UserMessageEntry pinned = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[^1]);
    Assert.True(pinned.Pending);
    Assert.Equal("queued once", pinned.Text);

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  /// <summary>Issue #319: THE PINNED ECHO IS RELEASED, through the real inbox the session
  ///     actually holds. A pending user entry is added, the loop's drain fact arrives naming
  ///     the id the sender minted, and <c>MarkDrained</c> clears the pending flag.
  ///     <para>Why this shape and not a hand-published fact: the defect was that the id the
  ///     app minted never survived the trip. Here the session steers into a REAL
  ///     <c>SignalingInbox</c> over a real <c>BoundedAgentMailbox</c>, the drain is the same
  ///     <c>TryTake</c> the loop performs, and the fact is built from what that drain
  ///     returned — so an identity dropped anywhere along the path fails this test.</para></summary>
  [Fact]
  public async Task SteeredEcho_IsReleasedByTheDrainFactNamingTheIdTheInboxActuallyHeld()
  {
    BoundedAgentMailbox mailbox = new();
    // The production root inbox shape: the decorator over an addressable box.
    SignalingInbox inbox = new(mailbox, new WaitWakeHub(), AgentId.NewId());
    TestFixtures.ParkingRunner park = new();
    RecordingLifecycle lifecycle = new(new StubStore());
    AgentId rootId = AgentId.NewId();
    InProcessAgentEvents events = new();
    AgentSessionViewModel vm = new(
        park.RunAsync, lifecycle, rootId, new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          Inbox = inbox,
          ChildEvents = events,
          UiMarshal = action => action(),
        });

    Task turnTask = vm.SubmitAsync("first question");
    await park.Started.ConfigureAwait(true);
    _ = vm.SubmitAsync("also check the config");

    UserMessageEntry pinned = Assert.Single(vm.Transcript.Entries.OfType<UserMessageEntry>(),
        e => e.Text == "also check the config");
    Assert.True(pinned.Pending, "the echo is pinned until the model has seen it");

    // The loop's safe-point drain: take the message and publish the fact naming it.
    Assert.True(mailbox.TryTake(out string drained, out Guid drainedId));
    Assert.Equal("also check the config", drained);
    events.Publish(new MailboxDrainedEvent(rootId, DateTimeOffset.UtcNow, [drainedId]));

    UserMessageEntry released = Assert.Single(vm.Transcript.Entries.OfType<UserMessageEntry>(),
        e => e.Text == "also check the config");
    Assert.False(released.Pending, "the drain fact named the id the echo was pinned under");

    park.Release();
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }

  private static List<string> TakeQueued(BoundedAgentMailbox inbox)

  {
    List<string> taken = [];
    while (inbox.TryTake(out string? text, out Guid _))
    {
      taken.Add(text);
    }

    return taken;
  }
}

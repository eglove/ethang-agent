using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #253 B3: a message typed mid-stream renders PINNED below the streaming
///     block until the loop actually drains it, then reflows to its chronological position.
///     The drain boundary is the loop's own fact (the inbox message id), not a guess from
///     timing - so the user's echo says exactly when the model will see it.
///     <para>View-model level with fakes, as the issue specifies: the transcript is driven
///     directly, so the pinning rule is pinned without a UI.</para></summary>
public class SteerPinnedEchoTests
{
  [Fact]
  public void PendingSteer_StaysPinnedBelowStreamingText_ThenReflowsOnDrain()
  {
    TranscriptViewModel transcript = new();
    Guid steerId = Guid.NewGuid();

    transcript.AddUser("first question");
    transcript.AppendAssistantDelta("thinking about it");
    transcript.AddPendingUser("also check the config", steerId);

    // PINNED: the steer stays below the streaming block even though the stream continues.
    transcript.AppendAssistantDelta(" more text");
    UserMessageEntry pinned = Assert.IsType<UserMessageEntry>(transcript.Entries[^1]);
    Assert.Equal("also check the config", pinned.Text);
    Assert.True(pinned.Pending);
    // The stream text grew ABOVE the echo, not below it.
    Assert.Contains("thinking about it more text", transcript.AssistantText, StringComparison.Ordinal);

    // DRAIN: the loop's fact names the message, and the echo stops being pending.
    transcript.MarkDrained(steerId);

    UserMessageEntry drained = Assert.Single(transcript.Entries.OfType<UserMessageEntry>(),
        entry => entry.Text == "also check the config");
    Assert.False(drained.Pending);
    // Reflowed: it is no longer pinned, so the NEXT entry lands after it rather than above.
    // (While pending, a new entry is inserted BEFORE the echo; after the drain it is not.)
    transcript.EndIteration();
    transcript.AppendAssistantDelta("and more");
    _ = Assert.IsType<AssistantTextEntry>(transcript.Entries[^1]);
    int steerIndex = transcript.Entries.ToList().FindIndex(e => e is UserMessageEntry u && u.Text == "also check the config");
    Assert.True(steerIndex >= 0 && steerIndex < transcript.Entries.Count - 1,
        "after the drain the echo is at its chronological position, with later stream text below it");
  }

  /// <summary>The pin is ONE sequence, not a text-only refinement. A tool call, its result,
  ///     a notice and a system line that arrive while the echo is pending all land ABOVE it
  ///     in arrival order - the same place streamed text lands. Before this, structural
  ///     appends went straight to the end while text was inserted before the echo, so a tool
  ///     call sat BELOW the echo and the next iteration's thoughts sat ABOVE it: every later
  ///     thought rendered above an earlier tool card (the reported 'thoughts above tool
  ///     calls' scramble).</summary>
  [Fact]
  public void StructuralEntries_LandAboveThePinnedEcho_InArrivalOrder()
  {
    TranscriptViewModel transcript = new();
    Guid steerId = Guid.NewGuid();

    transcript.AddUser("first question");
    transcript.AppendAssistantDelta("checking");
    transcript.AddPendingUser("also check the config", steerId);

    // A whole tool exchange and two structural lines arrive while the echo is pending.
    transcript.AddToolCall("read", /*lang=json,strict*/ "{\"path\":\"a.cs\"}");
    transcript.AddToolResult("read", "12 lines", "1: first", false);
    transcript.AddNotice("a transient notice");
    transcript.AddSystemMessage("a nudge");
    // The next iteration's thoughts and text keep streaming.
    transcript.AppendReasoning("thinking again");
    transcript.AppendAssistantDelta("more text");

    // The echo is still LAST and still pending.
    UserMessageEntry pinned = Assert.IsType<UserMessageEntry>(transcript.Entries[^1]);
    Assert.True(pinned.Pending);
    Assert.Equal("also check the config", pinned.Text);

    // Everything above it is in ARRIVAL order: no structural entry was left below the echo.
    List<TranscriptEntry> above = [.. transcript.Entries.Take(transcript.Entries.Count - 1)];
    Assert.Collection(above,
        e => Assert.Equal("first question", Assert.IsType<UserMessageEntry>(e).Text),
        e => Assert.Equal("checking", Assert.IsType<AssistantTextEntry>(e).Text),
        e => Assert.Equal("read", Assert.IsType<ToolCallEntry>(e).Name),
        e => Assert.Equal("read", Assert.IsType<ToolResultEntry>(e).Name),
        e => Assert.Equal("a transient notice", Assert.IsType<NoticeEntry>(e).Text),
        e => Assert.Equal("a nudge", Assert.IsType<SystemMessageEntry>(e).Text),
        e => Assert.Equal("thinking again", Assert.IsType<ReasoningEntry>(e).Text),
        e => Assert.Equal("more text", Assert.IsType<AssistantTextEntry>(e).Text));
  }

  [Fact]
  public void DrainedIdForAnotherMessage_LeavesThePendingEchoAlone()
  {
    TranscriptViewModel transcript = new();
    Guid steerId = Guid.NewGuid();
    transcript.AddPendingUser("steer", steerId);

    transcript.MarkDrained(Guid.NewGuid());

    UserMessageEntry entry = Assert.Single(transcript.Entries.OfType<UserMessageEntry>());
    Assert.True(entry.Pending);
  }

  [Fact]
  public void NonPendingUserEntry_IsNeverRepositioned()
  {
    TranscriptViewModel transcript = new();
    transcript.AddUser("ordinary question");
    transcript.AppendAssistantDelta("answer");

    _ = Assert.IsType<AssistantTextEntry>(transcript.Entries[^1]);
    Assert.False(Assert.Single(transcript.Entries.OfType<UserMessageEntry>()).Pending);
  }
}

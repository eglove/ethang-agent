using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Live system messages (nudges, continuation prompts, compaction-failure
///     notices) surface in the chat as their own entry kind - distinct from ephemeral
///     host notices (errors, model-pick announcements). Restored persisted System
///     messages land on the same kind so a resumed transcript reads identically.
///     Pure view-model tests - no Avalonia types.</summary>
public class SystemMessageTranscriptTests
{
  [Fact]
  public void AddSystemMessage_AddsSystemMessageEntry_AndClosesOpenBlock()
  {
    TranscriptViewModel vm = new();
    vm.AppendAssistantDelta("partial");
    vm.AddSystemMessage("[nudge] remember to curate");

    SystemMessageEntry entry = Assert.IsType<SystemMessageEntry>(vm.Entries[^1]);
    Assert.Equal("[nudge] remember to curate", entry.Text);
    AssistantTextEntry closed = Assert.IsType<AssistantTextEntry>(vm.Entries[0]);
    Assert.False(closed.IsOpen);
  }

  [Fact]
  public void Restore_SystemMessage_BecomesSystemMessageEntry_NotNotice()
  {
    TranscriptViewModel vm = new();
    vm.Restore(
    [
      new Message(Role.User, "go", DateTimeOffset.UtcNow),
      new Message(Role.Assistant, "working", DateTimeOffset.UtcNow),
      new Message(Role.System, Anchor.ContinuationPromptText, DateTimeOffset.UtcNow),
    ]);

    SystemMessageEntry entry = Assert.IsType<SystemMessageEntry>(vm.Entries[2]);
    Assert.Equal(Anchor.ContinuationPromptText, entry.Text);
  }

  [Fact]
  public async Task StreamEvent_SystemMessage_RoutesThroughApplyStreamEvent()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();

    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.SystemMessage("[nudge] live"));

    SystemMessageEntry entry = Assert.IsType<SystemMessageEntry>(vm.Transcript.Entries[^1]);
    Assert.Equal("[nudge] live", entry.Text);
  }
}

/// <summary>Anchor constants this test cites without referencing the Agent Domain
///     (the Desktop does not depend on it). Verbatim loop contracts.</summary>
internal static class Anchor
{
  public const string ContinuationPromptText =
      "[Your previous message was cut off by the output limit. Continue exactly where you stopped; do not repeat earlier text.]";
}

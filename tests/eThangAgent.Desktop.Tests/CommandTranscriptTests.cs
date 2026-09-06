using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

public class CommandTranscriptTests
{
  [Fact]
  public void AddCommandResult_ShowsTailAndStatus()
  {
    TranscriptViewModel transcript = new();
    CommandRun run = new(7, "git status", 0, false, "line1\nline2\nline3", DateTimeOffset.UtcNow);

    transcript.AddCommandResult(run);

    CommandResultEntry entry = Assert.IsType<CommandResultEntry>(transcript.Entries[^1]);
    Assert.Equal(7, entry.RunId);
    Assert.Equal(0, entry.ExitCode);
    Assert.False(entry.TimedOut);
    Assert.False(entry.Truncated);
    Assert.Contains("line3", entry.OutputDisplay, StringComparison.Ordinal);
  }

  [Fact]
  public void AddCommandResult_TruncatesLongOutputToTail()
  {
    TranscriptViewModel transcript = new();
    string[] lines = [.. Enumerable.Range(1, 50).Select(i => $"line {i}")];
    CommandRun run = new(8, "big", 0, false, string.Join("\n", lines), DateTimeOffset.UtcNow);

    transcript.AddCommandResult(run, tailLines: 10);

    CommandResultEntry entry = Assert.IsType<CommandResultEntry>(transcript.Entries[^1]);
    Assert.True(entry.Truncated);
    Assert.Contains("line 50", entry.OutputDisplay, StringComparison.Ordinal);
    Assert.DoesNotContain("line 40\n", entry.OutputDisplay, StringComparison.Ordinal);
  }

  [Fact]
  public void EmptyOutput_DisplaysNoOutputPlaceholder()
  {
    TranscriptViewModel transcript = new();
    CommandRun run = new(9, "silent", 0, false, "", DateTimeOffset.UtcNow);

    transcript.AddCommandResult(run);

    CommandResultEntry entry = Assert.IsType<CommandResultEntry>(transcript.Entries[^1]);
    Assert.Equal("(no output)", entry.OutputDisplay);
  }

  [Theory]
  [InlineData(0, false)]
  [InlineData(3, true)]
  [InlineData(-1, true)]
  public void StatusLine_ReflectsOutcome(int exitCode, bool timedOut)
  {
    CommandRun run = new(2, "x", exitCode, timedOut, "", DateTimeOffset.UtcNow);
    TranscriptViewModel transcript = new();

    transcript.AddCommandResult(run);

    CommandResultEntry entry = Assert.IsType<CommandResultEntry>(transcript.Entries[^1]);
    Assert.Equal(timedOut || exitCode != 0, entry.TimedOut || entry.ExitCode != 0);
  }
}

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Event record shapes: D5 metadata boundary and the phase vocabulary.</summary>
public class ChildEventTests
{
  private static readonly DateTimeOffset T = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

  [Fact]
  public void Events_CarryMetadataOnly_NoReportContentMember()
  {
    ChildSettledEvent evt = new(new AgentId(Guid.NewGuid()), T, AgentStatus.Completed, null, ReportBytes: 1234);
    Assert.Equal(1234, evt.ReportBytes);
    Assert.DoesNotContain(typeof(ChildSettledEvent).GetProperties(),
        p => p.PropertyType == typeof(string) && p.Name.Contains("Report", StringComparison.Ordinal)
            && p.Name != nameof(ChildSettledEvent.ReportBytes));
  }

  [Fact]
  public void ProgressEvent_PhaseValues_AreTheLoopPositions()
    => Assert.Equal(["ModelCall", "ToolExec", "Draining"], [.. Enum.GetNames<ChildPhase>()]);
}

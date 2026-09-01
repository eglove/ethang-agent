
namespace eThangAgent.AgentDomain.Tests;

/// <summary>The approved preemption matrix (D1/D4-revised): Urgent preempts only with a
///     contract grant and a running receiver; everything else drains at boundaries.</summary>
public class PreemptionPolicyTests
{
  [Theory]
  [InlineData(false, MessageUrgency.Urgent, true, PreemptionDecision.PreemptDenied)]
  [InlineData(true, MessageUrgency.Urgent, true, PreemptionDecision.PreemptGranted)]
  [InlineData(true, MessageUrgency.Urgent, false, PreemptionDecision.PreemptDenied)]
  [InlineData(false, MessageUrgency.Urgent, false, PreemptionDecision.PreemptDenied)]
  [InlineData(true, MessageUrgency.Attention, true, PreemptionDecision.DrainAtBoundary)]
  [InlineData(true, MessageUrgency.Normal, true, PreemptionDecision.DrainAtBoundary)]
  [InlineData(false, MessageUrgency.Normal, false, PreemptionDecision.DrainAtBoundary)]
  public void Matrix_MatchesApprovedDecision(bool grant, MessageUrgency urgency, bool running, PreemptionDecision expected)
      => Assert.Equal(expected, PreemptionPolicy.Decide(grant, urgency, running));
}

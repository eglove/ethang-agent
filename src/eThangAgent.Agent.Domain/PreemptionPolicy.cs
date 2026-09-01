
namespace eThangAgent.AgentDomain;

/// <summary>Pure preemption decision policy (approved D1 / D4-revised). No I/O, no clock:
///     the caller hands in facts and audits the decision. Normal never preempts; Attention
///     drains at the next tool boundary but never interrupts; Urgent preempts ONLY when the
///     sender's spawn contract carries PreemptGrant (default: NOT granted) and the receiver
///     is actually running.</summary>
public static class PreemptionPolicy
{
  /// <summary>The graduated outcome for one send with an urgency class.</summary>
  public static PreemptionDecision Decide(bool senderHasPreemptGrant, MessageUrgency urgency, bool receiverRunning)
      => urgency switch
      {
        MessageUrgency.Normal => PreemptionDecision.DrainAtBoundary,
        MessageUrgency.Attention => PreemptionDecision.DrainAtBoundary,
        MessageUrgency.Urgent when senderHasPreemptGrant && receiverRunning => PreemptionDecision.PreemptGranted,
        _ => PreemptionDecision.PreemptDenied,
      };
}

public enum PreemptionDecision
{
  DrainAtBoundary,
  PreemptGranted,
  PreemptDenied,
}

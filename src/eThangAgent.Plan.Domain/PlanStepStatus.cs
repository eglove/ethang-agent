namespace eThangAgent.PlanDomain;

public enum PlanStepStatus
{
  Pending,
  InProgress,
  Done,

  /// <summary>Deliberately not doing this step. The truthful exit that makes the
  ///     completion invariant (issue #271) honest: without it a model facing the
  ///     refusal would mark the step Done to get past the gate.</summary>
  Skipped,
}

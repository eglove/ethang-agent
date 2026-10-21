namespace eThangAgent.AgentDomain;

/// <summary>How far a resource has drifted past its budget (spec D2).</summary>
public enum ResourceLevel
{
  Normal,
  Notice,
  Act,
  Emergency,
}

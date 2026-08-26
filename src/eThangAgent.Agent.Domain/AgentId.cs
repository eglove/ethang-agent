namespace eThangAgent.AgentDomain;

public readonly record struct AgentId(Guid Value)
{
  public static AgentId NewId() => new(Guid.NewGuid());

  public override string ToString() => Value.ToString();
}

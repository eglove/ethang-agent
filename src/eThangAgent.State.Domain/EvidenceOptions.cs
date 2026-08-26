namespace eThangAgent.StateDomain;

public sealed record EvidenceOptions
{
  public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

  public static EvidenceOptions Default { get; } = new();
}

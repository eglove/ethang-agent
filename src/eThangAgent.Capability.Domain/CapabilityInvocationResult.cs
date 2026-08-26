namespace eThangAgent.CapabilityDomain;

public sealed record CapabilityInvocationResult(string Content, bool IsError)
{
  public static CapabilityInvocationResult Ok(string content) => new(content, false);
  public static CapabilityInvocationResult Fail(string content) => new(content, true);
}

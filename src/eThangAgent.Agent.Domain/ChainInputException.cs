namespace eThangAgent.AgentDomain;

/// <summary>Signals invalid chain input at the domain boundary. Public only because CA1064
///     forbids non-public exception types; it never escapes the chain store or a capability
///     provider — every boundary catches it and renders the message as a typed error.</summary>
public sealed class ChainInputException : Exception
{
  public ChainInputException() : base("Invalid chain input.") { }
  public ChainInputException(string message) : base(message) { }
  public ChainInputException(string message, Exception innerException) : base(message, innerException) { }
}

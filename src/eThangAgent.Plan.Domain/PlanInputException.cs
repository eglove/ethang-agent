namespace eThangAgent.PlanDomain;

/// <summary>Signals invalid plan input at the domain boundary. Public only because
/// CA1064 forbids non-public exception types; it never escapes the capability
/// provider - every action catches it and renders the message as a typed tool error.</summary>
public sealed class PlanInputException : Exception
{
  public PlanInputException() : base("Invalid plan input.") { }
  public PlanInputException(string message) : base(message) { }
  public PlanInputException(string message, Exception innerException) : base(message, innerException) { }
}

namespace eThangAgent.Roslyn.ACL;

/// <summary>Thrown when a nested tool call made from a script violates its pre-dispatch
///     contract — missing or invalid <c>timeoutSeconds</c>, unknown action, or malformed
///     arguments. The message carries the verbatim <c>Error [Code]: ...</c> text of the
///     violation. Throwing turns a silent in-band error string (which batch scripts
///     routinely ignore) into a loud script fault: the engine preserves whatever
///     Output() evidence the script already collected and appends an Error [ScriptError]
///     line, so a failed batch is visible at the offending call. Post-dispatch outcomes —
///     tool-level errors and elapsed budgets — remain in-band result strings by design.
///     Not [Serializable]: binary serialization is legacy; nothing marshals this type.</summary>
public sealed class ScriptToolException : Exception
{
  public ScriptToolException()
  {
  }

  public ScriptToolException(string message) : base(message)
  {
  }

  public ScriptToolException(string message, Exception innerException) : base(message, innerException)
  {
  }
}

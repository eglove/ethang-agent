namespace eThangAgent.Roslyn.ACL;

/// <summary>Thrown when a nested tool call made from a script fails in a way the
///     ARGUMENTS cannot explain: a missing or invalid <c>timeoutSeconds</c> the harness
///     itself checks, an unknown action, arguments that are not valid JSON or not an
///     object, or a failure reported by a tool that DID run work. The message carries the
///     verbatim <c>Error [Code]: ...</c> text. Throwing turns a silent in-band error string
///     (which batch scripts routinely ignore) into a loud script fault: the engine
///     preserves whatever Output() evidence the script already collected and appends an
///     Error [ScriptError] line, so a failed batch is visible at the offending call.
///     <para>An argument-shape refusal from an action declaring
///     <c>ValidatesArgumentsBeforeDispatch</c> is deliberately NOT thrown (issue #259):
///     the tool refused before doing any work and named the argument at fault, so the same
///     call with that argument corrected is a legitimate continue-path — it returns as an
///     in-band result the script branches on.</para>
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

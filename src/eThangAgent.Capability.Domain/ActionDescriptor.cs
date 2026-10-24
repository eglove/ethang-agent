namespace eThangAgent.CapabilityDomain;

/// <summary>Who converts an action's validated <c>timeoutSeconds</c> budget into
///     call cancellation.</summary>
public enum TimeoutPolicy
{
  /// <summary>The harness (e.g. ScriptTools.Invoke) cancels the call when the budget elapses.</summary>
  HarnessEnforced,
  /// <summary>The action applies its declared budget itself; the harness validates but never cancels on it.</summary>
  SelfManaged,
}

/// <summary>What a capability action advertises to the harness: its identity, its
///     contract text, its parameters, and the declarations dispatch reads.
///     <para><c>ValidatesArgumentsBeforeDispatch</c> is true when the action refuses a
///     call BEFORE performing any work, so its InvalidParameterValue / MissingParameter
///     refusal is about the ARGUMENTS alone and the remedy is the SAME call with the
///     named argument corrected. The exec bridge keeps such a refusal in-band as a
///     result the script branches on, instead of throwing and killing the whole program
///     (issue #259). The declaration is a claim the ACTION owns, which is why it rides
///     the descriptor rather than a name list inside the harness: an action whose error
///     text can also come from work already performed (edit's OccurrenceMismatch is
///     reported after matching the file) must NOT declare it, or a partially applied
///     batch could pass silently. Default false: an action that says nothing throws
///     exactly as before.</para></summary>
public sealed record ActionDescriptor(
    string Name,
    string Summary,
    string Description,
    IReadOnlyList<ActionParameter> Parameters,
    IReadOnlyList<string>? RequiredParameters = null,
    TimeoutPolicy Timeout = TimeoutPolicy.HarnessEnforced,
    bool ValidatesArgumentsBeforeDispatch = false);

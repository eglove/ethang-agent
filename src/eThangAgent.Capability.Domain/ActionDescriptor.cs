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

public sealed record ActionDescriptor(
    string Name,
    string Summary,
    string Description,
    IReadOnlyList<ActionParameter> Parameters,
    IReadOnlyList<string>? RequiredParameters = null,
    TimeoutPolicy Timeout = TimeoutPolicy.HarnessEnforced);

namespace eThangAgent.ToolDomain;

/// <summary>A dispatch refusal for a tool the agent's contract does not grant (R1.3).
///     Rendered verbatim so the model can distinguish POLICY (denied) from TYPO
///     (unknown) — the two cases must never share an error line.</summary>
public static class GrantViolation
{
  /// <summary>The exact one-line format contract for a grant refusal.</summary>
  public const string Format = "Error [GrantViolation]: tool '{0}' is not granted to this agent.";

  public static string For(string toolName)
      => string.Create(System.Globalization.CultureInfo.InvariantCulture,
          $"Error [GrantViolation]: tool '{toolName}' is not granted to this agent.");
}

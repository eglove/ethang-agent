namespace eThangAgent.AgentDomain;

public static class RuntimeErrors
{
    public const string CapReached =
        "Error [ConcurrencyCapReached]: The agent runtime is at its concurrent-agent limit. Retrieve pending results (agent.result) or wait, then retry.";

    /// <summary>Canonical interrupted-turn failure. Agent.SendMessage returns this (never an
    /// exception) when the turn's token fires; the conversation is repaired before returning.</summary>
    public const string TurnCancelled =
        "Error [TurnCancelled]: The turn was interrupted by the user.";


    public static string NotFound(Guid id) => $"Error [NotFound]: No agent exists with id '{id}'.";

    public static string NotComplete(Guid id) => $"Error [NotComplete]: Agent '{id}' has not finished running. Check agent.status later.";
}

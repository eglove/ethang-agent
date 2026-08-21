namespace eThangAgent.AgentDomain;

public static class RuntimeErrors
{
    public const string CapReached =
        "Error [ConcurrencyCapReached]: The agent runtime is at its concurrent-agent limit. Retrieve pending results (agent.result) or wait, then retry.";

    public static string NotFound(Guid id) => $"Error [NotFound]: No agent exists with id '{id}'.";

    public static string NotComplete(Guid id) => $"Error [NotComplete]: Agent '{id}' has not finished running. Check agent.status later.";
}

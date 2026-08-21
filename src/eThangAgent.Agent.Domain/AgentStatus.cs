namespace eThangAgent.AgentDomain;

public enum AgentStatus
{
    Running,
    Completed,
    Failed,
}

public enum AgentFailureReason
{
    MaxIterations,
    Timeout,
    ProviderError,
}
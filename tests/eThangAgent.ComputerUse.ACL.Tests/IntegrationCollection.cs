namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Serializes the owned-window integration suite: the single fixture window and the
///     per-test brokers race when tests run concurrently (13 brokers UIA-walking one window).</summary>
[CollectionDefinition("IntegrationSequential", DisableParallelization = true)]
public sealed class IntegrationSequentialDefinition;

namespace eThangAgent.Composition.Tests;

/// <summary>AgentConfiguration.Load reads process-global environment variables.
///     Every test class that mutates them (setting → asserting → clearing in a
///     finally) must run serially against the others: one collection means xunit
///     never interleaves those windows across classes.</summary>
// Named decision (CA1515): xUnit requires the collection definition type to be public
// for discovery; internal would silently split the collection into per-class runs.
#pragma warning disable CA1515 // Types can be made internal
[CollectionDefinition("EnvironmentSensitive")]
public sealed class EnvironmentSensitiveCollections { }
#pragma warning restore CA1515

namespace eThangAgent.Composition.Tests;

/// <summary>ETHANG_AGENT_DB selects the app database file — the bootstrap location of
///     the store itself (chicken-and-egg keeps it an environment variable), and the
///     test seam. It is database LOCATION, not app configuration: every setting lives
///     in app preferences now. Tests that set it run serially in this collection.</summary>
// Named decision (CA1515): xUnit requires the collection definition type to be public
// for discovery; internal would silently split the collection into per-class runs.
#pragma warning disable CA1515 // Types can be made internal
[CollectionDefinition("EnvironmentSensitive")]
public sealed class EnvironmentSensitiveCollections { }
#pragma warning restore CA1515

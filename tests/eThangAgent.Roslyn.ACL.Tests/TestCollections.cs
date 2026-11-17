namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>PATH is process-global, and the tests that put a directory at the front of it are
///     members of this collection so they run serially with each other instead of racing one
///     another's shims.</summary>
// Named decision (CA1515): xUnit requires the collection definition type to be public
// for discovery; internal would silently split the collection into per-class runs.
#pragma warning disable CA1515 // Types can be made internal
[CollectionDefinition("ProcessGlobalState")]
public sealed class ProcessGlobalStateCollections { }
#pragma warning restore CA1515

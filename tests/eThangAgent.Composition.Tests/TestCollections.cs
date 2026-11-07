namespace eThangAgent.Composition.Tests;

/// <summary>ETHANG_AGENT_DB selects the app database file — the bootstrap location of
///     the store itself (chicken-and-egg keeps it an environment variable), and the
///     test seam. It is database LOCATION, not app configuration: every setting lives
///     in app preferences now. The variable is process-global, so every test class
///     that sets it is a member of this collection and runs serially with the
///     others.
///     <para>That membership is not a convention: <c>DoctrineTests.ProcessGlobalState_IsSerializedByRuleNotConvention</c>
///     fails the build for a test file that sets this variable, or moves the process
///     working directory, without a collection attribute.</para></summary>
// Named decision (CA1515): xUnit requires the collection definition type to be public
// for discovery; internal would silently split the collection into per-class runs.
#pragma warning disable CA1515 // Types can be made internal
[CollectionDefinition("EnvironmentSensitive")]
public sealed class EnvironmentSensitiveCollections { }
#pragma warning restore CA1515

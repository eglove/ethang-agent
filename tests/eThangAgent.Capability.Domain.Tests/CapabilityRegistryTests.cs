using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityRegistryTests
{
  private static ActionDescriptor Act(string name) => new(name, "sum", "desc", []);

  [Fact]
  public void Create_NoProviders_CreatesEmptyRegistry()
  {
    CapabilityRegistry registry = CapabilityRegistry.Create([]);
    Assert.Empty(registry.Providers);
    Result<ResolvedCapability> resolved = registry.Resolve("anything");
    Assert.False(resolved.IsSuccess);
  }

  [Fact]
  public void Create_EmptyProviderId_Throws()
  {
    InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
        () => CapabilityRegistry.Create([new FakeProvider("")]));
    Assert.Contains("id must be non-empty", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_DuplicateProviderId_Throws()
  {
    InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create(
        [new FakeProvider("agent", Act("read")), new FakeProvider("agent", Act("grep"))]));
    Assert.Contains("Duplicate capability provider id 'agent'", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_ProviderWithoutActions_Throws()
  {
    InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
        () => CapabilityRegistry.Create([new FakeProvider("agent")]));
    Assert.Contains("exposes no actions", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_InvalidActionName_Throws()
  {
    InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
        () => CapabilityRegistry.Create([new FakeProvider("agent", Act("read-file"))]));
    Assert.Contains("is invalid", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_DuplicateActionNameAcrossProviders_Throws()
  {
    InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create(
        [new FakeProvider("a", Act("read")), new FakeProvider("b", Act("read"))]));
    Assert.Contains("Duplicate action name 'read'", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Resolve_ByBareName_Succeeds()
  {
    CapabilityRegistry registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"))]);

    Result<ResolvedCapability> result = registry.Resolve("read");

    Assert.True(result.IsSuccess);
    Assert.Equal("agent", result.Value!.ProviderId);
    Assert.Equal("read", result.Value.Action.Name);
  }

  [Fact]
  public void Resolve_ByFullRef_Succeeds()
  {
    CapabilityRegistry registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"))]);

    Result<ResolvedCapability> result = registry.Resolve("agent.read");

    Assert.True(result.IsSuccess);
    Assert.Equal("read", result.Value!.Action.Name);
  }

  [Fact]
  public void Resolve_Unknown_ListsAvailable()
  {
    CapabilityRegistry registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"), Act("grep"))]);

    Result<ResolvedCapability> result = registry.Resolve("nope");

    Assert.False(result.IsSuccess);
    Assert.Equal("UnknownAction", result.Error!.Code);
    Assert.Contains("grep, read", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvokeAsync_RoutesToOwningProvider()
  {
    RecordingProvider provider = new("agent", Act("read"));
    CapabilityRegistry registry = CapabilityRegistry.Create([provider]);
    ResolvedCapability resolved = registry.Resolve("read").Value!;

    CapabilityInvocationResult result = await registry.InvokeAsync(resolved, "{}");

    Assert.False(result.IsError);
    Assert.Equal("{}", provider.LastJson);
  }

  private sealed class FakeProvider(string id, params ActionDescriptor[] actions) : ICapabilityProvider
  {
    public string Id { get; } = id;
    public IReadOnlyList<ActionDescriptor> Actions { get; } = actions;

    public Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
        => Task.FromResult(CapabilityInvocationResult.Ok("ok"));
  }

  private sealed class RecordingProvider(string id, params ActionDescriptor[] actions) : ICapabilityProvider
  {
    public string Id { get; } = id;
    public IReadOnlyList<ActionDescriptor> Actions { get; } = actions;
    public string? LastJson { get; private set; }

    public Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
      LastJson = jsonArguments;
      return Task.FromResult(CapabilityInvocationResult.Ok("ok"));
    }
  }
}

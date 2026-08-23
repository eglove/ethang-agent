using eThangAgent.CapabilityDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityRegistryTests
{
    private static ActionDescriptor Act(string name) => new(name, "sum", "desc", []);

    [Fact]
    public void Create_NoProviders_CreatesEmptyRegistry()
    {
        var registry = CapabilityRegistry.Create([]);
        Assert.Empty(registry.Providers);
        var resolved = registry.Resolve("anything");
        Assert.False(resolved.IsSuccess);
    }

    [Fact]
    public void Create_EmptyProviderId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapabilityRegistry.Create([new FakeProvider("")]));
        Assert.Contains("id must be non-empty", ex.Message);
    }

    [Fact]
    public void Create_DuplicateProviderId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create(
            [new FakeProvider("agent", Act("read")), new FakeProvider("agent", Act("grep"))]));
        Assert.Contains("Duplicate capability provider id 'agent'", ex.Message);
    }

    [Fact]
    public void Create_ProviderWithoutActions_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapabilityRegistry.Create([new FakeProvider("agent")]));
        Assert.Contains("exposes no actions", ex.Message);
    }

    [Fact]
    public void Create_InvalidActionName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapabilityRegistry.Create([new FakeProvider("agent", Act("read-file"))]));
        Assert.Contains("is invalid", ex.Message);
    }

    [Fact]
    public void Create_DuplicateActionNameAcrossProviders_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CapabilityRegistry.Create(
            [new FakeProvider("a", Act("read")), new FakeProvider("b", Act("read"))]));
        Assert.Contains("Duplicate action name 'read'", ex.Message);
    }

    [Fact]
    public void Resolve_ByBareName_Succeeds()
    {
        var registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"))]);

        var result = registry.Resolve("read");

        Assert.True(result.IsSuccess);
        Assert.Equal("agent", result.Value!.ProviderId);
        Assert.Equal("read", result.Value.Action.Name);
    }

    [Fact]
    public void Resolve_ByFullRef_Succeeds()
    {
        var registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"))]);

        var result = registry.Resolve("agent.read");

        Assert.True(result.IsSuccess);
        Assert.Equal("read", result.Value!.Action.Name);
    }

    [Fact]
    public void Resolve_Unknown_ListsAvailable()
    {
        var registry = CapabilityRegistry.Create([new FakeProvider("agent", Act("read"), Act("grep"))]);

        var result = registry.Resolve("nope");

        Assert.False(result.IsSuccess);
        Assert.Equal("UnknownAction", result.Error!.Code);
        Assert.Contains("grep, read", result.Error.Message);
    }

    [Fact]
    public async Task InvokeAsync_RoutesToOwningProvider()
    {
        var provider = new RecordingProvider("agent", Act("read"));
        var registry = CapabilityRegistry.Create([provider]);
        var resolved = registry.Resolve("read").Value!;

        var result = await registry.InvokeAsync(resolved, "{}");

        Assert.False(result.IsError);
        Assert.Equal("{}", provider.LastJson);
    }

    private sealed class FakeProvider : ICapabilityProvider
    {
        public FakeProvider(string id, params ActionDescriptor[] actions)
        {
            Id = id;
            Actions = actions;
        }

        public string Id { get; }
        public IReadOnlyList<ActionDescriptor> Actions { get; }

        public Task<CapabilityInvocationResult> InvokeAsync(
            string actionName, string jsonArguments, CancellationToken ct = default)
            => Task.FromResult(CapabilityInvocationResult.Ok("ok"));
    }

    private sealed class RecordingProvider : ICapabilityProvider
    {
        public RecordingProvider(string id, params ActionDescriptor[] actions)
        {
            Id = id;
            Actions = actions;
        }

        public string Id { get; }
        public IReadOnlyList<ActionDescriptor> Actions { get; }
        public string? LastJson { get; private set; }

        public Task<CapabilityInvocationResult> InvokeAsync(
            string actionName, string jsonArguments, CancellationToken ct = default)
        {
            LastJson = jsonArguments;
            return Task.FromResult(CapabilityInvocationResult.Ok("ok"));
        }
    }
}

using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityReferenceRendererTests
{
    [Fact]
    public void Render_GroupsByProvider_OneLinePerAction()
    {
        var registry = new StubRegistry(
            new ProviderCapabilities("agent",
            [
                new ActionDescriptor("read", "Read lines from a text file.", "full",
                [
                    new ActionParameter("path", "String", "file path"),
                    new ActionParameter("startLine", "Integer", "first line"),
                    new ActionParameter("endLine", "Integer", "last line"),
                ]),
            ]));

        var text = CapabilityReferenceRenderer.Render(registry);

        Assert.Equal(
            "## Available actions\nagent:\nread(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
            text);
    }

    private sealed class StubRegistry : ICapabilityRegistry
    {
        public StubRegistry(params ProviderCapabilities[] providers) => Providers = providers;

        public IReadOnlyList<ProviderCapabilities> Providers { get; }

        public Result<ResolvedCapability> Resolve(string nameOrRef)
            => Result<ResolvedCapability>.Failure(new Error("UnknownAction", "stub"));

        public Task<CapabilityInvocationResult> InvokeAsync(
            ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
            => Task.FromResult(CapabilityInvocationResult.Fail("stub"));
    }
}

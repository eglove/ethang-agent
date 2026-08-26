using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Capability.Domain.Tests;

public class CapabilityReferenceRendererTests
{
  [Fact]
  public void Render_GroupsByProvider_OneLinePerAction()
  {
    StubRegistry registry = new(
        new ProviderCapabilities("agent",
        [
            new ActionDescriptor("read", "Read lines from a text file.", "full",
                [
                    new ActionParameter("path", "String", "file path"),
                    new ActionParameter("startLine", "Integer", "first line"),
                    new ActionParameter("endLine", "Integer", "last line"),
                ]),
        ]));

    string text = CapabilityReferenceRenderer.Render(registry);

    Assert.Equal(
        "## Available actions\nagent:\nread(path: String, startLine: Integer, endLine: Integer): Read lines from a text file.",
        text);
  }

  private sealed class StubRegistry(params ProviderCapabilities[] providers) : ICapabilityRegistry
  {
    public IReadOnlyList<ProviderCapabilities> Providers { get; } = providers;

    public Result<ResolvedCapability> Resolve(string nameOrRef)
        => Result.Failure<ResolvedCapability>(new DomainError("UnknownAction", "stub"));

    public Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
        => Task.FromResult(CapabilityInvocationResult.Fail("stub"));
  }
}

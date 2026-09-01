using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain.Tests;

/// <summary>R1 exec-path tests: the filtered capability registry fails denied-but-real
///     actions with GrantViolation (distinguishable from unknown), passes granted ones
///     through untouched, and audits every denial.</summary>
public class FilteredCapabilityRegistryTests
{
  private sealed class StubProvider(params string[] actions) : ICapabilityProvider
  {
    public string Id => "stub";
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
        [.. actions.Select(a => new ActionDescriptor(a, "sum", "desc", [], []))];

    public Task<CapabilityInvocationResult> InvokeAsync(string actionName, string jsonArguments, CancellationToken ct = default)
        => Task.FromResult(new CapabilityInvocationResult("ok", false));
  }

  private static CapabilityRegistry Inner()
      => CapabilityRegistry.Create([new StubProvider("read", "exec", "web_fetch")]);

  [Fact]
  public void Resolve_GrantedAction_Resolves()
  {
    FilteredCapabilityRegistry registry = new(Inner(),
        new HashSet<string>(StringComparer.Ordinal) { "read", "web_fetch" });

    Assert.True(registry.Resolve("read").IsSuccess);
  }

  [Fact]
  public void Resolve_DeniedButRealAction_FailsWithGrantViolation()
  {
    FilteredCapabilityRegistry registry = new(Inner(),
        new HashSet<string>(StringComparer.Ordinal) { "read" });

    Result<ResolvedCapability> resolved = registry.Resolve("exec");
    Assert.False(resolved.IsSuccess);
    Assert.Equal("GrantViolation", resolved.Error.Code);
  }

  [Fact]
  public void Resolve_UnknownAction_InnerUnknownActionErrorStands()
  {
    FilteredCapabilityRegistry registry = new(Inner(),
        new HashSet<string>(StringComparer.Ordinal) { "read" });

    Result<ResolvedCapability> resolved = registry.Resolve("nope");
    Assert.False(resolved.IsSuccess);
    Assert.Equal("UnknownAction", resolved.Error.Code);
  }

  [Fact]
  public void Resolve_DeniedDispatch_AuditsTheActionName()
  {
    List<string> denied = [];
    FilteredCapabilityRegistry registry = new(Inner(),
        new HashSet<string>(StringComparer.Ordinal) { "read" }, onDenial: denied.Add);

    _ = registry.Resolve("exec");
    _ = registry.Resolve("nope");

    _ = Assert.Single(denied);
    Assert.Equal("exec", denied[0]);
  }

  [Fact]
  public void Providers_PassesThrough()
  {
    FilteredCapabilityRegistry registry = new(Inner(),
        new HashSet<string>(StringComparer.Ordinal) { "read" });

    Assert.NotEmpty(registry.Providers); // surface metadata is harmless; Resolve is the gate
  }
}

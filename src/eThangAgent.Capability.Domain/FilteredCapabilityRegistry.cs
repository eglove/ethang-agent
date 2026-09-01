using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain;

/// <summary>A capability registry narrowed to an effective grant set — the exec-path
///     twin of the Agent Domain's FilteredToolRegistry (R1): in production a child's
///     IToolRegistry holds only exec, and every real tool dispatch resolves through
///     the per-exec capability registry (selected via the running child), so grant
///     enforcement must hold at THIS boundary or it holds nowhere. Resolve fails
///     GrantViolation for denied-but-existing actions (distinguishable from unknown,
///     R1.3); unknown names fall through to the inner registry's own error. Every
///     denial fires the audit callback (R1.4).</summary>
public sealed class FilteredCapabilityRegistry(
    ICapabilityRegistry inner,
    IReadOnlySet<string> effective,
    Action<string>? onDenial = null) : ICapabilityRegistry
{
  private readonly ICapabilityRegistry _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly IReadOnlySet<string> _effective = effective ?? throw new ArgumentNullException(nameof(effective));
  private readonly Action<string>? _onDenial = onDenial;

  public Result<ResolvedCapability> Resolve(string nameOrRef)
  {
    ArgumentNullException.ThrowIfNull(nameOrRef);
    Result<ResolvedCapability> resolved = _inner.Resolve(nameOrRef);
    if (!resolved.IsSuccess)
    {
      return resolved; // genuinely unknown: inner's own UnknownAction error stands
    }

    if (_effective.Contains(resolved.Value.Action.Name))
    {
      return resolved;
    }

    _onDenial?.Invoke(resolved.Value.Action.Name);
    return Result.Failure<ResolvedCapability>(new DomainError("GrantViolation",
        "action '" + resolved.Value.Action.Name + "' is not granted to this agent."));
  }

  public IReadOnlyList<ProviderCapabilities> Providers => _inner.Providers;

  public Task<CapabilityInvocationResult> InvokeAsync(
      ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
      => _inner.InvokeAsync(capability, jsonArguments, ct);
}

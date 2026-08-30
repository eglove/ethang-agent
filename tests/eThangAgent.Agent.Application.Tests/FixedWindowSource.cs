using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Test window source: resolves every model to a fixed positive window.
/// Tests construct resolvers/spawners without wiring a source only to assert the
/// legacy no-source behavior; anything asserting model resolution wires this.</summary>
internal sealed class FixedWindowSource : IContextWindowSource
{
  public const int DefaultWindow = 128_000;

  public Task<int?> WindowForAsync(string modelId, string? providerName, CancellationToken ct = default)
      => Task.FromResult<int?>(DefaultWindow);
}

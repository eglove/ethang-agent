using eThangAgent.Agent.Application;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>The session's vision capability (task 19): answers from the ROOT AGENT HOLDER'S
///     current resolved ModelConfig at call time, so a model-picker change applies from the next
///     turn; before the first turn resolves a model, image input is conservatively false.
///     The holder is resolved LAZILY through the given service provider: the computer tool's
///     binding executes while the container is still constructing the agent-tools singleton, and
///     eagerly resolving the holder (whose IToolRegistry dependency reaches back through the exec
///     tool to the capability registry) would re-enter that in-flight singleton and deadlock.</summary>
public sealed class SessionImageInputCapability(IServiceProvider services) : IImageInputCapability
{
  private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

  /// <inheritdoc />
  public bool AcceptsImages
  {
    get
    {
      RootAgentHolder? holder = _services.GetService<RootAgentHolder>();
      return holder?.CurrentConfig?.AcceptsImageInput ?? false;
    }
  }
}

using eThangAgent.Agent.Application;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>The session's vision capability, answered in two steps: the RUNNING root agent's
///     resolved ModelConfig when one exists, else the session's own model selection through
///     <see cref="SessionVisionCache"/>, else false.
///     <para>THE SECOND STEP IS WHAT MAKES THE ANSWER AVAILABLE WHERE NO TURN RUNS. A
///     container that hands its turns to a conversation host never builds a root agent, so
///     the first step has nothing to read and the session's selection is the only statement
///     of what will serve. A container that does run turns keeps the first step, which is the
///     model the turn actually used.</para>
///     <para>Both lookups are LAZY through the given provider: the computer tool's binding
///     executes while the container is still constructing the agent-tools singleton, and
///     eagerly resolving the holder (whose IToolRegistry dependency reaches back through the
///     exec tool to the capability registry) would re-enter that in-flight singleton.</para>
///     <para>FALSE IS THE UNKNOWN ANSWER: before any model is resolved or selected, image
///     input is not claimed.</para></summary>
public sealed class SessionImageInputCapability(IServiceProvider services) : IImageInputCapability
{
  private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

  /// <inheritdoc />
  public bool AcceptsImages
  {
    get
    {
      RootAgentHolder? holder = _services.GetService<RootAgentHolder>();
      return holder?.CurrentConfig?.AcceptsImageInput
          ?? _services.GetService<SessionVisionCache>()?.Current
          ?? false;
    }
  }
}

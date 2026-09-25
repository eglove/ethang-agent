using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Bridges the application-layer invocation service to the Tool
///     Domain's invocation port (spec #28): the verbatim render lives in ONE
///     place (SkillInvocationService); this adapter only re-shapes the
///     outcome for the port's record. Composition wires it; no project
///     reference is added in the other direction.</summary>
public sealed class SkillInvocationPortAdapter(SkillInvocationService service) : ISkillInvocationPort
{
  private readonly SkillInvocationService _service = service ?? throw new ArgumentNullException(nameof(service));

  public async Task<Result<SkillInvocationPortResult>> InvokeAsync(string name, string? arguments, CancellationToken ct = default)
  {
    Result<SkillInvocation> r = await _service.InvokeAsync(name, arguments, ct).ConfigureAwait(false);
    return r.IsSuccess
        ? Result.Success(new SkillInvocationPortResult(r.Value.Name, r.Value.SystemLine, r.Value.BodyInlined, r.Value.SkipReason))
        : Result.Failure<SkillInvocationPortResult>(r.Error);
  }
}

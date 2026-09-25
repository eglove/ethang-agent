using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The user-invocation resolution port (spec #28): the one shared
///     core lives in the application layer (SkillInvocationService); tools
///     and hosts depend on THIS seam, never on the concrete service. No
///     project reference from Tool Domain to Agent Application is created —
///     composition wires the implementation in.</summary>
public interface ISkillInvocationPort
{
  Task<Result<SkillInvocationPortResult>> InvokeAsync(string name, string? arguments, CancellationToken ct = default);
}

/// <summary>The port's outcome record: mirrors the application service's
///     SkillInvocation shape without importing it.</summary>
public sealed record SkillInvocationPortResult(
    string Name,
    string SystemLine,
    bool BodyInlined,
    string? SkipReason);

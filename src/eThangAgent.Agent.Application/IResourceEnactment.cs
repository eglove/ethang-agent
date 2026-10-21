using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Enacts one ladder rung for one conversation (spec D4): the governor owns
///     the decision and the record, the owner of the resource owns the mechanism. A
///     failed enactment is a typed failure the governor records and reports - it never
///     stops the next tick.</summary>
public interface IResourceEnactment
{
  Task<Result<string>> EnactAsync(string conversationId, ResourceAction action, string reason, CancellationToken ct = default);
}

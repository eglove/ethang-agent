using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;/// <summary>Outcome of one successful terminal transition: the saved plan, the todo ids
///     the linked-todo cleanup actually removed, and the cleanup failure when the
///     transition landed but the cleanup could not (best-effort by contract - the plan
///     is already terminal, so failing the call would be a lie the caller cannot retry
///     out of). CleanupError is null on success; RemovedTodoIds is empty when no cleaner
///     is wired, no steps link todos, or the cleanup failed.</summary>
public sealed record PlanSetStatusResult(
  Plan Plan,
  IReadOnlyList<int> RemovedTodoIds,
  DomainError? CleanupError);

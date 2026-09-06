using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;/// <summary>Port the Plan Domain owns for removing the plan's linked todos from the
///     shared todo list. The composition root adapts the Tool Domain's todo document
///     (state key todo/list) to it - the domain depends only on this contract, never on
///     another context's types. Implementations return the ids they actually removed;
///     duplicates in the request are the implementation's concern to collapse.</summary>
public interface IPlanTodoCleaner
{
  /// <summary>Removes the given todo ids from the shared todo list, returning the ids
  ///     actually removed (duplicates collapsed, unknown ids ignored). Expected failures
  ///     flow as DomainErrors through Result - never exceptions.</summary>
  Task<Result<IReadOnlyList<int>>> RemoveLinkedAsync(IReadOnlyList<int> todoIds, CancellationToken ct = default);
}

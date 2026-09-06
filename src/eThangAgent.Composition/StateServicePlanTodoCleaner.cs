using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Composition adapter: removes a plan's linked todos from the shared todo
///     document held in the State Domain (the same todo/list key the todo tool serves).
///     Rewrites the document without the requested ids, deletes the key when nothing
///     remains (the todo tool treats a missing key as an empty document), and returns
///     the ids actually removed - duplicates in the request collapse through the set
///     membership test. A corrupt document fails closed with its StorageCorrupt error;
///     the key is left unchanged.</summary>
internal sealed class StateServicePlanTodoCleaner(IStateService state) : IPlanTodoCleaner
{
  public async Task<Result<IReadOnlyList<int>>> RemoveLinkedAsync(IReadOnlyList<int> todoIds,
      CancellationToken ct = default)
  {
    Result<string> read = await state.GetAsync(TodoTool.StoreKey, ct).ConfigureAwait(false);
    if (!read.IsSuccess)
    {
      return read.Error.Code == "KeyNotFound"
          ? Result.Success<IReadOnlyList<int>>([])
          : Result.Failure<IReadOnlyList<int>>(read.Error);
    }

    Result<IReadOnlyList<TodoItem>> parsed = TodoDocument.Parse(read.Value);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<int>>(parsed.Error);
    }

    HashSet<int> doomed = [.. todoIds];
    List<int> removed = [.. parsed.Value.Where(i => doomed.Contains(i.Id)).Select(i => i.Id)];
    List<TodoItem> remaining = [.. parsed.Value.Where(i => !doomed.Contains(i.Id))];

    if (remaining.Count == 0)
    {
      Result<string> deleted = await state.DeleteAsync(TodoTool.StoreKey, null, ct).ConfigureAwait(false);
      return deleted.IsSuccess || deleted.Error.Code == "KeyNotFound"
          ? Result.Success<IReadOnlyList<int>>(removed)
          : Result.Failure<IReadOnlyList<int>>(deleted.Error);
    }

    Result<StateKeyValue> written = await state.SetAsync(
        TodoTool.StoreKey, TodoDocument.Serialize(remaining), null, ct).ConfigureAwait(false);
    return written.IsSuccess
        ? Result.Success<IReadOnlyList<int>>(removed)
        : Result.Failure<IReadOnlyList<int>>(written.Error);
  }
}

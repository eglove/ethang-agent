using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

/// <summary>Composition-root adapter: exposes the State Domain's IStateService to the
///     todo tool through the Tool Domain's own ITodoListStore port. Tool.Domain cannot
///     reference State.Domain directly (State.Domain → Capability.Domain → Tool.Domain),
///     and the domain should not depend on another context's contract anyway; this
///     translation lives with the wiring, like the clarify channel adapters.</summary>
internal sealed class StateServiceTodoListStore(IStateService state) : ITodoListStore
{
    public Task<Result<string>> GetValueAsync(string key, CancellationToken ct = default) =>
        state.GetAsync(key, ct);

    public async Task<Result<int>> WriteValueAsync(string key, string value,
        int? expectedVersion, CancellationToken ct = default) =>
        (await state.SetAsync(key, value, expectedVersion, ct)).Map(kv => kv.Version);
}

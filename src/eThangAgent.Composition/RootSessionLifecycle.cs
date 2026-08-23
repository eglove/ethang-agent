using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Persists the root session around a turn loop: appends one completed exchange
///     (user then final assistant — the same Message instances the aggregate holds) and marks
///     the row Completed on graceful exit. Persistence failures surface via reportError;
///     the session continues. Semantics lifted verbatim from the CLI's Program helpers.</summary>
public sealed class RootSessionLifecycle(IAgentStore store)
{
    public async Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
        int messageCountBefore, Result<string> result, Action<string> reportError)
    {
        if (!result.IsSuccess) return;

        var user = await store.AppendMessageAsync(rootId, conversation.Messages[messageCountBefore]);
        if (!user.IsSuccess)
            reportError($"Error [{user.Error!.Code}]: {user.Error.Message}");

        var assistant = await store.AppendMessageAsync(rootId, conversation.Messages[^1]);
        if (!assistant.IsSuccess)
            reportError($"Error [{assistant.Error!.Code}]: {assistant.Error.Message}");
    }

    public async Task CompleteAsync(AgentId rootId, Action<string> reportError)
    {
        var record = await store.GetAsync(rootId);
        if (!record.IsSuccess || record.Value is null)
        {
            reportError(record.IsSuccess
                ? $"Error [NotFound]: root session {rootId} was not found."
                : $"Error [{record.Error!.Code}]: {record.Error.Message}");
            return;
        }

        var updated = await store.UpdateAsync(record.Value with
        {
            Status = AgentStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        if (!updated.IsSuccess)
            reportError($"Error [{updated.Error!.Code}]: {updated.Error.Message}");
    }
}

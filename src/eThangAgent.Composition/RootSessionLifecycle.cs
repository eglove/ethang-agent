using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Persists the root session around a turn loop: appends one completed exchange
///     (user then final assistant — the same Message instances the aggregate holds) and marks
///     the row Completed on graceful exit. Persistence failures surface via reportError;
///     the session continues. Semantics lifted verbatim from the CLI's Program helpers.</summary>
public class RootSessionLifecycle(IAgentStore store)
{
  public virtual async Task AppendExchangeAsync(AgentId rootId, Conversation conversation,
      int messageCountBefore, Result<string> result, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    ArgumentNullException.ThrowIfNull(result);
    ArgumentNullException.ThrowIfNull(reportError);
    if (!result.IsSuccess)
    {
      return;
    }

    Result<string> user = await store.AppendMessageAsync(rootId, conversation.Messages[messageCountBefore]).ConfigureAwait(false);
    if (!user.IsSuccess)
    {
      reportError($"Error [{user.Error!.Code}]: {user.Error.Message}");
    }

    Result<string> assistant = await store.AppendMessageAsync(rootId, conversation.Messages[^1]).ConfigureAwait(false);
    if (!assistant.IsSuccess)
    {
      reportError($"Error [{assistant.Error!.Code}]: {assistant.Error.Message}");
    }
  }

  public virtual async Task CompleteAsync(AgentId rootId, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(reportError);
    Result<AgentRecord> record = await store.GetAsync(rootId).ConfigureAwait(false);
    if (!record.IsSuccess || record.Value is null)
    {
      reportError(record.IsSuccess
          ? $"Error [NotFound]: root session {rootId} was not found."
          : $"Error [{record.Error!.Code}]: {record.Error.Message}");
      return;
    }

    Result<string> updated = await store.UpdateAsync(record.Value with
    {
      Status = AgentStatus.Completed,
      CompletedAt = DateTimeOffset.UtcNow,
    }).ConfigureAwait(false);
    if (!updated.IsSuccess)
    {
      reportError($"Error [{updated.Error!.Code}]: {updated.Error.Message}");
    }
  }
}

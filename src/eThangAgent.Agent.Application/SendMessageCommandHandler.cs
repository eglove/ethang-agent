using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application;

/// <summary>
/// Sends a user message through the agent loop and, after a SUCCESSFUL turn, offers the
/// nudge policy a chance to append a reminder line as a System message. Failed turns are
/// never nudged. Nudging is active only when the conversation, the policy, and the
/// memory-write counter are all supplied; with any of them absent the handler behaves
/// exactly as before (turns pass through, nothing is appended).
/// </summary>
/// <remarks>
/// Root model resolution: when a <see cref="RootAgentHolder"/> + <see cref="RootAgentResolver"/>
/// are supplied, each turn first resolves the model to serve (explicit pin, or intelligent
/// selection on the cadence boundary), rebuilds the root agent when the model changed, and
/// surfaces selection notices via the onNotice callback. Without the holder/resolver the
/// handler falls back to the single pre-built agent exactly as it did before this seam existed.
/// </remarks>
public class SendMessageCommandHandler(Ag? agent = null, Conversation? conversation = null,
    INudgePolicy? policy = null, Func<int>? memoriesWritten = null, IAgentInbox? inbox = null,
    RootAgentHolder? rootHolder = null, RootAgentResolver? rootResolver = null)
{
  private readonly Ag? _agent = agent;
  private readonly Conversation? _conversation = conversation;
  private readonly INudgePolicy? _nudgePolicy = policy;
  private readonly Func<int>? _memoriesWritten = memoriesWritten;
  private readonly IAgentInbox? _inbox = inbox;
  private readonly RootAgentHolder? _rootHolder = rootHolder;
  private readonly RootAgentResolver? _rootResolver = rootResolver;
  private int _turnCount;

  public async Task<Result<string>> Handle(SendMessageCommand command,
      Action<string>? onContentDelta = null,
      Action<string>? onReasoningDelta = null,
      Action? onIterationEnd = null,
      Action<string, string>? onToolCall = null,
      Action<string, string>? onToolResult = null,
      Action<string>? onNotice = null,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    int turnNumber = Interlocked.Increment(ref _turnCount);

    Ag active = await ResolveAgentAsync(command.Text, onNotice, ct).ConfigureAwait(false);

    Result<string> result = await active.SendMessage(command.Text,
        onContentDelta, onReasoningDelta, onIterationEnd, onToolCall, onToolResult, _inbox, ct).ConfigureAwait(false);
    if (!result.IsSuccess)
    {
      return result;
    }

    if (_conversation is not null && _nudgePolicy is not null && _memoriesWritten is not null)
    {
      string? line = _nudgePolicy.Evaluate(
          new NudgeContext(turnNumber, active.LastTurnToolCalls, _memoriesWritten()));
      if (line is not null)
      {
        _conversation.AddSystemMessage(line);
      }
    }

    return result;
  }

  /// <summary>Resolves the agent to serve this turn. When the root holder/resolver seam is
  ///     wired, runs pre-turn model resolution (cadence-based selection or explicit pin),
  ///     rebuilds the agent when the model changed, and surfaces any selection notice. Without
  ///     the seam, returns the single pre-built agent.</summary>
  private async Task<Ag> ResolveAgentAsync(string prompt, Action<string>? onNotice, CancellationToken ct)
  {
    if (_rootHolder is null || _rootResolver is null)
    {
      return _agent ?? throw new InvalidOperationException(
          "SendMessageCommandHandler has no agent and no root holder; one must be supplied.");
    }

    Conversation rootConversation = _conversation
        ?? throw new InvalidOperationException(
            "Root model resolution requires a conversation to count user messages.");

    (ModelConfig config, string? notice) = await _rootResolver.ResolveAsync(rootConversation, prompt, ct)
        .ConfigureAwait(false);
    if (notice is not null)
    {
      onNotice?.Invoke(notice);
    }

    return _rootHolder.Build(_rootHolder.Current, config);
  }
}

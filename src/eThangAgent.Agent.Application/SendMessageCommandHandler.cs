using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
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
public class SendMessageCommandHandler(Ag agent, Conversation? conversation = null,
    INudgePolicy? policy = null, Func<int>? memoriesWritten = null, IAgentInbox? inbox = null)
{
  private readonly Ag _agent = agent ?? throw new ArgumentNullException(nameof(agent));
  private readonly Conversation? _conversation = conversation;
  private readonly INudgePolicy? _nudgePolicy = policy;
  private readonly Func<int>? _memoriesWritten = memoriesWritten;
  private readonly IAgentInbox? _inbox = inbox;
  private int _turnCount;

  public async Task<Result<string>> Handle(SendMessageCommand command, CancellationToken ct = default,
      Action<string>? onContentDelta = null,
      Action<string>? onReasoningDelta = null,
      Action? onIterationEnd = null,
      Action<string, string>? onToolCall = null,
      Action<string, string>? onToolResult = null)
  {
    int turnNumber = Interlocked.Increment(ref _turnCount);

    Result<string> result = await _agent.SendMessage(command.Text, ct,
        onContentDelta, onReasoningDelta, onIterationEnd, onToolCall, onToolResult, _inbox);
    if (!result.IsSuccess)
    {
      return result;
    }

    if (_conversation is not null && _nudgePolicy is not null && _memoriesWritten is not null)
    {
      string? line = _nudgePolicy.Evaluate(
          new NudgeContext(turnNumber, _agent.LastTurnToolCalls, _memoriesWritten()));
      if (line is not null)
      {
        _conversation.AddSystemMessage(line);
      }
    }

    return result;
  }
}

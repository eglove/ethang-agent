using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.Agent.Application.Nudges;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>
/// Sends a user message through the agent loop and, after a SUCCESSFUL turn, offers the
/// nudge policy a chance to append a reminder line as a System message. Failed turns are
/// never nudged. Nudging is active only when the conversation, the policy, and the
/// memory-write counter are all supplied; with any of them absent the handler behaves
/// exactly as before (turns pass through, nothing is appended).
/// </summary>
public class SendMessageCommandHandler
{
    private readonly Ag _agent;
    private readonly Conversation? _conversation;
    private readonly INudgePolicy? _nudgePolicy;
    private readonly Func<int>? _memoriesWritten;
    private int _turnCount;

    public SendMessageCommandHandler(Ag agent, Conversation? conversation = null,
        INudgePolicy? policy = null, Func<int>? memoriesWritten = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _conversation = conversation;
        _nudgePolicy = policy;
        _memoriesWritten = memoriesWritten;
    }

    public async Task<Result<string>> Handle(SendMessageCommand command, CancellationToken ct = default)
    {
        var turnNumber = Interlocked.Increment(ref _turnCount);

        var result = await _agent.SendMessage(command.Text, ct);
        if (!result.IsSuccess)
            return result;

        if (_conversation is not null && _nudgePolicy is not null && _memoriesWritten is not null)
        {
            var line = _nudgePolicy.Evaluate(
                new NudgeContext(turnNumber, _agent.LastTurnToolCalls, _memoriesWritten()));
            if (line is not null)
                _conversation.AddSystemMessage(line);
        }

        return result;
    }
}

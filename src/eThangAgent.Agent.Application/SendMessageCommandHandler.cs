using eThangAgent.Agent.Domain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

public class SendMessageCommandHandler
{
    private readonly Agent _agent;

    public SendMessageCommandHandler(Agent agent)
        => _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public Task<Result<string>> Handle(SendMessageCommand command, CancellationToken ct = default)
        => _agent.SendMessage(command.Text, ct);
}

using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.ModelDomain;

public sealed record ModelRequest(IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition>? Tools = null);

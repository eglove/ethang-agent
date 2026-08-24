using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public class Agent
{
    /// <summary>Bounded auto-continuations per turn when a response ends with
    ///     <see cref="FinishReason.Length"/>.</summary>
    public const int DefaultMaxAutoContinuations = 8;

    /// <summary>Appended as a System message after a length-truncated assistant response.
    ///     Verbatim contract: the model must resume exactly where it stopped.</summary>
    public const string ContinuationPrompt =
        "[Your previous message was cut off by the output limit. Continue exactly where you stopped; do not repeat earlier text.]";
    private readonly IModelProvider _provider;
    private readonly IToolRegistry _tools;
    private readonly ISystemPromptProvider? _systemPrompt;
    private readonly int _maxAutoContinuations;

    // Auto-continuations used by the current turn; reset at SendMessage entry.
    private int _autoContinuations;

    public Conversation Conversation { get; }
    public ModelConfig Config { get; }

    /// <summary>Identity of this agent. Roots generate one on construction; spawned children carry their persisted id.</summary>
    public AgentId Id { get; }

    /// <summary>Depth in the spawn tree. Root agents are depth 0; children run at parent depth + 1.</summary>
    public int Depth { get; }

    /// <summary>Tool calls executed during the most recent SendMessage; 0 when the turn ended without any.</summary>
    public int LastTurnToolCalls { get; private set; }

    public Agent(IModelProvider provider, Conversation conversation, ModelConfig config,
        IToolRegistry tools, ISystemPromptProvider? systemPrompt = null,
        AgentId? id = null, int depth = 0, int maxAutoContinuations = DefaultMaxAutoContinuations)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _systemPrompt = systemPrompt;
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _maxAutoContinuations = maxAutoContinuations;
        Id = id ?? AgentId.NewId();
        Depth = depth;
    }

    /// <summary>
    /// Runs one user turn through the provider/tool loop. Content deltas stream out through
    /// <paramref name="onContentDelta"/> exactly as the provider emits them — every iteration,
    /// interstitial text between tool calls included — and <paramref name="onIterationEnd"/>
    /// fires once after each provider response so observers can separate iterations. Both are
    /// optional: providers without streaming support simply never invoke the delta callback,
    /// and the returned result is identical either way. Callbacks may fire on arbitrary
    /// threads; observers must marshal to their own context.
    /// </summary>
    public async Task<Result<string>> SendMessage(string text, CancellationToken ct = default,
        Action<string>? onContentDelta = null,
        Action<string>? onReasoningDelta = null,
        Action? onIterationEnd = null,
        Action<string, string>? onToolCall = null,
        Action<string, string>? onToolResult = null)
    {
        LastTurnToolCalls = 0;
        _autoContinuations = 0;
        Conversation.AddUserMessage(text);
        // No iteration cap by design: the loop runs until the model answers without
        // tool calls. Termination is the model's job — but cancellation is checked
        // every round, because nothing else in the loop is obliged to observe ct
        // (fakes, cached providers, and instant tools may never see it).
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var request = new ModelRequest(
                Conversation.Messages, _tools.Definitions, _systemPrompt?.Build());
            var result = await _provider.SendStreamingAsync(Config, request,
                onContentDelta, onReasoningDelta, ct);
            if (!result.IsSuccess)
                return Result<string>.Failure(result.Error!);
            onIterationEnd?.Invoke();

            var response = result.Value!;
            if (response.ToolCalls.Count == 0)
            {
                var content = response.Content ?? "";

                // A length-truncated answer is not a final answer. Keep the partial
                // message in history, nudge the model to continue where it stopped,
                // and loop — bounded so a pathological model cannot spin forever.
                // (Named decision: auto-continuation is leniency with a visible cap,
                // never a silent retry.)
                if (response.FinishReason is FinishReason.Length)
                {
                    if (_autoContinuations >= _maxAutoContinuations)
                        return Result<string>.Failure(new Error("MaxOutputContinuations",
                            $"Output limit reached {_maxAutoContinuations + 1} times without a complete answer."));
                    _autoContinuations++;
                    Conversation.AddAssistantMessage(content);
                    Conversation.AddSystemMessage(ContinuationPrompt);
                    continue;
                }

                Conversation.AddAssistantMessage(content);
                return Result<string>.Success(content);
            }

            Conversation.AddAssistantMessage(response.Content ?? "",
                response.ToolCalls
                    .Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments))
                    .ToList());

            foreach (var call in response.ToolCalls)
            {
                LastTurnToolCalls++;
                onToolCall?.Invoke(call.Name, call.Arguments);
                var tool = _tools.Find(call.Name);
                var toolResult = tool is null
                    ? new ToolResult($"Error [UnknownTool]: Unknown tool: {call.Name}.", true)
                    : await tool.ExecuteAsync(new RawToolInput(call.Name, call.Arguments), ct);
                Conversation.AddToolResult(call.Id, toolResult.Content);
                var summary = toolResult.IsError
                    ? (toolResult.Content.Length > 80 ? toolResult.Content[..77] + "…" : toolResult.Content)
                    : "ok";
                onToolResult?.Invoke(call.Name, summary);
            }
        }
    }
}

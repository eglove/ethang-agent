using eThangAgent.Agent.Application.Nudges;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Verification;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application;

/// <summary>The root-resolution seam, grouped: the holder that rebuilds the root agent
///     when its model changes and the resolver that picks the model to serve each turn.
///     Both are wired together — one without the other has no meaning.</summary>
public sealed record RootTurnServices(RootAgentHolder Holder, RootAgentResolver Resolver);

/// <summary>The verification-gate collaborators, grouped: the gate deciding when a
///     nudge is due and the resolver of the files changed since the last verification
///     run. Both are wired together — one without the other has no meaning.</summary>
public sealed record VerificationCollaborators(
    VerificationGate Gate,
    Func<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>> ChangedFilesResolver);

/// <summary>
/// Sends a user message through the agent loop and, after a SUCCESSFUL turn, offers the
/// nudge policy a chance to append a reminder line as a System message. Failed turns are
/// never nudged. Nudging is active only when the conversation, the policy, and the
/// memory-write counter are all supplied; with any of them absent the handler behaves
/// exactly as before (turns pass through, nothing is appended).
/// </summary>
/// <remarks>
/// Root model resolution: when the <see cref="RootTurnServices"/> seam is supplied, each
/// turn first resolves the model to serve (the user's live choice, the session pin, or the
/// fallback), rebuilds the root agent when the model changed, and surfaces resolution
/// notices via the onNotice callback. Without the seam the handler falls back to the
/// single pre-built agent exactly as it did before this seam existed.
/// </remarks>
public class SendMessageCommandHandler(
    Ag? agent = null,
    Conversation? conversation = null,
    INudgePolicy? policy = null,
    Func<int>? memoriesWritten = null,
    IAgentInbox? inbox = null,
    RootTurnServices? rootTurn = null,
    VerificationCollaborators? verification = null)
{
  private readonly Ag? _agent = agent;
  private readonly Conversation? _conversation = conversation;
  private readonly INudgePolicy? _nudgePolicy = policy;
  private readonly Func<int>? _memoriesWritten = memoriesWritten;
  private readonly IAgentInbox? _inbox = inbox;
  private readonly RootTurnServices? _rootTurn = rootTurn;
  private readonly VerificationCollaborators? _verification = verification;
  private int _turnCount;

  public async Task<Result<string>> Handle(SendMessageCommand command,
      TurnCallbacks? callbacks = null,
      Action<string>? onNotice = null,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    int turnNumber = Interlocked.Increment(ref _turnCount);

    Ag active;
    try
    {
      active = await ResolveAgentAsync(onNotice, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // Same contract as the loop's own cancellation path (Agent.SendMessage): a budget
      // or interrupt firing during pre-turn model resolution fails the turn as
      // TurnCancelled — it must not escape as a raw exception across the
      // Task<Result<string>> seam.
      return Result.Failure<string>(new DomainError(Ag.TurnCancelledCode, RuntimeErrors.TurnCancelled));
    }

    Result<string> result = await active.SendMessage(command.Text, callbacks, _inbox, command.ImageParts, ct)
        .ConfigureAwait(false);
    if (!result.IsSuccess)
    {
      return result;
    }

    ApplyNudge(turnNumber, active, callbacks);
    ApplyVerificationGate(callbacks);
    return result;
  }

  /// <summary>After a SUCCESSFUL turn, offers the nudge policy a chance to append a
  ///     reminder line as a System message. Active only when the conversation, the
  ///     policy, and the memory-write counter are all supplied.</summary>
  private void ApplyNudge(int turnNumber, Ag active, TurnCallbacks? callbacks)
  {
    if (_conversation is null || _nudgePolicy is null || _memoriesWritten is null)
    {
      return;
    }

    string? line = _nudgePolicy.Evaluate(
        new NudgeContext(turnNumber, active.LastTurnToolCalls, _memoriesWritten()));
    if (line is null)
    {
      return;
    }

    _conversation.AddSystemMessage(line);
    callbacks?.OnSystemMessage?.Invoke(line);
  }

  /// <summary>Gate B (spec #24): one bounded verification nudge per turn when files
  ///     changed without a fresh verification run. Both collaborators must be present;
  ///     absent means byte-identical legacy behavior.</summary>
  private void ApplyVerificationGate(TurnCallbacks? callbacks)
  {
    if (_verification is not { } collaborators || collaborators.Gate is not { Enabled: true } gate)
    {
      return;
    }

    string? nudge = gate.NudgeLine(collaborators.ChangedFilesResolver());
    if (nudge is null || _conversation is null)
    {
      return;
    }

    _conversation.AddSystemMessage(nudge);
    callbacks?.OnSystemMessage?.Invoke(nudge);
  }

  /// <summary>Resolves the agent to serve this turn. When the root holder/resolver seam is
  ///     wired, runs pre-turn model resolution (the user's live choice, the session pin, or
  ///     the fallback), rebuilds the agent when the model changed, and surfaces any notice.
  ///     Without the seam, returns the single pre-built agent.</summary>
  private async Task<Ag> ResolveAgentAsync(Action<string>? onNotice, CancellationToken ct)
  {
    if (_rootTurn is not { } services)
    {
      return _agent ?? throw new InvalidOperationException(
          "SendMessageCommandHandler has no agent and no root holder; one must be supplied.");
    }

    (ModelConfig config, string? notice) = await services.Resolver.ResolveAsync(ct)
        .ConfigureAwait(false);
    if (notice is not null)
    {
      onNotice?.Invoke(notice);
    }

    return services.Holder.Build(services.Holder.Current, config);
  }
}

using System.Diagnostics;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>One live conversation host: the conversation it serves, and the process
///     whose working set is that conversation's memory.</summary>
internal sealed record ResourceHostHandle(string ConversationId, int ProcessId);

/// <summary>Where the app observes each conversation's memory from (spec D5/D14).
///     Every conversation runs in its OWN host process (spec D6), so a conversation's
///     memory is its host's working set - the number that actually bounds it.
///     <para>The app process itself is observed too: the shell, the exec engine, and the
///     Roslyn hosts live in-process, so the app's own working set is part of the
///     app-wide budget and would otherwise be invisible to the indicator. It is
///     reported SEPARATELY, through <see cref="AppUsageBytes"/>: the governor picks
///     the largest OBSERVATION as its target and enacts a rung against it, so an
///     'app' row presented as a conversation is a decision about a conversation that
///     does not exist - it produced an audit row naming 'conversation app' and a
///     refusal, every tick, for the app's own memory.</para>
///     <para>Program ids are the host's process id - the handle the enactment side
///     understands. The governor never inspects processes itself.</para></summary>
internal sealed class ResourceHostObservation : IResourceObservationSource
{
  private readonly Func<IReadOnlyList<ResourceHostHandle>> _hosts;

  public ResourceHostObservation(Func<IReadOnlyList<ResourceHostHandle>> hosts)
  {
    ArgumentNullException.ThrowIfNull(hosts);
    _hosts = hosts;
  }

  /// <summary>The app process's own consumer id. It is NOT a conversation: the
  ///     governor never sees it as one, so it can never be its target.</summary>
  public const string AppConsumerId = "app";

  /// <summary>The app process's own working set, in bytes, as of the last
  ///     observation. It is part of the app-wide budget and is shown in the
  ///     indicator, but it is never a governor target.</summary>
  public long AppUsageBytes { get; private set; }

  /// <summary>The observations the LAST tick was derived from. The indicator renders
  ///     exactly these, so the bar and the governor's decision always describe the
  ///     same sample - never two samples taken a moment apart.</summary>
  public IReadOnlyList<ResourceObservation> LastObserved { get; private set; } = [];

  /// <summary>Observes every open conversation's host. A host that has exited
  ///     contributes nothing: a dead process holds no memory worth counting, and its
  ///     working set would only confuse the ranking.
  ///     <para>The app process is measured too, but reported through
  ///     <see cref="AppUsageBytes"/> rather than as a row - see the class note.</para></summary>
  public IReadOnlyList<ResourceObservation> Observe()
  {
    List<ResourceObservation> observations = [];
    foreach (ResourceHostHandle host in _hosts())
    {
      observations.Add(new ResourceObservation(host.ConversationId, WorkingSetOf(host.ProcessId),
          [host.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
    }

    AppUsageBytes = WorkingSetOf(Environment.ProcessId);
    LastObserved = observations;
    return observations;
  }

  /// <summary>The working set of one process, in bytes. A process that has exited, or
  ///     one this account may not inspect, contributes zero - never a fabricated
  ///     number and never a thrown fault (the governor's tick must survive it).</summary>
  private static long WorkingSetOf(int processId)
  {
    try
    {
      using Process process = Process.GetProcessById(processId);
      return process.WorkingSet64;
    }
    catch (ArgumentException)
    {
      // The process exited between the handle list and the read.
      return 0;
    }
    catch (InvalidOperationException)
    {
      // The handle names no process this account can inspect.
      return 0;
    }
  }
}

/// <summary>Enacts the governor's ladder rungs over the app's OWN resources (spec
///     D3/D4). The app owns the conversation hosts (it launches and supervises them),
///     so a rung that acts on a process is real; a rung the app cannot honestly
///     perform is REFUSED with a named error, and the governor records that refusal
///     in the audit trail - the trail never claims an action that did not happen.
///     <para>Steering is refused on purpose: the conversation's model runs in its host
///     process (spec D6), and the app has no wire path to the host's model mailbox, so
///     an app-side steering message would be a no-op dressed as an action.</para>
///     <para>KillProgram is a MISNOMER in production: it stops the whole conversation
///     HOST process, not one program inside it. The domain's per-program budget
///     (PerSpawnBytes) is not what this rung measures, so the rung is RENAMED in
///     the user-facing vocabulary ("stop the conversation's host process"); the
///     domain enum keeps its spec name, and the observation's ProgramIds carry the
///     host pid only - the per-program breakdown is a later increment.</para></summary>
internal sealed class ResourceHostEnactment : IResourceEnactment
{
  private readonly Func<string, ResourceHostRegistration?> _resolve;

  public ResourceHostEnactment(Func<string, ResourceHostRegistration?> resolve)
  {
    ArgumentNullException.ThrowIfNull(resolve);
    _resolve = resolve;
  }

  /// <inheritdoc/>
  public Task<Result<string>> EnactAsync(string conversationId, ResourceAction action, string reason,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
    ResourceHostRegistration? registration = _resolve(conversationId);
    if (registration is null)
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("ConversationNotOpen",
          $"conversation '{conversationId}' is not open; nothing to act on.")));
    }

    switch (action)
    {
      case ResourceAction.Steer:
        return Task.FromResult(Refused("steer the conversation",
            "the conversation's model runs in its host process; the app cannot deliver a steering message to it."));
      case ResourceAction.KillProgram:
        if (registration.Supervisor is null)
        {
          return Task.FromResult(Refused("stop the conversation's host process",
              "the conversation has no host process to stop."));
        }

        registration.Supervisor.StopHost();
        return Task.FromResult(Result.Success("stopped the conversation's host process."));
      case ResourceAction.ReclaimMemory:
        return Task.FromResult(TrimWorkingSetOf(registration.HostProcessId));
      case ResourceAction.RestartConversation:
        if (registration.Supervisor is null)
        {
          return Task.FromResult(Refused("restart the conversation",
              "the conversation has no host process to restart."));
        }

        registration.Supervisor.StopHost();
        return Task.FromResult(Result.Success("stopped the conversation's host process; it relaunches on the next attach."));
      case ResourceAction.RestartApp:
        return Task.FromResult(Refused("restart the app",
            "the app does not restart itself; that is the operator's call."));
      case ResourceAction.None:
        return Task.FromResult(Result.Success("nothing to do."));
      default:
        return Task.FromResult(Refused("act on the conversation", "this rung is not known to the app."));
    }
  }

  /// <summary>A refusal that names itself, so the audit trail reads as an honest
  ///     record of a decision the app could not carry out. The rung is named in the
  ///     reader's language, never as the internal enum name.</summary>
  private static Result<string> Refused(string rung, string why)
      => Result.Failure<string>(new DomainError("RungRefused",
          $"the app refused to {rung}: {why}"));

  /// <summary>Trims the TARGET CONVERSATION's host process working set. The OS pages
  ///     the pages back in on demand, so this is safe and reversible. The GC's own
  ///     collection is NOT forced: forcing it is a process-wide side effect the
  ///     analyzer rightly refuses, and trimming the working set is the reclaim that
  ///     actually returns pages.
  ///     <para>The rung is keyed by a CONVERSATION, so the effect must land on that
  ///     conversation's process. Trimming the APP's working set instead was a target
  ///     and an effect that disagreed - and the app's memory is never what the
  ///     governor decided about.</para></summary>
  private static Result<string> TrimWorkingSetOf(int? hostProcessId)
  {
    if (hostProcessId is not { } processId)
    {
      return Result.Failure<string>(new DomainError("ReclaimFailed",
          "the conversation has no host process to reclaim."));
    }

    try
    {
      using Process host = Process.GetProcessById(processId);
      host.MinWorkingSet = 0;
      host.MaxWorkingSet = 0;
      return Result.Success("trimmed the conversation's host process working set.");
    }
    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
        or ArgumentException)
    {
      return Result.Failure<string>(new DomainError("ReclaimFailed",
          $"the app could not trim the conversation's working set: {ex.Message}"));
    }
  }
}

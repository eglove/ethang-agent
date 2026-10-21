using System.ComponentModel;
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
    catch (Win32Exception)
    {
      // The process exists but this account may not open it.
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
///     host pid only - the per-program breakdown is a later increment.</para>
///     <para>NEVER THROWS: an enactment is the OUTCOME of a rung, and a rung that
///     cannot be carried out - a host that cannot be inspected, a stop that is denied,
///     a working set that cannot be trimmed - is a NAMED failure the governor records
///     as a refusal row. An exception escaping this seam would instead be swallowed
///     by the governor loop's broad catch, so the tick would vanish and the audit
///     trail would show NOTHING: the user would never learn that the app decided to
///     act and failed to.</para></summary>
internal sealed class ResourceHostEnactment : IResourceEnactment
{
  private readonly Func<string, ResourceHostRegistration?> _resolve;
  private readonly Func<int, IWorkingSetLimits> _workingSets;

  /// <summary>Production wiring: the working-set seam opens the real process.</summary>
  public ResourceHostEnactment(Func<string, ResourceHostRegistration?> resolve)
      : this(resolve, OpenProcessWorkingSet)
  {
  }

  /// <summary>Testable wiring: the working-set seam is injected, so the reclaim's
  ///     OS-failure paths (a process this account may not inspect, a process that has
  ///     exited) are drivable without hunting the machine for a process to fail
  ///     against - and the reclaim's SUCCESS is provable without a real page
  ///     allocation.</summary>
  internal ResourceHostEnactment(Func<string, ResourceHostRegistration?> resolve,
      Func<int, IWorkingSetLimits> workingSets)
  {
    ArgumentNullException.ThrowIfNull(resolve);
    ArgumentNullException.ThrowIfNull(workingSets);
    _resolve = resolve;
    _workingSets = workingSets;
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

    try
    {
      return Task.FromResult(Enact(registration, action));
    }
    // Named decision (CA1031): this seam is the boundary between the governor's
    // decision and the machine's answer. A fault here is an expected outcome of a
    // rung, not a programming error, and it must become a NAMED failure the governor
    // records - never an exception that escapes into the tick and leaves the audit
    // trail empty.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return Task.FromResult(Result.Failure<string>(new DomainError("RungFailed",
          $"the app could not {ResourceRungPhrases.Of(action)}: {ex.Message}")));
    }
  }

  /// <summary>One rung, carried out as far as the app honestly can.</summary>
  private Result<string> Enact(ResourceHostRegistration registration, ResourceAction action)
  {
    switch (action)
    {
      case ResourceAction.Steer:
        return Refused(action,
            "the conversation's model runs in its host process; the app cannot deliver a steering message to it.");
      case ResourceAction.KillProgram:
        if (registration.StopHost is not { } kill)
        {
          return Refused(action, "the conversation has no host process to stop.");
        }

        kill();
        return Result.Success("stopped the conversation's host process.");
      case ResourceAction.ReclaimMemory:
        return TrimWorkingSetOf(registration.HostProcessId);
      case ResourceAction.RestartConversation:
        if (registration.StopHost is not { } restart)
        {
          return Refused(action, "the conversation has no host process to restart.");
        }

        restart();
        return Result.Success("stopped the conversation's host process; it relaunches on the next attach.");
      case ResourceAction.RestartApp:
        return Refused(action, "the app does not restart itself; that is the operator's call.");
      case ResourceAction.None:
        return Result.Success("nothing to do.");
      default:
        return Refused(action, "this rung is not known to the app.");
    }
  }

  /// <summary>A refusal that names itself, so the audit trail reads as an honest
  ///     record of a decision the app could not carry out. The rung is named in the
  ///     reader's language, never as the internal enum name.</summary>
  private static Result<string> Refused(ResourceAction action, string why)
      => Result.Failure<string>(new DomainError("RungRefused",
          $"the app refused to {ResourceRungPhrases.Of(action)}: {why}"));

  /// <summary>The floor the reclaim sets while it releases the host's pages. Windows
  ///     trims a process's resident set when its working-set limits are lowered - this
  ///     is the step that actually returns the memory. Zero is NOT usable: the OS
  ///     rejects a zero minimum with ERROR_INVALID_PARAMETER (a Win32Exception), and
  ///     a zero MAXIMUM is rejected outright, so the old "min = 0; max = 0" recipe could
  ///     only ever throw - a reclaim that always throws is a reclaim that never
  ///     happens.</summary>
  internal const int TrimMinimumBytes = 64 * 1024;

  /// <summary>The ceiling the reclaim sets. The OS trims the resident set down to the
  ///     CURRENT maximum working set, so the ceiling is the step that returns the
  ///     bulk of the pages.</summary>
  internal const int TrimMaximumBytes = 1024 * 1024;

  /// <summary>Trims the TARGET CONVERSATION's host process working set. The OS pages
  ///     the pages back in on demand, so this is safe and reversible. The GC's own
  ///     collection is NOT forced: forcing it is a process-wide side effect the
  ///     analyzer rightly refuses, and trimming the working set is the reclaim that
  ///     actually returns pages.
  ///     <para>The rung is keyed by a CONVERSATION, so the effect must land on that
  ///     conversation's process. Trimming the APP's working set instead was a target
  ///     and an effect that disagreed - and the app's memory is never what the
  ///     governor decided about.</para>
  ///     <para>NEVER THROWS: a host that has exited, a host this account may not
  ///     inspect, and a host whose limits the OS rejects all become a NAMED
  ///     ReclaimFailed, which the governor records as a refusal row. The Win32
  ///     failure the OS raises for an untouchable process is the one that used to
  ///     escape the whole tick.</para></summary>
  private Result<string> TrimWorkingSetOf(int? hostProcessId)
  {
    if (hostProcessId is not { } processId)
    {
      return Result.Failure<string>(new DomainError("ReclaimFailed",
          "the conversation has no host process to reclaim."));
    }

    try
    {
      using IWorkingSetLimits limits = _workingSets(processId);
      nint originalMin = limits.Minimum;
      nint originalMax = limits.Maximum;

      try
      {
        // The floor FIRST: the OS refuses a maximum below the current minimum, so a
        // host whose minimum is already above the reclaim's ceiling would reject the
        // maximum outright.
        limits.Minimum = TrimFloorFor(originalMax);
        limits.Maximum = TrimCeilingFor(originalMax);
      }
      finally
      {
        RestoreWorkingSetLimits(limits, originalMin, originalMax);
      }

      return Result.Success("trimmed the conversation's host process working set.");
    }
    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
        or ArgumentException or Win32Exception)
    {
      return Result.Failure<string>(new DomainError("ReclaimFailed",
          $"the app could not trim the conversation's working set: {ex.Message}"));
    }
  }

  /// <summary>The floor the reclaim sets: the reclaim's own floor, never above the
  ///     ceiling the host is about to be given (the OS refuses a minimum above the
  ///     maximum).</summary>
  private static nint TrimFloorFor(nint originalMax)
      => (nint)Math.Min(TrimMinimumBytes, (long)originalMax);

  /// <summary>The ceiling the reclaim sets: the reclaim's own ceiling, never above the
  ///     host's own maximum (raising the ceiling would GROW the host's allowance) and
  ///     never below the floor the OS insists on.</summary>
  private static nint TrimCeilingFor(nint originalMax)
      => (nint)Math.Min(TrimMaximumBytes, Math.Max(TrimMinimumBytes, (long)originalMax));

  /// <summary>Restores the host's own working-set limits. The maximum goes first: the
  ///     original minimum can be larger than the reclaim's ceiling, and the OS refuses a
  ///     maximum below the minimum. Best effort: a host that has exited has nothing to
  ///     restore, and a restore that fails must never mask the reclaim's own outcome.</summary>
  private static void RestoreWorkingSetLimits(IWorkingSetLimits limits, nint originalMin, nint originalMax)
  {
    try
    {
      limits.Maximum = originalMax;
      limits.Minimum = originalMin;
    }
    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
        or ArgumentException or Win32Exception)
    {
      _ = ex;
    }
  }

  /// <summary>Opens the real process for a working-set write. The OS's failure modes
  ///     surface here as the framework's own exceptions, which the reclaim turns into a
  ///     NAMED failure.</summary>
  private static IWorkingSetLimits OpenProcessWorkingSet(int processId)
      => new ProcessWorkingSetLimits(Process.GetProcessById(processId));
}

/// <summary>The working-set limits of one foreign process, and nothing else. A SEAM:
///     the reclaim is the only place the app writes to a process it did not start, and
///     the OS's failure modes - a process this account may not inspect (a Win32
///     failure), a process that has exited - must be drivable in a test without
///     hunting the machine for a process to fail against. The production
///     implementation is the real <see cref="Process"/>; the domain knows nothing
///     about it.</summary>
internal interface IWorkingSetLimits : IDisposable
{
  /// <summary>The process's minimum working set, in bytes.</summary>
  nint Minimum { get; set; }

  /// <summary>The process's maximum working set, in bytes.</summary>
  nint Maximum { get; set; }
}

/// <summary>The production working-set seam: the real process, read and written
///     through the framework's own working-set properties.</summary>
internal sealed class ProcessWorkingSetLimits(Process process) : IWorkingSetLimits
{
  public nint Minimum
  {
    get => process.MinWorkingSet;
    set => process.MinWorkingSet = value;
  }

  public nint Maximum
  {
    get => process.MaxWorkingSet;
    set => process.MaxWorkingSet = value;
  }

  public void Dispose() => process.Dispose();
}

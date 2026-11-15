using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Lsp.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>One opened agent session: an isolated slice of the composed core
///     rooted at a single workspace directory. Sessions share nothing mutable —
///     conversation, path resolution, and workspace identity are
///     per-session — so several can run concurrently inside one process (one per
///     open agent tab in the desktop shell). The SQLite database IS shared by
///     design: rows are keyed by workspace id. The session is wired for exactly
///     one AI provider (<see cref="ProviderName"/>) for its whole lifetime.</summary>
public sealed record AgentSession(
    ServiceProvider Services,
    AgentId RootId,
    Conversation Conversation,
    SendMessageCommandHandler Handler,
    RootSessionLifecycle Lifecycle,
    ModelConfig Model,
    string WorkspaceRoot,
    string ProviderName,
    IAgentInbox Inbox,
    IAgentRuntime ChildRuntime,
    SessionModelPreferences? Preferences = null)
{
  public string ModelId => Model.ModelId;

  /// <summary>This session's hold on the workspace's shared watcher, when the host wired one:
  ///     a subscription surface, not the watcher. Container services that need announcements
  ///     subscribe through it, so every subscriber the session made is dropped when the
  ///     container's hold goes back - a watcher the next session over the same root keeps
  ///     running must not keep calling a closed session (issue #388).</summary>
  public IFileWatcherSubscription? WorkspaceWatcher { get; init; }

  /// <summary>The session's rendered system prompt - the verbatim text every provider
  ///     call sends (skills bootstrap, persona, guides, configured session files).
  ///     Surfaces carry it to the user so nothing the agent receives is hidden.
  ///     Rendered once at session creation from the container's prompt providers.</summary>
  public string SystemPrompt { get; init; } = string.Empty;

  /// <summary>Runs ! commands (user-side shell utilities) against this session's
  ///     workspace: persists the run, appends the system message. Null when the host
  ///     did not wire shell access (headless stubs).</summary>
  public IUserCommandRunner? CommandRunner { get; init; }

  /// <summary>This container's language-server status (plan #143 step 6): what the daemon
  ///     announced it is doing with the server, so the status bar can show a first-run
  ///     download. Null when the container wired no daemon host (a bare test stub).
  ///     <para>THE SAME INSTANCE THE CONTAINER'S TOOLS USE: the daemon host is a singleton,
  ///     so the announcements the model's symbol queries trigger land on the monitor the
  ///     status bar is following.</para></summary>
  public LspServerStatusMonitor? LspServerStatus { get; init; }

  /// <summary>The shared skill invocation core (spec #28): the Desktop's slash
  ///     input resolves through it exactly as the skill_invoke tool does. Null
  ///     when the host did not wire the skill catalog (headless stubs).</summary>
  public SkillInvocationService? SkillInvocation { get; init; }

  /// <summary>Sink for out-of-band session notices (host health, orphan repair, skill
  ///     reload announcements), populated by the host UI after the session is
  ///     constructed: the VM owns the transcript, the session does not. Null = notices
  ///     are dropped (headless hosts).
  ///     <para>Setting this ALSO fills the container's <see cref="SessionNoticeSink"/>
  ///     (issue #269): container services post from background paths and cannot resolve
  ///     this session (it is registered in no container), so the carrier is the seam they
  ///     reach. One sink, two doors. A container without the carrier (a bare test stub)
  ///     keeps the session-local sink working exactly as before.</para>
  ///     Thread-safe by contract: invoked from background supervisory paths.</summary>
  public Action<string>? NoticeSink
  {
    get;
    set
    {
      field = value;
      if (Services.GetService(typeof(SessionNoticeSink)) is SessionNoticeSink carrier)
      {
        carrier.Sink = value;
      }
    }
  }

  /// <summary>Notices produced while the session was being BUILT, before any sink existed
  ///     (issue #317 D5/D6: a workspace that moved or was copied says so once, at open).
  ///     <para>They cannot be posted at the moment they are discovered - the sink is attached by
  ///     the host UI AFTER the session is constructed - so they are carried here and delivered by
  ///     <see cref="DeliverPendingNotices"/> once a sink exists. A notice nobody delivered is a
  ///     notice nobody saw, which for a moved workspace means the user never learns why their
  ///     chains are where they are.</para></summary>
  public IReadOnlyList<string> PendingNotices { get; internal set; } = [];

  /// <summary>Posts one out-of-band notice to the session surface.</summary>
  public void PostNotice(string message) => NoticeSink?.Invoke(message);

  /// <summary>Disposes this session's container exactly once, whoever asks first.
  ///     <para>THE CONTAINER IS THE HOST PROCESS'S OWNER: disposing it kills the conversation
  ///     host this session launched. Two callers can reach that act for one session - the
  ///     shell that retires a tab's cell, and the shell's app-wide shutdown - so the act is
  ///     idempotent here rather than repeated there: a second disposal is a no-op instead of a
  ///     read through a disposed provider.</para>
  ///     <para>THE SINK IS RELEASED FIRST, while the provider is still alive to be asked for the
  ///     carrier. The host UI attaches a closure that captures its session view-model and tab,
  ///     so a container that keeps the sink keeps the whole surface behind it reachable - and,
  ///     because the carrier lives inside the container, anything still holding a resolved
  ///     service keeps the UI graph too. Disposing without releasing it left every closed
  ///     session's container pointing at a shell that had forgotten it (issue #388).</para>
  ///     </summary>
  /// <returns>True when this call performed the disposal, false when it had already run.</returns>
  public async Task<bool> TryDisposeContainerAsync()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
      return false;
    }

    NoticeSink = null;
    PendingNotices = [];
    await Services.DisposeAsync().ConfigureAwait(false);
    return true;
  }

  private int _disposed;

  /// <summary>Delivers every notice held from construction, then clears the list so a second
  ///     call cannot repeat them. Safe with no sink: the notices are KEPT (not dropped) so a
  ///     later delivery still shows them.</summary>
  public void DeliverPendingNotices()
  {
    if (NoticeSink is null || PendingNotices.Count == 0)
    {
      return;
    }

    foreach (string notice in PendingNotices)
    {
      NoticeSink(notice);
    }

    PendingNotices = [];
  }
}

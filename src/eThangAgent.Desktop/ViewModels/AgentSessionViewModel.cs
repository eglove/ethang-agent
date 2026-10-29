using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.ModelDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Construction options for <see cref="AgentSessionViewModel"/>. The workspace
///     root is required identity; every other member is an optional seam whose null
///     keeps the built-in default exactly as the former absent parameter did::
///     a calling-thread stream sink, no inbox (steering rejected
///     with a notice), no child runtime (stop never interrupts children), no status
///     model updater, and no model preferences (pickers report unavailable).</summary>
internal sealed record AgentSessionViewModelOptions
{
  /// <summary>The workspace directory this agent works from (tab subtitle and title).</summary>
  public required string WorkspaceRoot { get; init; }

  /// <summary>Applies stream events; default applies on the calling thread (adequate
  ///     for unit tests — production passes UI-thread marshaling).</summary>
  public Func<UiStreamEvent, Task>? UiStreamSink { get; init; }

  /// <summary>The session steering inbox.</summary>
  public IAgentInbox? Inbox { get; init; }

  /// <summary>The session's child-agent runtime.</summary>
  public IAgentRuntime? ChildRuntime { get; init; }

  /// <summary>Updates the status bar when root model resolution picks a model.</summary>
  public Action<string>? StatusModelUpdater { get; init; }

  /// <summary>The session's live model/effort preferences backing the Model and Effort
  ///     pickers.</summary>
  public SessionModelPreferences? ModelPreferences { get; init; }

  /// <summary>Runs ! commands (user-side shell utilities). Null = the surface reports
  ///     commands unavailable.</summary>
  public IUserCommandRunner? CommandRunner { get; init; }

  /// <summary>The session's shared skill invocation core (spec #28): slash submit
  ///     resolves through the same object the skill_invoke tool uses. Null = the
  ///     slash surface reports unavailable.</summary>
  public SkillInvocationService? SkillInvocation { get; init; }

  /// <summary>The autocomplete popup's catalog source. Null = no popup (headless
  ///     stubs without the skill catalog).</summary>
  public Func<IReadOnlyList<SkillOption>>? SkillCatalogSource { get; init; }

  /// <summary>The shell's new-session command (issue #234): invoked by the /new slash
  ///     command with the rest of the input as the prompt. Null = the command is not
  ///     offered (headless stubs, or a host without the shell seam).</summary>
  public Func<string, Task>? NewSessionCommand { get; init; }

  /// <summary>Handles a session-request event from the host (issue #234): the new_session
  ///     tool or a compaction handoff asked the app to start a session in this tab's
  ///     workspace. The shell implements it; the view-model only forwards. The request id
  ///     travels WITH the request because the host is BLOCKED awaiting the answer, and the
  ///     answer goes back through <see cref="IRootTurnChannel.SendSessionResponse"/> — a
  ///     handler that could not name the request could not answer it.
  ///     Null = requests are refused with a notice.</summary>
  public Func<SessionRequestPayload, Task>? SessionRequestHandler { get; init; }

  /// <summary>Marshals an action onto the UI thread (test seam). Null = the Avalonia
  ///     UI-thread dispatcher (production). Desktop tests inject a synchronous executor
  ///     so the session-request path is drivable headless.</summary>
  public Action<Action>? UiMarshal { get; init; }

  /// <summary>The session's child-event stream: the split view's subagent accordion
  ///     subscribes to it (started adds rows, progress updates phases, settled marks
  ///     done). Null = the panel attaches nothing (headless stubs).</summary>
  public IAgentEvents? ChildEvents { get; init; }

  /// <summary>The session's agent store: the subagent rows poll child transcripts
  ///     from it for the realtime view. Null = rows render events only.</summary>
  public IAgentStore? AgentStore { get; init; }

  /// <summary>This conversation's host channel (spec D6). When wired, EVERY turn travels
  ///     to the host process and the app runs no agent work; when null the session is a
  ///     headless/test host that deliberately opted into the in-process runtime. The
  ///     presence of the channel IS the thin-client decision — there is no separate flag
  ///     that could disagree with the wiring.</summary>
  public IRootTurnChannel? RootTurnChannel { get; init; }

  /// <summary>The session's rendered system prompt, shown verbatim (collapsed) in the
  ///     bootstrap entry so nothing the agent receives is hidden. Empty = nothing to
  ///     surface (headless stubs); production always passes the session's prompt.</summary>
  public string SystemPrompt { get; init; } = string.Empty;

  /// <summary>The usage ledger's read model (issue #24): the status bar's session cost is
  ///     SEEDED from it at tab open, so a resumed session starts from its real history.
  ///     Null = nothing is seeded and the cost starts at zero (headless stubs).</summary>
  public IUsageQueries? UsageQueries { get; init; }

  /// <summary>The workbench's Plans view seams (issue #8): the plan service whose events
  ///     announce in-process writes, and the cross-process change signal. Null = the
  ///     workbench renders its unavailable state (headless stubs), never a blank pane.
  ///     <para>The state service and state bus were dropped here with the todo list
  ///     (issue #310): the Plans view has no second collection to read.</para></summary>
  public PlanService? Plans { get; init; }

  public IChangeSignal? ChangeSignal { get; init; }

  /// <summary>The session's agent event stream: the workbench's Plans view subscribes it
  ///     for <see cref="PlanChangedEvent"/> facts, which is the
  ///     INSTANT refresh path (the host forwards them over the wire, so a plan the model
  ///     writes in the host process reaches this view with no polling). Null = the view
  ///     falls back to its poller alone (headless stubs).</summary>
  public IAgentEvents? Events { get; init; }

  /// <summary>The workbench's Git view seam (issue #9): the read-only git queries behind
  ///     branch, status, and diff. Null = the view renders its unavailable state
  ///     (headless stubs), never a blank pane.</summary>
  public IGitQueryAccess? Git { get; init; }

  /// <summary>The workbench's GitHub view seam (issue #10): pull-request reads and the one
  ///     create-PR write, driven by the <c>gh</c> CLI (design D6). Null = the view renders
  ///     its unavailable state (headless stubs), never a blank pane.</summary>
  public IGitHubAccess? GitHub { get; init; }

  /// <summary>The workspace tab's workbench rail (issue #307 D6), when the shell built one for
  ///     the tab. The rail is WORKSPACE-scoped, so every cell in a tab shares one instance and a
  ///     cell swap must not rebuild it.
  ///     <para>Null = this view-model builds its own from the seams above, which is the pre-#307
  ///     behaviour and what a single-session test host still gets. The shell always passes one,
  ///     so production never takes that path.</para></summary>
  public WorkbenchViewModel? Workbench { get; init; }

  /// <summary>Receives this session's own ROOT TURN facts (issue #301 D5): a turn started,
  ///     a tool dispatch, a streamed fragment, a turn settled.
  ///     <para>WHY THIS EXISTS: the chain advance must be able to tell that a root session
  ///     has gone idle without completing its step, and the ONLY honest source for that is
  ///     the root loop's own lifecycle. <c>ChildProgressEvent</c> describes a child run and is
  ///     retired by #305/#316; a wall-clock timer is the mechanism <c>ChildTimeout</c> was
  ///     deleted for. These are facts this view-model already knows, surfaced so the root
  ///     supervisor can read them.</para>
  ///     <para>Null (the default) means the host wired no supervision and the callbacks are
  ///     no-ops, so a headless or test host is byte-identical to before.</para></summary>
  public Action<RootTurnActivity>? RootActivity { get; init; }
}

/// <summary>View-model for one open agent tab: owns that agent's transcript,
///     status bar, and turn loop — the chat interaction
///     surface moved here from MainViewModel when the shell gained tabs. Several
///     instances coexist, one per open tab, so nothing here may be static or
///     process-global.</summary>
internal sealed partial class AgentSessionViewModel : ObservableObject
{
  private readonly TurnRunner _runner;
  private readonly RootSessionLifecycle _lifecycle;
  private readonly AgentId _rootId;
  private readonly Conversation _conversation;
  private readonly Func<UiStreamEvent, Task> _streamSink;
  private readonly Action<string>? _statusModelUpdater;
  private readonly SessionModelPreferences? _modelPreferences;

  /// <summary>The usage ledger's read model (issue #24): the tab-open seed for the status
  ///     bar's session cost. Null = nothing to seed from (headless stubs).</summary>
  private readonly IUsageQueries? _usageQueries;

  /// <summary>The session's bootstrap model (the provider default). Serves as the
  ///     status-bar display when the user returns the session to automatic choice.</summary>
  private readonly string _sessionDefaultModelId;

  private Task? _runningTurn;

  [ObservableProperty]
  public partial bool IsBusy { get; set; }

  [ObservableProperty]
  public partial int MessageCount { get; set; }

  [ObservableProperty]
  public partial string Input { get; set; } = "";

  /// <summary>Images staged for the NEXT message (issue #20): clipboard pastes and file
  ///     drops land here; submit attaches them to the user message and clears the
  ///     queue. Bounded by ImageLimits at attach time - violations are notices.</summary>
  public System.Collections.ObjectModel.ObservableCollection<PendingImage> PendingImages { get; } = [];

  /// <summary>True when at least one image is staged (chip-row visibility).</summary>
  public bool HasPendingImages => PendingImages.Count > 0;

  /// <summary>Stages one image for the next message: format sniffed (image/png or
  ///     image/jpeg only), 20 MB bound, 4-per-message batch. Violations append a
  ///     transcript notice and return false - never a throw, never a silent clamp.</summary>
  public bool AttachImageBytes(byte[] bytes, string label)
  {
    ArgumentNullException.ThrowIfNull(bytes);
    string? mediaType = ImageFormats.SniffMediaType(bytes);
    if (mediaType is null)
    {
      Transcript.AddNotice($"Error [UnsupportedImageFormat]: '{label}' is not an attachable image. Formats: image/png or image/jpeg.");
      return false;
    }

    if (bytes.Length > ImageLimits.MaxBytes)
    {
      Transcript.AddNotice($"Error [ImageTooLarge]: '{label}' is {bytes.Length} bytes; the maximum is {ImageLimits.MaxBytes} bytes (20 MB).");
      return false;
    }

    if (PendingImages.Count >= ImageLimits.MaxPerMessage)
    {
      Transcript.AddNotice($"Error [ImageBatchFull]: at most {ImageLimits.MaxPerMessage} images per message.");
      return false;
    }

    PendingImages.Add(new PendingImage(label, bytes, mediaType));
    OnPropertyChanged(nameof(HasPendingImages));
    return true;
  }

  /// <summary>Reads one image file from disk and stages it under its file name.</summary>
  public bool AttachImageFile(string path)
  {
    try
    {
      return AttachImageBytes(File.ReadAllBytes(path), Path.GetFileName(path));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException)
    {
      Transcript.AddNotice($"Error [ImageReadFailed]: could not read '{path}': {ex.Message}");
      return false;
    }
  }

  /// <summary>Removes the staged image at <paramref name="index"/> (chip close).</summary>
  public void RemovePendingImage(int index)
  {
    if (index >= 0 && index < PendingImages.Count)
    {
      PendingImages.RemoveAt(index);
      OnPropertyChanged(nameof(HasPendingImages));
    }
  }

  private void ClearPendingImages()
  {
    PendingImages.Clear();
    OnPropertyChanged(nameof(HasPendingImages));
  }

  public TranscriptViewModel Transcript { get; } = new();
  public StatusViewModel Status { get; }

  /// <summary>The workspace directory this agent works from (tab subtitle).</summary>
  public string WorkspaceRoot { get; }

  /// <summary>The persisted root session id (status-bar display, copy support).</summary>
  public Guid SessionId => _rootId.Value;

  /// <summary>The session id's first 8 hex characters (the compact status-bar form).</summary>
  public string SessionIdShort => SessionId.ToString()[..8];

  /// <summary>The full session id as a string (tooltip / clipboard form).</summary>
  public string SessionIdFull => SessionId.ToString();

  /// <summary>Tab caption: the workspace directory's name.</summary>
  public string Title => Path.GetFileName(WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

  /// <summary>Runs ! commands (user-side shell utilities) against the workspace.
  ///     Null = the ! surface is unavailable (headless stubs).</summary>
  private readonly IUserCommandRunner? _commandRunner;

  /// <summary>The shell's new-session command (issue #234): the /new slash command
  ///     invokes it with the rest of the input as the prompt. Null = the command
  ///     is not offered (headless stubs, or a host without the shell seam).</summary>
  private readonly Func<string, Task>? _newSessionCommand;

  /// <summary>Handles a session-request event from the host (issue #234): the shell
  ///     starts the session in this tab's workspace. Null = requests are refused
  ///     with a typed notice.</summary>
  private readonly Func<SessionRequestPayload, Task>? _sessionRequestHandler;

  /// <summary>Marshals an action onto the UI thread. Production: the Avalonia UI-thread
  ///     dispatcher. Tests: a synchronous executor injected through the options record —
  ///     the session-request path must be drivable headless.</summary>
  private readonly Action<Action> _uiMarshal;

  /// <summary>The shared skill invocation core (slash resolution) — null when the
  ///     session did not wire the skill catalog.</summary>
  private readonly SkillInvocationService? _skillInvocation;

  /// <summary>The '/' autocomplete popup. Null when the session has no catalog
  ///     source (headless stubs); the view routes keys only while it is open.</summary>
  public SkillAutocompleteViewModel? Autocomplete { get; }

  /// <summary>The split view's subagent accordion (right column): rows arrive by
  ///     subscription to the session's child-event stream, transcripts poll the
  ///     store. Always constructed; visible only while rows exist.</summary>
  public SubagentPanelViewModel Subagents { get; }

  /// <summary>The right-hand workbench (issue #8, design D1/D5): a permanent column with
  ///     an icon strip, showing one view at a time. Always constructed so the shell's
  ///     layout is stable; the user's collapse is what hides it.</summary>
  public WorkbenchViewModel Workbench { get; }

  /// <summary>The rail the workspace tab handed this session (issue #307 D6), or null when
  ///     this view-model built its own. Null is what makes the rail this view-model's to
  ///     dispose: a SHARED rail outlives every cell in the tab, so a cell that disposed it
  ///     would kill the pane its siblings are still reading.</summary>
  private readonly WorkbenchViewModel? _sharedWorkbench;

  /// <summary>Feeds the popup from the input box's current text; the view calls
  ///     this on every input change.</summary>
  public void UpdateAutocomplete(string input) => Autocomplete?.Update(input);

  /// <summary>The /new command's popup row (issue #234): a well-known option the
  ///     catalog source can include alongside the skills. It is a COMMAND, not a
  ///     manual skill — it sorts after manual skills and before plain ones and
  ///     renders the distinct <c>[command]</c> badge. The source wiring itself
  ///     (MainViewModel.SkillOptionsOf) is Task 7's; the option's shape is what
  ///     the popup tests pin here.</summary>
  public static SkillOption NewSessionCommandOption { get; } =
      new("new", "Start a new session in this workspace with the rest of the input as its first prompt.", IsManual: false, IsCommand: true);

  private readonly IAgentInbox? _inbox;
  private readonly Action<RootTurnActivity>? _rootActivity;
  private readonly IAgentRuntime? _childRuntime;

  /// <summary>The subscription that releases pinned steering echoes when the loop's drain
  ///     fact names them (issue #253 B3). Disposed with the session.</summary>
  private IDisposable? _drainLease;

  /// <summary>This conversation's host channel (spec D6). Null means the session runs its
  ///     turns in this process — the headless/test-host shape, never production.
  ///     <para>Public because the shell must ANSWER a session-request (issue #234): the host
  ///     is blocked awaiting the reply, and the reply travels back over the same channel the
  ///     turn arrived on. Null when the session runs in process (no host to answer).</para></summary>
  public IRootTurnChannel? RootTurnChannel { get; }

  /// <summary>App-side conversation mutations waiting for the NEXT turn to carry to the host
  ///     (spec D6): a slash invocation's System line and a ! command's model-facing line.
  ///     They queue because they are produced OUT of turn — a ! command may run mid-turn or
  ///     between turns — and they must travel with a turn, since the host owns the
  ///     conversation the model actually reads. Cleared as each turn takes them.</summary>
  private readonly List<RootTurnPreTurnMessage> _pendingPreTurn = [];

  /// <summary>The live host turn's id, or null between turns. Stop needs it to interrupt the
  ///     turn IN THE HOST: cancelling the local token alone abandons only this process's wait.</summary>
  private string? _liveTurnId;

  /// <summary>The active turn's cancellation source, or null between turns. Assigned on the UI
  /// thread at turn start; cleared at turn end. Never disposed inline — cancelling a disposed
  /// source from the UI thread would race the worker's teardown.</summary>
  private CancellationTokenSource? _turnCts;

  public AgentSessionViewModel(TurnRunner runner,
      RootSessionLifecycle lifecycle,
      AgentId rootId,
      Conversation conversation,
      string provider,
      string modelId,
      AgentSessionViewModelOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    WorkspaceRoot = options.WorkspaceRoot;
    _statusModelUpdater = options.StatusModelUpdater;
    // Turns must never run on the UI thread: their awaits would post back to
    // Avalonia's SynchronizationContext and one blocking tool call would freeze
    // the whole shell (see DesktopHost.OffUiThread). The schedule wraps EVERY
    // runner, stub or real, so the guarantee cannot be bypassed by callers.
    _runner = DesktopHost.OffUiThread(
        runner ?? throw new ArgumentNullException(nameof(runner)));
    _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    _rootId = rootId;
    _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
    // Default sink applies on the calling thread — adequate for unit tests.
    // Production passes ApplyUiStreamEventOnUIThreadAsync so transcript mutations
    // always land on the UI thread.
    _streamSink = options.UiStreamSink ?? (evt =>
    {
      ApplyStreamEvent(evt);
      return Task.CompletedTask;
    });
    _modelPreferences = options.ModelPreferences;
    _usageQueries = options.UsageQueries;
    _sessionDefaultModelId = modelId;
    // The session's model is ALWAYS NAMED (2026-10-22) - see ResolveServingModel.
    string servingModel = ResolveServingModel(_modelPreferences, modelId);
    Status = new StatusViewModel(provider, servingModel, EffortLevels.DisplayName(_modelPreferences?.ReasoningEffort));
    _commandRunner = options.CommandRunner;
    _skillInvocation = options.SkillInvocation;
    _newSessionCommand = options.NewSessionCommand;
    _sessionRequestHandler = options.SessionRequestHandler;
    // Null = the Avalonia UI-thread dispatcher (production); tests inject a
    // synchronous executor so the session-request path runs headless.
    _uiMarshal = options.UiMarshal ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
    Autocomplete = options.SkillCatalogSource is null ? null : new SkillAutocompleteViewModel(options.SkillCatalogSource);
    _inbox = options.Inbox;
    _childRuntime = options.ChildRuntime;
    _rootActivity = options.RootActivity;
    RootTurnChannel = options.RootTurnChannel;
    AttachDrainLease(options.ChildEvents, rootId);

    Subagents = new SubagentPanelViewModel(options.AgentStore);
    Subagents.Attach(options.ChildEvents, apply => evt =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => apply(evt)));
    // The workbench renders on the UI thread (its refresh completes on pool threads).
    // Issue #307 D6: a tab hands its OWN rail to every cell, because the rail is
    // workspace-scoped - a cell swap must not rebuild a pane the user is reading.
    _sharedWorkbench = options.Workbench;
    Workbench = _sharedWorkbench ?? BuildWorkbench(options);
    // Grand-plan "show initial bootstrap/context at start of session": the first
    // transcript line states what the session runs under (transient, never a
    // conversation message). Resume replays persisted history after it.
    Transcript.AddBootstrap(new BootstrapEntry(
        WorkspaceRoot, provider, modelId, SessionIdShort, options.SystemPrompt));
  }



  /// <summary>Subscribes the root loop's drain fact, which releases a pinned steering echo
  ///     (issue #253 B3). The drain happens in the HOST process, so the fact arrives over the
  ///     child-event stream the session already subscribes to; the marshal keeps the transcript
  ///     mutation on the UI thread, exactly like the subagent panel's.
  ///     <para>Null stream (headless hosts) subscribes nothing.</para></summary>
  private void AttachDrainLease(IAgentEvents? events, AgentId rootId)
  {
    if (events is null)
    {
      return;
    }

    _drainLease = events.Subscribe(new SteerDrainSubscriber(rootId, ids =>
        _uiMarshal(() =>
        {
          foreach (Guid id in ids)
          {
            Transcript.MarkDrained(id);
          }
        })));
  }

  /// <summary>Builds this session's own workbench rail from the supplied seams (issue #8).
  ///     Only a session whose tab handed it no rail takes this path (issue #307 D6): the shell
  ///     builds ONE rail per workspace tab and shares it, because the rail is workspace-scoped.
  ///     <para>Every view renders its own unavailable state when its seam is null, so a headless
  ///     stub gets a working rail rather than a blank pane.</para></summary>
  private WorkbenchViewModel BuildWorkbench(AgentSessionViewModelOptions options) => new(
      new PlansViewViewModel(
          options.Plans,
          options.ChangeSignal,
          render => Avalonia.Threading.Dispatcher.UIThread.Post(render),
          events: options.Events),
      // The git view watches the WORKSPACE ROOT (never .git): working-tree edits are the
      // thing it exists to show, and they never touch .git.
      new GitViewViewModel(
          options.WorkspaceRoot,
          options.Git,
          render => Avalonia.Threading.Dispatcher.UIThread.Post(render)),
      Subagents,
      // The GitHub view (issue #10) polls the gh CLI on a long interval and ONLY while
      // visible: each read is a subprocess, and no push channel exists (design D6).
      new PullRequestPaneViewModel(
          options.WorkspaceRoot,
          options.GitHub,
          render => Avalonia.Threading.Dispatcher.UIThread.Post(render)));

  /// <summary>Seeds the status bar's session cost from the persisted rollup (issue #24):
  ///     the ledger rows a resumed or reopened session already has. Called at tab open,
  ///     BEFORE the first turn, so no in-turn increment can be lost to it.
  ///     <para>Best effort by design: a read-model fault leaves the cost at zero rather
  ///     than failing the session open — a status readout is never load-bearing. The
  ///     failure is still visible: the cost line simply stays empty, which is the same
  ///     state a session with no spend renders.</para></summary>
  public async Task SeedUsageAsync(CancellationToken ct = default)
  {
    if (_usageQueries is null)
    {
      return;
    }

    // Named decision (CA1031): a status seed must never fail the session open.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Result<UsageSummary> summary = await _usageQueries
          .SessionSummaryAsync(_rootId.ToString(), ct).ConfigureAwait(true);
      if (summary.IsSuccess)
      {
        Status.SeedUsage(summary.Value.Totals);
      }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Deliberately swallowed: see above.
    }
#pragma warning restore CA1031
  }
  /// <summary>
  /// Processes one submission. Blank input is ignored. While a turn runs, input
  /// steers it. For normal turns, sets the in-flight task and returns that same
  /// task so callers can await it directly. A RAW leading '/' (no trim — the
  /// spec's slash-detection amendment) resolves as a skill invocation BEFORE
  /// the ! check: found injects the System line then forwards the full input
  /// as the ordinary user message; unknown names fall through as plain
  /// messages; ambiguous names send nothing and surface a notice.
  /// </summary>
  public async Task SubmitAsync(string rawInput)
  {
    string input = rawInput?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(input))
    {
      return;
    }

    // Slash detection reads the RAW input's first character (spec #28: no trim —
    // leading-whitespace input is not slash input) and runs before the ! check.
    if (rawInput!.StartsWith('/'))
    {
      await SubmitSlashAsync(rawInput).ConfigureAwait(true);
      return;
    }

    // The ! prefix is a USER-side shell command, not an LLM turn: it runs against the
    // workspace directly — even while a turn is in flight (it never steers the model).
    if (input.StartsWith('!'))
    {
      await RunCommandAsync(input[1..].Trim()).ConfigureAwait(true);
      return;
    }

    // While a turn runs, input steers it: posted to the session inbox for delivery at the
    // loop's next safe point, and echoed into the transcript immediately. Never dropped.
    if (IsBusy)
    {
      Steer(input);
      return;
    }

    // Real turn — start and track it.
    _runningTurn = ExecuteTurnAsync(input);
    await _runningTurn.ConfigureAwait(true);
  }

  /// <summary>Resolves a slash input: the session commands first (issue #234 —
  ///     /new is a SHELL command, not a skill, and must work even where no skill
  ///     catalog is wired), then the shared invocation core (the same object the
  ///     skill_invoke tool uses). Found: the System line goes into the
  ///     conversation (out-of-turn, the UserCommandRunner precedent) AND onto the
  ///     transcript (live parity with the ! command surface), then the FULL
  ///     original input forwards as the ordinary user message. Unknown: not slash
  ///     input — a plain message. Ambiguous: nothing is sent; a transcript notice
  ///     lists the matches (spec amendment). A resolvable skill named 'new' loses
  ///     to the command: the command registry resolves before the skill lookup.</summary>
  private async Task SubmitSlashAsync(string rawInput)
  {
    string token = rawInput[1..];
    int space = token.IndexOf(' ', StringComparison.Ordinal);
    string name = space < 0 ? token : token[..space];
    string? args = space < 0 ? null : token[(space + 1)..].Trim();

    // Issue #234: the /new command resolves BEFORE the skill lookup — it is a
    // shell command, not a skill, so it must work even without a catalog.
    if (string.Equals(name, "new", StringComparison.OrdinalIgnoreCase))
    {
      await RunNewSessionCommandAsync(args).ConfigureAwait(true);
      return;
    }

    if (_skillInvocation is null)
    {
      Transcript.AddNotice("Skill invocation is unavailable in this session (no catalog wired).");
      return;
    }

    Result<SkillInvocation> r = await _skillInvocation.InvokeAsync(name, args, CancellationToken.None).ConfigureAwait(true);
    if (r.IsSuccess)
    {
      _conversation.AddSystemMessage(r.Value.SystemLine);
      // Spec D6: the host owns the conversation the model reads, so the line must ALSO
      // travel with the next turn — a local-only injection reaches no model at all.
      _pendingPreTurn.Add(new RootTurnPreTurnMessage("system", r.Value.SystemLine));
      Transcript.AddSystemMessage(r.Value.SystemLine);
      if (IsBusy)
      {
        Steer(rawInput);
        return;
      }

      _runningTurn = ExecuteTurnAsync(rawInput);
      await _runningTurn.ConfigureAwait(true);
      return;
    }

    if (r.Error.Code == "AmbiguousSkill")
    {
      Transcript.AddNotice(r.Error.Message);
      return;
    }

    // Unknown name: not slash input — an ordinary message.
    if (IsBusy)
    {
      Steer(rawInput);
      return;
    }

    _runningTurn = ExecuteTurnAsync(rawInput);
    await _runningTurn.ConfigureAwait(true);
  }

  /// <summary>Runs the /new session command (issue #234): every refusal is a typed
  ///     notice — nothing is coerced. Unwired seam: unavailable. Busy turn: refused
  ///     (a new session must not race the running one for the tab). Missing prompt:
  ///     refused with the usage line. Otherwise the shell's command delegate runs
  ///     with the rest of the input as the prompt, announced by a notice first.</summary>
  private async Task RunNewSessionCommandAsync(string? args)
  {
    if (_newSessionCommand is null)
    {
      Transcript.AddNotice("The new-session command is unavailable in this session (no shell seam wired).");
      return;
    }

    if (IsBusy)
    {
      Transcript.AddNotice("A turn is running — stop it before starting a new session (/new refuses while busy).");
      return;
    }

    string prompt = args ?? "";
    if (string.IsNullOrWhiteSpace(prompt))
    {
      Transcript.AddNotice("Give the new session its first prompt: /new <prompt>.");
      return;
    }

    Transcript.AddNotice("[new session: starting a fresh session with the prompt you gave.]");
    try
    {
      await _newSessionCommand(prompt).ConfigureAwait(true);
    }
#pragma warning disable CA1031 // Named decision: a command fault must surface as a notice, never an unobserved task exception.
    catch (Exception ex)
    {
      Transcript.AddNotice($"Error [NewSessionCommandFault]: {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Runs one ! command: echoes a command entry, executes through the
  ///     session's runner (persisting the run and appending the system message for the
  ///     model), and lands the captured output as a local transcript entry. The
  ///     command's output never enters the conversation; the system message does.</summary>
  private async Task RunCommandAsync(string command)
  {
    if (string.IsNullOrWhiteSpace(command))
    {
      Transcript.AddNotice("Type a command after the ! (for example: ! git status).");
      return;
    }

    if (_commandRunner is null)
    {
      Transcript.AddNotice("Commands are unavailable in this session (no shell runner wired).");
      return;
    }

    Transcript.AddCommandRun(command);
    Result<CommandRun> result = await _commandRunner.RunAsync(command, CancellationToken.None).ConfigureAwait(true);
    if (!result.IsSuccess)
    {
      Transcript.AddNotice($"Error [{result.Error.Code}]: {result.Error.Message}");
      return;
    }

    Transcript.AddCommandResult(result.Value);
    // Full transparency: the conversation just gained the model-facing system line
    // for this run; the live transcript shows the SAME verbatim text (a resumed
    // session replays it as a system entry - live parity is the contract).
    // Spec D6: the host owns the conversation the model reads, so the line must ALSO
    // travel with the next turn — otherwise command_output is never discoverable.
    _pendingPreTurn.Add(new RootTurnPreTurnMessage("system", result.Value.ModelFacingLine));
    Transcript.AddSystemMessage(result.Value.ModelFacingLine);
  }

  /// <summary>
  /// Applies the user's effort-picker choice: sets the session's reasoning effort
  /// (null returns it to the provider default) and updates the status bar. Applies
  /// from the next turn, root and children alike; persisted per workspace by the shell.
  /// </summary>
  public void ApplyEffortChoice(ReasoningEffort? effort)
  {
    if (_modelPreferences is null)
    {
      Transcript.AddNotice("Effort choice is unavailable in this session (no model preferences wired).");
      return;
    }

    _modelPreferences.ReasoningEffort = effort;
    Status.Effort = EffortLevels.DisplayName(effort);
    Transcript.AddNotice(effort is null
        ? "Reasoning effort set to the model default; applies from the next turn."
        : $"Reasoning effort set to {EffortLevels.DisplayName(effort.Value)}; applies from the next turn.");
  }

  /// <summary>
  /// Applies a Model Settings save: the effort choice and every non-null knob
  /// land on the session's live model preferences (null = unset — the resolved
  /// config's value applies; a null effort returns it to the model default), and
  /// the serialized OpenRouter provider settings ride along verbatim. Applies
  /// from the next turn, root and children alike; persisted per workspace by the
  /// shell. Mirrors ApplyEffortChoice.
  /// </summary>
  public void ApplySamplingSettings(SessionModelPreferences settings)
  {
    if (_modelPreferences is null)
    {
      Transcript.AddNotice("Sampling settings are unavailable in this session (no model preferences wired).");
      return;
    }

    _modelPreferences.ReasoningEffort = settings.ReasoningEffort;
    Status.Effort = EffortLevels.DisplayName(settings.ReasoningEffort);
    _modelPreferences.Temperature = settings.Temperature;
    _modelPreferences.MaxTokens = settings.MaxTokens;
    _modelPreferences.TopP = settings.TopP;
    _modelPreferences.TopK = settings.TopK;
    _modelPreferences.FrequencyPenalty = settings.FrequencyPenalty;
    _modelPreferences.PresencePenalty = settings.PresencePenalty;
    _modelPreferences.RepetitionPenalty = settings.RepetitionPenalty;
    _modelPreferences.MinP = settings.MinP;
    _modelPreferences.TopA = settings.TopA;
    _modelPreferences.Seed = settings.Seed;
    _modelPreferences.Verbosity = settings.Verbosity;
    _modelPreferences.ParallelToolCalls = settings.ParallelToolCalls;
    _modelPreferences.ProviderSettings = settings.ProviderSettings;
    Transcript.AddNotice("Sampling settings applied; they take effect from the next turn.");
  }

  /// <summary>The model that will serve this session: the user's pick when there is
  ///     one, the session's default model otherwise. The default is STATED (written into
  ///     the live preferences) rather than left unnamed: the resolver never substitutes a
  ///     model the session did not name - an unnamed session fails every turn with "no
  ///     model is selected" - and the status bar must name exactly what serves.
  ///     <para>2026-10-22: the resolver used to serve the provider's routing model for
  ///     any session that had not named one, while the app displayed a different pick.</para></summary>
  private static string ResolveServingModel(SessionModelPreferences? preferences, string defaultModelId)
  {
    if (preferences is null)
    {
      return defaultModelId;
    }

    if (preferences.ModelId is { Length: > 0 } named)
    {
      return named;
    }

    preferences.ModelId = defaultModelId;
    return defaultModelId;
  }

  /// <summary>
  /// Applies the user's picker choice: pins the session's MODEL AND ROUTING VARIANT, updates the
  /// status bar, and announces the change. Applies from the next turn, root and children alike;
  /// persisted per workspace by the shell.
  /// <para>THE VARIANT IS THE SECOND HALF OF THE DECISION. It is a suffix on the model id, so it
  /// is what the provider routes on - committing only the model would leave the turn on Standard
  /// routing while the dialog reported the tradeoff the user chose. A null
  /// <paramref name="variant"/> CLEARS it rather than inheriting the previous one, which would
  /// silently keep routing under a tradeoff the user just moved away from.</para>
  /// </summary>
  /// <param name="modelId">The model to serve, or null for the session's default model.</param>
  /// <param name="variant">The routing variant, or null for the provider's own routing.</param>
  public void ApplyModelChoice(string? modelId, ModelVariant? variant = null)
  {
    if (_modelPreferences is null)
    {
      Transcript.AddNotice("Model choice is unavailable in this session (no model preferences wired).");
      return;
    }

    // Null means the session's DEFAULT model, and the default is an EXPLICIT choice
    // (2026-10-22): an unnamed session fails every turn with "no model is selected", and
    // the status bar would name a model that never serves. The default model id is
    // therefore what the session states - the same id the status bar shows.
    string chosen = string.IsNullOrWhiteSpace(modelId) ? _sessionDefaultModelId : modelId;
    _modelPreferences.ModelId = chosen;
    _modelPreferences.ModelVariant = variant;
    Status.ModelId = chosen;
    // The status line names the routing the request will actually carry, by its bare name
    // ('Nitro', 'Floor') - the caption beneath the Routing dropdown explains what each one
    // trades. Standard renders as nothing: the default is not a decision the user made, and a
    // bar labeling every session 'Standard' would be noise.
    Status.ServingVariant = variant is { } committed && committed != ModelVariant.Standard
        ? ModelVariants.Describe(committed)
        : string.Empty;
    Transcript.AddNotice(variant is { } served && served != ModelVariant.Standard
        ? $"Model set to {chosen} with {ModelVariants.Describe(served)}; applies from the next turn."
        : $"Model set to {chosen}; applies from the next turn.");
  }

  /// <summary>Awaits the in-flight turn task. Returns a completed task if no turn is running.</summary>
  public Task WaitForTurnAsync() => _runningTurn ?? Task.CompletedTask;

  /// <summary>Releases pinned steering echoes on the ROOT loop's own drain fact (issue #253
  ///     B3). Only the root's drain counts: a CHILD's drain names messages the user never
  ///     typed into this transcript, and releasing on one would unpin an echo the model has
  ///     not seen. The root agent carries the session's persisted root id, which is what the
  ///     loop stamps on the fact.</summary>
  private sealed class SteerDrainSubscriber(AgentId rootId, Action<IReadOnlyList<Guid>> onDrained)
      : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      if (evt is MailboxDrainedEvent { Count: > 0 } drained && drained.ChildId == rootId)
      {
        onDrained(drained.MessageIds);
      }
    }
  }

  /// <summary>
  /// Completes the root session on graceful exit (tab close or window close).
  /// Persistence failures surface as transcript notices — teardown itself never throws.
  /// </summary>
  public async Task ShutdownAsync()
  {
    // M17: release decoded screenshot bitmaps when the tab shuts down.
    _drainLease?.Dispose();
    _drainLease = null;
    // Issue #8: stop the workbench's poller and detach its event subscriptions; a
    // closed tab must not keep reading the database once a second.
    // Issue #307 D6: a SHARED rail belongs to the tab, not to this cell - disposing it here
    // would kill the pane every other cell in the tab is reading. Only the rail this
    // view-model built itself is its to dispose.
    if (_sharedWorkbench is null)
    {
      Workbench.Dispose();
    }
    Transcript.DisposeEntries();
    await _lifecycle.CompleteAsync(_rootId, ReportPersistenceError).ConfigureAwait(true);
  }

  /// <summary>Reports one root-turn fact to the session's supervisor (issue #301 D5).
  ///     <para>Best effort by contract: the supervisor is a supervision surface, and a
  ///     callback fault must never fail the turn it is observing - the same rule the event
  ///     stream's subscribers follow.</para></summary>
  private void BeatRootActivity(RootTurnPhase phase, string label)
  {
    if (_rootActivity is not { } sink)
    {
      return;
    }

    try
    {
      sink(new RootTurnActivity(_rootId, DateTimeOffset.UtcNow, phase, label));
    }
    // Named decision (CA1031): a supervision callback is an observer, never a turn step.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // Swallowed deliberately: see the named decision above.
    }
  }

  private async Task ExecuteTurnAsync(string input)
  {
    try
    {
      await ExecuteTurnCoreAsync(input);
    }
    // A faulting turn must surface, never vanish: the view fire-and-forgets the turn
    // task (an unobserved exception is invisible), so the fault is delivered to the
    // transcript instead — the user sees it, and so does any diagnostic session.
#pragma warning disable CA1031 // Named decision: the turn loop is a fault boundary.
    catch (Exception ex)
    {
      Transcript.AddNotice($"Error [TurnFault]: {ex}");
    }
#pragma warning restore CA1031
    finally
    {
      _turnCts = null;
      Status.Phase = TurnPhase.Ready;
      IsBusy = false;
      // The turn is over: this is the moment idleness BEGINS for the root supervisor
      // (issue #301 D5). Emitted from the finally so a faulted or cancelled turn settles
      // the clock too - a session whose turn threw is idle, not busy.
      BeatRootActivity(RootTurnPhase.TurnSettled, "turn-settled");
    }
  }

  private async Task ExecuteTurnCoreAsync(string input)
  {
    MessageCount++;

    // Staged images (issue #20) attach to THIS user message and clear: the parts ride
    // the conversation message (and the provider wire); the transcript entry shows the
    // decoded strip. A payload that no longer decodes degrades to a notice (I13).
    List<TranscriptImage>? transcriptImages = null;
    List<string>? transcriptImageLabels = null;
    IReadOnlyList<MessagePart>? imageParts = null;
    if (PendingImages.Count > 0)
    {
      imageParts = [.. PendingImages.Select(p => new MessagePart.ImagePart(p.MediaType, Convert.ToBase64String(p.Bytes)))];
      transcriptImages = [];
      transcriptImageLabels = [.. PendingImages.Select(p => p.Label)];
      foreach (PendingImage p in PendingImages)
      {
        try
        {
          transcriptImages.Add(new TranscriptImage(p.Bytes));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
          Transcript.AddNotice($"[chat] staged image '{p.Label}' could not be decoded and was skipped.");
        }
      }

      ClearPendingImages();
    }

    Transcript.AddUser(input, transcriptImages, transcriptImageLabels);
    Status.Phase = TurnPhase.Thinking;
    IsBusy = true;
    BeatRootActivity(RootTurnPhase.TurnStarted, "turn-start");

    CancellationTokenSource cts = new();
    _turnCts = cts;

    StreamBridge bridge = new(_streamSink, coalesce: true);
    bridge.Start();

    int messageCountBefore = _conversation.Messages.Count;
    bool sawStream = false;
    bool compactedThisTurn = false;
    bool shrunkThisTurn = false;
    // Every streamed fragment is a root-turn fact (issue #301 D5): this is the clock the
    // root supervisor reads, and it must advance while the model is producing text or the
    // session would read as idle mid-answer.
    void OnStreamed()
    {
      sawStream = true;
      BeatRootActivity(RootTurnPhase.Streamed, "stream");
    }

    Result<string> result = RootTurnChannel is { } channel
        ? await SendTurnToHostAsync(channel, input, imageParts, bridge,
            OnStreamed, cts.Token).ConfigureAwait(true)
        : await RunTurnInProcessAsync(input, imageParts, bridge,
            OnStreamed,
            () => compactedThisTurn = true,
            () => shrunkThisTurn = true, cts.Token).ConfigureAwait(true);

    // Close the channel so the pump can drain all buffered events.
    bridge.MarkTurnComplete();

    try
    {
      await bridge.DrainUntilIdleAsync();
    }
    // Named decision (CA1031): a sink fault must surface as an error notice —
    // the turn loop keeps running and the VM never crashes.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      // Sink fault — surface as an error notice rather than crashing the VM.
      Transcript.AddNotice($"Error [StreamFault]: {ex.Message}");
      return;
    }
#pragma warning restore CA1031

    await PersistTurnAsync(messageCountBefore, result, compactedThisTurn || shrunkThisTurn);

    // Non-streaming fallback: if no deltas were delivered, show the final text as a notice.
    // A HOST turn that streams partial text and then settles with an error payload is still
    // a failure the user must see: the transport Result is a SUCCESS (the settle arrived),
    // so the error is read from the payload — otherwise a truncated answer would render with
    // no error at all, which the in-process path never did.
    if (!result.IsSuccess)
    {
      Transcript.AddNotice($"Error [{result.Error.Code}]: {result.Error.Message}");
      return;
    }

    if (!sawStream)
    {
      Transcript.AddNotice(result.Value);
      return;
    }

    if (IsErrorSettle(result.Value))
    {
      Transcript.AddNotice(result.Value);
    }
  }

  /// <summary>Runs one turn in THIS process. Only a session with no host channel reaches
  ///     this path — the app's own production wiring always has one (spec D6), so this is
  ///     the headless/test-host shape: a container that deliberately opted into the
  ///     in-process runtime. It stays byte-identical to the pre-thin-client behavior.
  ///     <para>The turn's own cancellation is a TYPED TurnCancelled result, never an
  ///     escaped OperationCanceledException: the stop button cancels <paramref name="ct"/>
  ///     and the user must read 'stopped' rather than a fault.</para></summary>
  private async Task<Result<string>> RunTurnInProcessAsync(string input,
      IReadOnlyList<MessagePart>? imageParts, StreamBridge bridge,
      Action onStreamed, Action onCompacted, Action onShrunk, CancellationToken ct)
  {
    try
    {
      return await _runner(
          new SendMessageCommand(input, imageParts),
          ct,
          new TurnCallbacks(
              OnContentDelta: d =>
              {
                onStreamed();
                bridge.OnContentDelta(d);
              },
              OnReasoningDelta: bridge.OnReasoningDelta,
              OnIterationEnd: bridge.OnIterationEnd,
              OnContextUpdate: snapshot => Status.SetContext(snapshot),
              OnUsageRecorded: row => Status.AddUsage(row),
              OnCompacted: _ => onCompacted(),
              OnContextShrunk: onShrunk,
              OnToolCall: (name, args, index, count) => bridge.OnToolCall(name, args),
              OnToolResult: (name, summary, full, isError, rich) =>
                bridge.OnToolResult(name, summary, full, isError, rich),
              OnSystemMessage: bridge.OnSystemMessage),
          onNotice: bridge.OnNotice).ConfigureAwait(true);
    }
    catch (OperationCanceledException)
    {
      return Result.Failure<string>(new DomainError("TurnCancelled", "the turn was stopped."));
    }
  }

  /// <summary>Sends one turn to this conversation's host process (spec D6) and maps its
  ///     events onto the SAME bridge the in-process path feeds, so the rendered surface is
  ///     identical while no agent work runs here.
  ///     <para>Every event is routed through the bridge rather than applied inline: the
  ///     bridge is what keeps transcript mutation on the sink's thread (the UI thread in
  ///     production) and what makes <c>DrainUntilIdleAsync</c> mean 'every event was
  ///     applied'. Applying on the wire's pump thread would violate the transcript's
  ///     UI-thread-only contract.</para>
  ///     <para>A host that settles with an <c>Error [&lt;code&gt;]: ...</c> payload is a
  ///     SUCCESSFUL transport turn carrying a failed turn outcome — the payload is the
  ///     answer the user must see. A failed Result is a transport-level fault and keeps its
  ///     own typed code.</para></summary>
  private async Task<Result<string>> SendTurnToHostAsync(IRootTurnChannel channel, string input,
      IReadOnlyList<MessagePart>? imageParts, StreamBridge bridge, Action onStreamed,
      CancellationToken ct)
  {
    // A FRESH turn id and generation per turn: the wire runtime keys its settle matching on
    // both, so a reused id would let a timed-out predecessor's settle complete this turn
    // with the wrong payload.
    string turnId = Guid.NewGuid().ToString("N");
    _liveTurnId = turnId;
    RootTurnRequest request = new(turnId, input, [])
    {
      Images = ToWireImages(imageParts),
      // The app's own pre-turn conversation mutations ride the request: the host is a
      // different process with its own conversation, so anything injected only here would
      // never reach the model.
      PreTurn = [.. _pendingPreTurn],
      // The model picker lives HERE and the agent lives in the host: a choice that did not
      // travel never reached the serving model (2026-10-22 - every host turn served the
      // fallback while the status bar showed the user's pick). The carrier is opaque JSON;
      // the host decodes it onto its own preferences before resolving.
      ModelPreferences = _modelPreferences is null
          ? null
          : SessionModelPreferencesJson.Serialize(_modelPreferences),
    };
    _pendingPreTurn.Clear();
    try
    {
      return await channel
          .SendRootTurnAsync(request, evt => ApplyRootTurnEvent(evt, bridge, onStreamed), ct)
          .ConfigureAwait(true);
    }
    finally
    {
      _liveTurnId = null;
    }
  }

  /// <summary>Maps one host event onto the stream bridge. The wire kinds are the
  ///     session surface's own vocabulary (delta/reasoning/notice/tool/settled, plus
  ///     context for the status bar's accounting), so this is a translation, never a
  ///     second rendering path.
  ///     <para>An unreadable <c>tool</c> payload becomes a notice instead of vanishing: a
  ///     dropped tool event would hide a whole step of the turn.</para>
  ///     <para><c>progress</c> is the host's LIVENESS HEARTBEAT (2026-10-22) and renders
  ///     nothing: it exists so the app can tell "the host is busy" from "the host stopped
  ///     answering" instead of failing a healthy long turn. The turn's own tool card is
  ///     already on screen, so a heartbeat line in the transcript would be noise.</para></summary>
  private void ApplyRootTurnEvent(RootTurnEvent evt, StreamBridge bridge, Action onStreamed)
  {
    ArgumentNullException.ThrowIfNull(evt);
    switch (evt.Kind)
    {
      case "delta":
        onStreamed();
        bridge.OnContentDelta(evt.Payload);
        break;
      case "reasoning":
        // The model's streaming thoughts (issue #245): the same ReasoningEntry the
        // in-process path renders. The host forwards them as their own wire kind.
        bridge.OnReasoningDelta(evt.Payload);
        break;
      case "notice":
        bridge.OnNotice(evt.Payload);
        break;
      case "tool":
        ApplyRootTurnToolEvent(evt.Payload, bridge);
        break;
      case "session-request":
        ApplySessionRequest(evt.Payload);
        break;
      case "context":
        ApplyContextEvent(evt.Payload);
        break;
      case "usage":
        ApplyUsageEvent(evt.Payload);
        break;
      case "progress":
        // The heartbeat proves the host is alive; it says nothing the transcript needs.
        break;
      case "settled":
        // The settle's payload is the turn's answer (or its typed failure line); the
        // outcome is read from the channel's Result, so nothing is applied here beyond
        // the phase change. TurnPhase has no Idle member - Ready IS the idle phase, and
        // the turn's finally block sets it too, so this is idempotent.
        Status.Phase = TurnPhase.Ready;
        break;
      default:
        break;
    }
  }

  /// <summary>Applies one `context` event (issue #255): the accountant lives in the HOST,
  ///     so this event is the only way the status bar's CTX line advances for a host-run
  ///     turn. SetContext is already thread-safe (it raises through the dispatcher), so no
  ///     marshal is needed here. An unreadable payload is dropped rather than rendered: the
  ///     CTX line is a status readout, and a notice per provider response would flood the
  ///     transcript.</summary>
  private void ApplyContextEvent(string payload)
  {
    if (RootTurnContextPayload.Parse(payload) is { } context)
    {
      Status.SetContext(context.ToSnapshot());
    }
  }
  /// <summary>Applies one `usage` event (issue #24): the ledger row is written in the HOST
  ///     process, so this event is the only way the status bar's session cost advances
  ///     during a host-run turn. An unreadable payload is dropped rather than rendered —
  ///     the cost line is a readout, and a notice per provider call would flood the
  ///     transcript.</summary>
  private void ApplyUsageEvent(string payload)
  {
    if (RootTurnUsagePayload.Parse(payload) is { } usage)
    {
      Status.AddUsage(usage.ToRow());
    }
  }
  /// <summary>True when a settled payload is the host's typed failure line
  ///     (<c>Error [&lt;code&gt;]: &lt;message&gt;</c>) rather than an answer. The wire carries the
  ///     outcome in the payload, so this is the only way to tell a failed turn from a
  ///     successful one that happened to start with the word 'Error'.</summary>
  private static bool IsErrorSettle(string payload)
      => payload.StartsWith("Error [", StringComparison.Ordinal)
          && payload.Contains("]: ", StringComparison.Ordinal);

  /// <summary>Converts the turn's attached image parts to their wire form, or an empty list
  ///     when the turn carries none.</summary>
  private static List<RootTurnImage> ToWireImages(IReadOnlyList<MessagePart>? imageParts)
  {
    if (imageParts is null || imageParts.Count == 0)
    {
      return [];
    }

    List<RootTurnImage> images = [];
    foreach (MessagePart part in imageParts)
    {
      // Only image parts are stageable through PendingImages; anything else would be a
      // caller fault, and dropping it silently is exactly the bug this carrier fixes.
      if (part is MessagePart.ImagePart image)
      {
        images.Add(new RootTurnImage(image.MediaType, image.Base64Data));
      }
    }

    return images;
  }

  /// <summary>Renders one host tool event as the same call/result cards the in-process
  ///     path renders.</summary>
  private static void ApplyRootTurnToolEvent(string payload, StreamBridge bridge)
  {
    if (RootTurnToolPayload.Parse(payload) is not { } tool)
    {
      bridge.OnNotice($"[host] unreadable tool event: {payload}");
      return;
    }

    if (tool.IsResult)
    {
      // The full content rides the JSON payload (issue #245): the legacy shape passed the
      // SUMMARY as the full content, so every card body read "ok". A legacy payload that
      // carried no content falls back to the summary (the old rendering), never empty.
      // The rich title travels as a ToolResult (the bridge's own carrier shape).
      // The result's images ride the payload (issue #230): every conversation runs in a
      // host process, so an image that did not travel could never be rendered. They
      // reach the same ToolResult.Images surface computer screenshots use.
      IReadOnlyList<ToolResultImage>? images = tool.Images is { Count: > 0 } wire
          ? [.. wire.Select(image => new ToolResultImage(image.MediaType, image.Base64Data))]
          : null;
      bridge.OnToolResult(tool.Name, tool.Summary, tool.Content ?? tool.Summary, tool.IsError,
          new ToolResult(tool.Content ?? tool.Summary, tool.IsError, tool.Title, images));
      return;
    }

    bridge.OnToolCall(tool.Name, tool.Arguments);
  }

  /// <summary>Handles one host session-request event (issue #234): the new_session
  ///     tool or a compaction handoff asked the app to start a NEW session in this
  ///     tab's workspace. The event arrives on the wire pump thread, so the PARSE
  ///     happens here (pure) and everything that touches the transcript or the
  ///     handler marshals onto the UI thread through <see cref="AgentSessionViewModelOptions.UiMarshal"/>.
  ///     An unreadable payload surfaces a notice instead of vanishing — a dropped
  ///     request would hide a whole handoff (the unreadable-tool-event precedent).
  ///     A faulting handler surfaces a SessionRequestFault notice: the handler is
  ///     the shell's own seam, and its fault must never crash the pump or the VM.</summary>
  private void ApplySessionRequest(string payload)
  {
    SessionRequestPayload? request;
    try
    {
      request = JsonSerializer.Deserialize<SessionRequestPayload>(payload);
    }
    catch (JsonException)
    {
      request = null;
    }

    if (request is null)
    {
      _uiMarshal(() => Transcript.AddNotice($"[host] unreadable session-request: {payload}"));
      return;
    }

    if (_sessionRequestHandler is null)
    {
      _uiMarshal(() => Transcript.AddNotice("Error [SessionRequestRefused]: this session cannot start a new one (no shell seam wired)."));
      return;
    }

    Func<SessionRequestPayload, Task> handler = _sessionRequestHandler;
    _uiMarshal(() => _ = FireSessionRequestAsync(handler, request));
  }

  /// <summary>Awaits the session-request handler and surfaces its fault as a typed
  ///     notice. Fire-and-forget by design (CA1031 named decision): the request is
  ///     one-way over the wire — the host's new_session tool already returned — and
  ///     a handler fault must be visible, never an unobserved task exception.</summary>
  private async Task FireSessionRequestAsync(Func<SessionRequestPayload, Task> handler, SessionRequestPayload request)
  {
    try
    {
      await handler(request).ConfigureAwait(true);
    }
#pragma warning disable CA1031 // Named decision: a handler fault must surface as a notice, never crash the pump.
    catch (Exception ex)
    {
      Transcript.AddNotice($"Error [SessionRequestFault]: {ex.Message}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Persists the finished turn. A compacted or shrunk turn replaces the whole
  ///     transcript (the append slice would double-count); otherwise the append starts at the
  ///     turn's own starting index, and the continuous sink — when this session has one —
  ///     skips whatever it already wrote.</summary>
  private Task PersistTurnAsync(int messageCountBefore, Result<string> result, bool replaced)
  {
    if (replaced)
    {
      return _lifecycle.ReplaceTranscriptAsync(_rootId, _conversation, ReportPersistenceError);
    }

    string assistantText = result.IsSuccess ? result.Value : string.Empty;
    return _lifecycle.AppendExchangeAsync(_rootId, _conversation, messageCountBefore,
        assistantText, ReportPersistenceError);
  }

  /// <summary>
  /// Interrupts the running turn and this session's own sub-agent subtree (FR-C6).
  /// Hard cancel: the domain repairs any half-finished tool batch so history stays
  /// valid, and surfaces the interruption as a TurnCancelled result. No-op with a
  /// notice when idle. Safe to call repeatedly; only the first call per turn has
  /// effect. The subtree interrupt is scoped to this session's root id — other
  /// sessions' children are never touched, even when they share the remote host.
  /// </summary>
  public void RequestStop()
  {
    CancellationTokenSource? cts = _turnCts;
    if (!IsBusy || cts is null)
    {
      Transcript.AddNotice("No active turn to stop.");
      return;
    }
    // Scoped teardown (FR-C6): interrupt THIS session's subtree only. A blanket
    // Interrupt() cancels every active run the runtime owns — in remote-host mode
    // that is every active child in the host, including other sessions' children.
    _childRuntime?.InterruptSubtree(_rootId);

    // Spec D6: the ROOT turn lives in the host, so the stop must reach it there. Cancelling
    // the local token alone only abandons this process's wait — the host would keep running
    // the turn and persist it.
    if (RootTurnChannel is { } channel && _liveTurnId is { Length: > 0 } turnId)
    {
      channel.InterruptRootTurn(turnId);
    }

    cts.Cancel();
  }

  /// <summary>Posts input as steering for the running turn and echoes it into the
  /// transcript ON ACCEPTANCE (issue #189): nothing renders unless the message was actually
  /// queued, so a refusal surfaces as a notice naming the reason and never claims to be
  /// queued - no user entry, no unread bump.
  ///     <para>WHERE IT GOES (issue #253): the running turn belongs to this conversation's
  ///     HOST process (spec D6), so a live host turn routes the message over the channel to
  ///     the ROOT container's inbox - the box the root loop drains and the hub its
  ///     <c>agent.wait</c> subscribes to. Posting to this container's own inbox queued it in
  ///     a process that runs no agent work: the model never saw it and no wait ever woke.
  ///     The local inbox remains the path for a session with NO host channel (the
  ///     headless/test shape), which is the only case where this container runs the turn.</para>
  ///     <para>The model sees the message at the loop's next safe point: an iteration
  ///     boundary, or - for a turn that answers without tool calls - the mid-stream safe
  ///     point right after the answer lands in history (issue #253 B2).</para></summary>
  private void Steer(string input)
  {
    // The echo is PINNED until the loop's drain fact names it (issue #253 B3). The APP mints
    // the message identity and it travels with the message (the issue #92 handshake), so the
    // drain fact - which names exactly what it consumed - can release exactly this echo.
    Guid pendingId = Guid.NewGuid();
    if (RootTurnChannel is { } channel && _liveTurnId is { Length: > 0 } turnId)
    {
      channel.SteerRootTurn(turnId, input, pendingId);
      MessageCount++;
      Transcript.AddPendingUser(input, pendingId);
      return;
    }

    if (_inbox is null)
    {
      Transcript.AddNotice("Error [NoInbox]: This session cannot accept steering messages.");
      return;
    }

    Result<bool> posted = PostWithIdentity(input, pendingId);
    if (!posted.IsSuccess)
    {
      Transcript.AddNotice($"Error [{posted.Error.Code}]: your message was not queued - {posted.Error.Message}");
      return;
    }

    MessageCount++;
    Transcript.AddPendingUser(input, pendingId);
  }

  /// <summary>Posts to the local inbox carrying the identity the echo was pinned under.
  ///     An inbox that only exposes the minting <see cref="IAgentInbox.TryPost"/> cannot be
  ///     correlated, so the echo is released immediately - the pin is a display refinement
  ///     and must never outlive a message the loop has already taken.</summary>
  private Result<bool> PostWithIdentity(string input, Guid messageId)
  {
    if (_inbox is IAgentMailbox mailbox)
    {
      return mailbox.Deliver(PendingMessage.New(input, MessageUrgency.Normal, "human") with { Id = messageId });
    }

    Result<bool> posted = _inbox!.TryPost(input);
    if (posted.IsSuccess)
    {
      Transcript.MarkDrained(messageId);
    }

    return posted;
  }

  private void ApplyStreamEvent(UiStreamEvent evt)
  {
    switch (evt)
    {
      case UiStreamEvent.Delta d:
        Status.Phase = TurnPhase.Streaming;
        Transcript.AppendAssistantDelta(d.Text);
        break;
      case UiStreamEvent.Reasoning r:
        Transcript.AppendReasoning(r.Text);
        break;
      case UiStreamEvent.IterationEnd:
        Transcript.EndIteration();
        break;
      case UiStreamEvent.ToolCallEvent tc:
        Transcript.AddToolCall(tc.Name, tc.Arguments);
        break;
      case UiStreamEvent.ToolResultEvent tr:
        // I13: a decode failure degrades to no-image (a notice) - never aborts stream delivery.
        List<TranscriptImage>? decoded = null;
        if (tr.Images is { } images && images.Count > 0)
        {
          decoded = [];
          foreach (ToolResultImage img in images)
          {
            try
            {
              decoded.Add(new TranscriptImage(Convert.FromBase64String(img.Base64Data)));
            }
            // I13 residual: ANY decode failure degrades to no-image, never aborts delivery.
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
              Transcript.AddNotice("[computer] screenshot image could not be decoded and was skipped.");
            }
          }
        }

        Transcript.AddToolResult(tr.Name, tr.Summary, tr.FullContent, tr.IsError, tr.Title, decoded);
        break;
      case UiStreamEvent.SystemMessage sm:
        // Same thread contract as notices (see below): bridge-delivered, applied on
        // the sink's thread — the UI thread in production.
        Transcript.AddSystemMessage(sm.Text);
        break;
      case UiStreamEvent.Notice n:
        // Notices arrive through the bridge (turn thread) and apply here on the sink's
        // thread — the UI thread in production — so the transcript mutation honors the
        // UI-thread-only contract, exactly like every other turn-voice event.
        Transcript.AddNotice(n.Text);
        if (_statusModelUpdater is not null && TryExtractModelId(n.Text) is { } resolved)
        {
          _statusModelUpdater(resolved);
        }
        break;
      default:
        break;
    }
  }

  /// <summary>Parses the resolved model id from a RootAgentResolver notice so the status
  ///     bar can track mid-session model changes. The ONLY live notice contract is the
  ///     resolver's preferred-pick line, "Model selected: &lt;id&gt;[ via &lt;provider&gt;]", and the
  ///     notice must START with that prefix. The old fallback contract ("... using
  ///     &lt;id&gt;.") died with the selection machinery; its loose "using " match parsed
  ///     arbitrary notice prose - tool errors, repeat-guard lines - as a model id and
  ///     clobbered the status bar's model name (issue #247). The ' via ' suffix is
  ///     dropped so the provider never leaks into the status bar's model cell.
  ///     Returns null when the notice carries no recognizable model id.</summary>
  private static string? TryExtractModelId(string notice)
  {
    const string selectedPrefix = "Model selected: ";
    if (!notice.StartsWith(selectedPrefix, StringComparison.Ordinal))
    {
      return null;
    }

    string selected = notice[selectedPrefix.Length..].Trim();
    const string viaSeparator = " via ";
    int via = selected.IndexOf(viaSeparator, StringComparison.Ordinal);
    return via >= 0 ? selected[..via].Trim() : selected;
  }

  /// <summary>Applies a stream event on the calling thread. Test seam; also usable when
  ///     the caller has already marshaled onto the UI thread.</summary>
  public Task ApplyUiStreamEventAsync(UiStreamEvent evt)
  {
    ApplyStreamEvent(evt);
    return Task.CompletedTask;
  }

  /// <summary>Production stream sink: marshals the event onto the UI thread before applying
  ///     it, keeping every <see cref="Transcript"/> mutation on the UI thread.</summary>
  public Task ApplyUiStreamEventOnUIThreadAsync(UiStreamEvent evt) =>
      Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyStreamEvent(evt)).GetTask();

  private void ReportPersistenceError(string message) => Transcript.AddNotice(message);

  /// <summary>Out-of-band supervisory notice (host health, orphan repair) posted by the
  ///     session container. Caller must be on the UI thread — the shell marshals before
  ///     invoking — because transcript mutation is UI-thread-only.</summary>
  public void AddSystemNotice(string message) => Transcript.AddNotice(message);
}

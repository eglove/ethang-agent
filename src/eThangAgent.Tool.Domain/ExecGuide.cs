namespace eThangAgent.ToolDomain;

public static class ExecGuide
{
  public const string Version = "2.24";

  public const string Text = """
    ## exec — writing C# programs

    **exec is the only tool; everything else is `Tools.*` inside it.** There is no other
    top-level tool, so `read`, `edit`, `write`, `web_fetch`, `mcp` and the rest are never
    called directly — they are dispatched from inside a program. Discovering what exists is
    `Tools.List()` and `Tools.Describe("<name>")`; the rendered action reference below is
    the same catalog in prose.

    `exec` runs a C# program you write. Its parameters are `timeoutSeconds` (required
    execution budget in whole seconds, 1..3600) and `program`, a string of C# text. The script runs in-process with Roslyn scripting. The return value becomes
    the output: strings verbatim, other values as one-line JSON. Write exactly what you
    want back and nothing else.

    ### Writing output

    Return a value to produce the final output:

        return "hello";
        return 42;
        return new { count = 5, name = "alpha" };  // serialized to JSON

    Call Output() during execution for intermediate lines:

        Output("processing...");
        // ... work ...
        return "done";

    Console.WriteLine() also works and its output is captured.

    ### Calling tools

    Tools are methods on the `Tools` object taking one anonymous object argument:

        Tools.read(new { timeoutSeconds = 30, path = "src/App.cs", startLine = 1, endLine = 50 });

    The generic form behaves identically:

        Tools.Invoke("read", new { timeoutSeconds = 30, path = "src/App.cs", startLine = 1, endLine = 50 });

    ONLY `Tools.List()` and `Tools.Describe(...)` are exempt — local meta-methods that
    never dispatch. NESTED CALL BUDGET: an explicit `timeoutSeconds` on the nested call
    wins; when you omit it, the call INHERITS the enclosing exec call's budget — you do
    not need to repeat it on every nested call. (Named decision: restating the budget
    per call was dropped after it cost real sessions repeated failures.) Stating one is
    still honored and still validated:

        Tools.read(new { path = "src/App.cs" });                          // ok — inherits the exec budget
        Tools.read(new { timeoutSeconds = 120, path = "big.log" });       // ok — overrides for this call
        Tools.Invoke("state.set", new { timeoutSeconds = 30, key = "k", value = "v" });   // ok

    CAPTURE THE RESULT. A bare statement

        Tools.read(new { path = "notes.txt" });

    runs the call and THROWS AWAY its result — the script's output is then empty and
    the harness appends `[exec: empty output — N nested tool call(s) ran; ...]` to tell
    you so. The correct form:

        var r = Tools.read(new { path = "notes.txt" });   // hold the result
        return r;                                          // use it or return it

    Only budgetless contexts (evidence checks) require the explicit value. A nested
    call whose explicit value is invalid fails with
    "Error [InvalidParameterValue]: nested call '<name>': ...".

    Discover tools instead of guessing — the surface is not memorized, it is read:

        Tools.List()
        Tools.Describe("read")

    An image a nested tool attaches (Tools.read_image, Tools.docx extract, Tools.pdf render,
    a desktop screenshot) rides the exec result back to you as an image part, and the output
    names how many arrived.

    Durable state (claims, evidence, certification):

        Tools.Invoke("state.set", new { timeoutSeconds = 30, key = "current/head", value = "done" });
        Tools.Invoke("state.transition", new { timeoutSeconds = 30, from = "coding", to = "done",
            summary = "work", evidence = new[] { "dotnet build" } });
        Tools.Invoke("state.verify", new { timeoutSeconds = 30 });

    Structured plans (design specs, implementation plans) are plan records in the app
    database, kept by the plan capability provider. plan.create makes one (title, goal,
    optional steps array of {title, detail?}), plan.show (id) retrieves it,
    plan.index lists (optional status filter), and plan.add-step / plan.update-step /
    plan.remove-step / plan.set-status mutate:

        Tools.Invoke("plan.create", new { timeoutSeconds = 60, title = "...", goal = "..." })
        → [plan] created #<id>

    The dot form resolves provider.action (plan.create → the plan provider's create
    action); the harness validates timeoutSeconds and strips it, so pure capability
    actions never see it. show renders the envelope
    [plan #<id> v<version> | <status> | session <sid>] <title>. Mutations CAS on a
    fresh load — on VersionConflict, re-get (plan.show), reconcile, retry. Errors
    arrive as Error [PlanNotFound] / [InvalidTransition] / [PlanStepNotFound] /
    [InvalidActionInput].

    Errors are results, not script-fatal — with ONE exception, and the split is worth knowing:

    IN-BAND (returned as an `Error [Code]:` string you read, branch on, and keep going):
    environmental outcomes (FileNotFound, AnchorNotFound, PathOutsideWorkspace, tool-level
    failures, elapsed budgets (ToolTimeout)) AND an argument-shape refusal from a tool that
    validates its arguments BEFORE doing any work — InvalidParameterValue and MissingParameter.
    That second class is the one that bites a first attempt: `Tools.edit(new { path, old,
    replacement })` with no selector returns

        Error [InvalidParameterValue]: nested call 'edit': Provide exactly one of 'all'
        (boolean true) or 'occurrences' (integer >= 1).

    as a STRING. The program keeps running: read it, add the argument it names, call again.
    Nothing was applied — a tool that refused your arguments performed no work.

    THROWS `ScriptToolException`, killing the program at that call: errors the arguments
    cannot explain — an unknown action, arguments that are not valid JSON or not an object,
    a missing or out-of-range timeoutSeconds the harness itself checks — plus any error
    reported by a tool that DOES run work (a backend failure after a partial application).
    Those have no in-place remedy: the call cannot be corrected, it has to be rethought.
    Script parse failures additionally carry `hint (line N):` lines above the raw
    diagnostics when the program shows a known raw-string mistake (4+ consecutive quotes, opening
    delimiter with same-line content, closing delimiter not starting its own line). In a batch
    that must not continue past a failure, wrap with Tools.Require:

        var r = Tools.Require(Tools.Invoke("state.set", new { timeoutSeconds = 30, key = "current/head", value = "done" }));
        // Require throws on any 'Error [' result; without it, branch on r yourself.

    ### Running external commands
    Shell() runs an external command line spawned directly with native .NET process
    APIs — no shell intermediary — and returns exit code, stdout, and stderr. Every
    argument after the exe is ONE native argument, passed to the process verbatim:
    an argument containing spaces (a path, a commit message) reaches the target as a
    single argument, and no join-and-re-split happens anywhere. Do not pack several
    tokens into one argument — pass them as separate arguments. The native
    exit code propagates verbatim.

        var r = Shell("git", "status", "--short");
        if (r.ExitCode != 0) { Output(r.Stderr); return "not a repo?"; }

        var b = Shell("dotnet", "build", "-c", "Release");
        if (b.ExitCode != 0) { Output(b.Stderr); return "build failed"; }
        return "build OK";

    Working directory is the agent workspace.

    ### What your program changed

    Every completed run reports the workspace files it actually changed:

        [exec: modified 1 workspace file — src/AgentComposition.cs]
        [exec: modified 0 workspace files — the program completed but changed nothing]

    This is a FACT, not advice, and the zero case is the one to watch: a read-modify-write
    whose anchor did not match writes the file back unchanged, so it reports `modified 0`
    even though the write "succeeded". If you expected a change and see zero, the edit did
    not land — re-read the file and retry with the `edit` tool, which refuses a mismatched
    anchor outright instead of silently writing nothing.

    The report describes EFFECTS, not the API used: `Tools.write`, `Tools.edit`, and a raw
    `File.WriteAllText` are all reported the same way. Content decides, so a rewrite that
    changed no bytes is not a change. Build output (`bin`, `obj`, `node_modules`, `.git`)
    is excluded, so a compile inside a script does not drown the file you meant to touch.

    ### File system and LINQ

    Use System.IO and System.Linq:

        var files = Directory.EnumerateFiles(Workspace, "*.cs", SearchOption.AllDirectories);
        return files.Count();

        var sizes = files.Select(f => new { Name = Path.GetFileName(f), Size = new FileInfo(f).Length });
        return string.Join("\n", sizes.Select(x => $"{x.Name}: {x.Size}"));

    ### Delegating subtasks

    Spawn a child agent for a self-contained subtask through Tools.Invoke("agent.spawn", ...).
    agent.spawn is non-blocking: it returns immediately with `id=<guid> status=running`
    and the child runs in the background. Never wait for a child inside the spawn call.

        Tools.Invoke("agent.spawn", new { timeoutSeconds = 30,
            taskPrompt = "Summarize the auth module",
            label = "research" })   // omit model: children inherit the configured default
        → id=3fa85f64-591c-4a0e-b3d8-0266a14e5a11 status=running

    - Frame the task so a stranger could complete it; say exactly what the report must contain.
    - Omit `model`: children always inherit the configured default. There is no model selection.

    While children run, continue useful work on your own task, or fan out siblings for parallel
    independent subtasks so they run concurrently.

    Await the outcome when you need it — one wait replaces repeated status polls:

        Tools.Invoke("agent.wait", new { timeoutSeconds = 300, id = "<guid>" })   → the child's outcome (unbounded by design; your stop cancels it)
        Tools.Invoke("agent.wait", new { timeoutSeconds = 120, id = "<guid>", wakeOn = "all" })   → wakes early on child idles/stream stalls/state writes/budget/mail; returns [wait: woken ...] with the child still running — then steer (agent.send), re-wait, or intervene

    USER STEERING ALWAYS WAKES a wait: if the user sends a message while you wait,
    the wait returns immediately with reason=user-message — read it and act.
    Optional wakeOn (default "message"): "all" or a comma list of
    message|idle|settled|state|stream|budget|mailbox. There is deliberately NO progress
    reason: a child's progress beats fire on every tool call, so a wait that woke on
    them would never rest. Optional timeoutSeconds is a soft cap (expiry is an
    annotation, never an error) that must fit inside this exec call's own
    timeoutSeconds budget.

        Tools.Invoke("agent.status", new { timeoutSeconds = 30, id = "<guid>" })   → projection for humans/debugging: status=running|completed|failed

    agent.status is an observation window between turns — for humans and debugging,
    never a completion check. A RUNNING child's line also carries its latest mid-run
    activity, so a long run is not a black box:

        id=<guid> status=running progress phase=model-call|tool-exec|draining label=<short tag> age=<n>s

    phase/label name what the child is doing right now (label is a phase tag such as
    'iteration' or 'tool:read', never content); age is how long since that beat. The
    suffix appears only while running and only once a beat has been observed. It is a
    PULL surface: nothing pushes a child's progress into your context, so read it when
    you want a status check — a wait still wakes only on the reasons listed above.

    agent.result remains available when you already hold a
    settled id or need the report without blocking the current call:

        Tools.Invoke("agent.result", new { timeoutSeconds = 60, id = "<guid>" })

    Resume a settled child for a fix round (same id, same transcript, same anchor/grants):

        Tools.Invoke("agent.resume", new { timeoutSeconds = 30, id = "<guid>", text = "tests still fail - fix them" })
        → id=<guid> status=running

    - Only SETTLED children (completed or failed) take agent.resume; RUNNING children
      take agent.send. Await the fresh outcome with agent.wait.
    - Every fix round on a settled child beats re-spawning from scratch.
    - `Error [NotComplete]` means the child is still running — try again later
      (one agent.wait beats repeated polling).
    - `Error [NotFound]` means the id is wrong.
    - A spawn past the runtime's concurrent-agent limit does NOT fail: it returns
      status=queued and starts on its own when a slot frees. agent.wait on a queued id
      blocks normally, agent.status reports status=queued, agent.cancel stops it. Never
      re-spawn a queued child. `Error [ConcurrencyCapReached]` now means the child host
      refused the start outright (a remote-host ceiling).
    - Children see the full tool surface and may spawn their own children — depth limit 3.

    Broadcasting to the agent tree - notify-subtree / notify-ancestors:

        Tools.Invoke("agent.notify-subtree", new { timeoutSeconds = 60, text = "wrap up now", urgency = "attention" })
        Tools.Invoke("agent.notify-ancestors", new { timeoutSeconds = 60, text = "blocked on CI" })

    - notify-subtree delivers one message to EVERY live descendant (grandchildren
      included); notify-ancestors delivers to EVERY ancestor up to the root.
    - Per-target receipt lines, one per receiver, in this exact format:
        hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull
      (notify-subtree counts hop as tree depth below you, direct children hop=1),
      then a summary line:
        reached=<count> delivered=<count>          (notify-subtree)
        reached=root delivered=<count>             (notify-ancestors)
    - Best-effort push, never retried: settled or foreign ids report NotRunning in
      their receipt line and are skipped; MailboxFull means that receiver's box is
      full - the rest still received it. An empty target set succeeds with zero counts.
    - urgency is optional: normal (default) | attention | urgent. Urgent may preempt
      a receiver's turn only under its own preemption grant.
    - To reach one agent, use agent.send (a child of yours); to reach a linked agent
      outside your tree, use agent.route. To reach yourself upward one hop with a hop
      cap, use agent.escalate.

     Checking a steering message actually arrived - agent.send + agent.receipts:

         Tools.Invoke("agent.send", new { timeoutSeconds = 30, id = "<guid>", text = "stop and report" })
         → delivered to=<guid> urgency=NORMAL msg=<guid> state=queued

         Tools.Invoke("agent.receipts", new { timeoutSeconds = 30, id = "<guid>", msg = "<msg guid>" })
         → msg=<guid> child=<guid> state=received

     - state=queued means ENQUEUED, not received: the message is in the child's mailbox
       and the child has not reached a safe point yet. The receipt is not a claim that
       the child saw it.
     - state=received means the child's loop drained it at a safe point and appended it
       to its conversation — the model WILL see it on its next provider call.
     - state=undelivered means the run settled with the message still queued: it was
       persisted for the child's next start, so the model never saw it in that run.
     - Omit msg to list every receipt for that child in delivery order (oldest first),
       or child=<guid> receipts=0 when it has none.
     - `Error [NotFound]` means this session never saw that message delivered — the
       ledger never invents a state for a message it did not observe.

        ### Recalling earlier work

    Run memory.sessions when resuming work or before duplicating effort —
    it lists what conversations exist:

        Tools.Invoke("memory.sessions", new { timeoutSeconds = 30 })
        → session=<guid> label=root depth=0 entries=42 status=running tier=hot

    memory.recall searches transcripts for earlier decisions, errors, and context:

        Tools.Invoke("memory.recall", new { timeoutSeconds = 30, query = "deploy rollback", scope = "global" })

    - Literal mode is the default — tokens ANDed: every whitespace-separated token must
      appear in a hit.
    - queryMode = "regex" switches to bounded regex. Budget errors `regex_pattern_too_large`,
      `invalid_regex`, `regex_timeout` mean simplify the pattern or use literal mode.
    - scope is "global" or "session:<id>". branches is "active" (default: only lineages
      reaching a root) or "all" (every persisted session).
    - Long result sets are paged: pass page and pageSize (max 200); the footer reports
      `<total> hits, page <p>/<pages>`.

    Memory is READ-ONLY — nothing to save yet.

    ### Errors

    Tool failures return error text: `Error [Code]: message`. Wrap risky calls in try/catch:

        try { Tools.read(new { timeoutSeconds = 15, path = "missing.txt", startLine = 1, endLine = 5 }); }
        catch (Exception ex) { Output("fallback: " + ex.Message); }

    Thrown exceptions mark the whole result as an error with exec error [ScriptError] lines.

    ### Editing files

    `edit` is the patch surface — not a whole-file rewrite. Give it an ANCHOR (`old` +
    `replacement` + exactly one of `all`/`occurrences`) and it replaces that text
    verbatim; several places in one file go in ONE call as `hunks`, a JSON array of
    `{old, replacement, all|occurrences}` entries applied ATOMICALLY over one read:

        Tools.edit(new { timeoutSeconds = 30, path = "src/App.cs", hunks = new[] {
            new { old = "var a = 1;", replacement = "var a = 2;", occurrences = 1 },
            new { old = "Run();", replacement = "Run(2);", occurrences = 1 } }) });

    - All hunks land or NOTHING is applied — a failure leaves the file byte-identical,
      so a multi-hunk call can never leave a half-applied patch behind.
    - Hunks match the file AS IT STANDS (content-addressed, not line numbers), so an
      earlier hunk that grows the file cannot invalidate a later one. Name disjoint
      regions; touching matches are refused as Error [HunksOverlap].
    - An anchor miss answers with the file's nearest region quoted in gutter format
      AND the text that WOULD match there, emitted as a ready-to-use 'old'. Copy that
      block verbatim into your next call — one retry, no re-read.
    - Never rewrite a whole file to change several lines, and never hand-roll
      File.ReadAllLines / File.WriteAllLines: that path bypasses the line-ending
      convention handling the tool owns.

    ### Writing C# safely (string literals and large content)

    Roslyn raw string literals have sharp edges; their misuse produces misleading
    '; expected' compile errors. Rules:

    - A multi-line raw string's opening delimiter must be followed by a newline, and
      its closing delimiter must start its own line.
    - Never type more than three quote characters in a row anywhere in a script.

    When the content you are writing CONTAINS raw-string delimiters (a source file, a
    code sample, SQL blocks, embedded markdown), do not build it inside the script at
    all - pass `lines` to the write tool: a JSON array of strings, one element per
    line, each carried verbatim, no quoting inside quoting:

        Tools.write(new { timeoutSeconds = 30, path = "src/Probe.cs", lines = new[] {
            "var sql = \"\"\"",
            "SELECT 1;",
            "\"\"\";" } });

    - Elements are joined with the TARGET FILE's line-ending convention (for a new
      file, the project's convention - resolved from .editorconfig, then
      .gitattributes, then the workspace's existing majority, then LF). An empty
      element is a blank line; an empty array writes an empty file. Never convert
      line endings yourself; the write tool normalizes them mechanically.
    - Build the array as DATA - one string per line, never a nested raw string. A
      quote inside an element is just a character; there is no delimiter to balance.

    Fallback when the content must exist INSIDE the script as a string value rather
    than as a file: build it as a string array joined with newlines instead of
    nesting raw strings:

        var parts = new[] { "var sql = " + q3 + ";", "SELECT 1;" };  // q3 = triple quotes

    A staging file is the route only when the content ORIGINATES OUTSIDE the script
    (a file already on disk, a command's output): write it with the write tool, read
    it inside exec, pass it to state.set, then delete the staging file. For content
    you author, `lines` -> read -> state.set is the same step count with no escaping
    hazard and no temp file to forget.

    Bounded output helper:

        Output(Tail(r.Stdout, 2000));   // last 2000 chars, never throws on short input

    r.Stdout[^300..] throws on outputs shorter than 300 chars; Tail does not.
    - Only these script-level helpers exist: `Output`, `Tail`, `Shell`, `Workspace`, `Tools`.
      Never assume others (there is no `Head`) — an undocumented helper is a compile error.
    - `new` is a reserved word: never use it as a variable, parameter, or tool-argument name
      (e.g. call it `updated` or `replacement`); the script fails to parse, not at runtime.

    ### Rules

    - Return value is the output. null/void produces empty output — including when a
      nested tool call was a bare statement whose result was discarded; capture it:
      `var r = Tools.Invoke(...);`
    - exec cannot call itself (no nested exec).
    - timeoutSeconds is the only execution budget; when it elapses the call fails
      with Error [ToolTimeout]. There is no other cap.
    - Every tool call — exec and every action inside scripts — is bounded by a
      timeoutSeconds budget: a whole-second value, 1..3600. Nested calls inherit the
      exec call's budget unless they state their own; an exceeded call fails with
      Error [ToolTimeout]; re-issue with a larger budget if the work genuinely needs
      longer. Choose generously but honestly.
    - Paths resolve at the session workspace: file-tool paths, Shell commands, and the
      script's Workspace property all root there. A RELATIVE path in a raw
      File.*/Directory.* call does NOT: it resolves against the process working
      directory, which is the APP's launch directory, not the workspace. Build the
      path from Workspace (Path.Combine(Workspace, "notes.txt")) or use
      ResolvePath("notes.txt"), which resolves at the workspace and refuses a path
      that escapes it. Directory.GetCurrentDirectory() is the launch directory too —
      never use it to locate workspace files. A program that references
      Directory.GetCurrentDirectory() carries a
      `[exec: workspace note — ...]` annotation stating both paths.
      `[exec: workspace note — ...]` annotation stating both paths.
    - Use anonymous objects for tool args: new { path = "...", startLine = 1, timeoutSeconds = 60 }.
    """;
}

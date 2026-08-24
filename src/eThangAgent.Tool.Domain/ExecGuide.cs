namespace eThangAgent.ToolDomain;

public static class ExecGuide
{
    public const string Version = "2.2";

    public const string Text = """
    ## exec — writing C# programs

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

        Tools.read(new { path = "src/App.cs", startLine = 1, endLine = 50 });
        Tools.search_files(new { pattern = "TODO", regex = false, rootPath = ".", maxResults = 20, contextLines = 2 });

    The generic form behaves identically:

        Tools.Invoke("read", new { path = "src/App.cs", startLine = 1, endLine = 50 });

    Discover tools instead of guessing:

        Tools.List()
        Tools.Describe("read")

    Durable state (claims, evidence, certification):

        Tools.Invoke("state.set", new { key = "current/head", value = "done" });
        Tools.Invoke("state.transition", new { from = "coding", to = "done",
            summary = "work", evidence = new[] { "dotnet build" } });
        Tools.Invoke("state.verify", new { });

    ### Running external commands

    Shell() runs an external command line through powershell -NoProfile and returns
    exit code, stdout, and stderr. Every argument after the exe is one token of a single
    native command line; a multi-token piece such as "build -c Release" is re-parsed as
    separate tokens instead of reaching the exe as one quoted literal argument. The
    native exit code propagates verbatim.

        var r = Shell("git", "status", "--short");
        if (r.ExitCode != 0) { Output(r.Stderr); return "not a repo?"; }

        var b = Shell("dotnet", "build", "-c", "Release");
        if (b.ExitCode != 0) { Output(b.Stderr); return "build failed"; }
        return "build OK";

    Working directory is the agent workspace.

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

        Tools.Invoke("agent.spawn", new {
            taskPrompt = "Summarize the auth module", model = "provider/cheap-model",
            label = "research" })
        → id=3fa85f64-591c-4a0e-b3d8-0266a14e5a11 status=running

    - Frame the task so a stranger could complete it; say exactly what the report must contain.
    - Pick a cheap model for grunt work; omit `model` to use the configured default.

    While children run, continue useful work on your own task, or fan out siblings for parallel
    independent subtasks so they run concurrently.

    Poll each child's progress between turns:

        Tools.Invoke("agent.status", new { id = "<guid>" })   → id=<guid> status=running|completed|failed

    When a child is done, fetch its final report:

        Tools.Invoke("agent.result", new { id = "<guid>" })

    - `Error [NotComplete]` means the child is still running — try again later.
    - `Error [NotFound]` means the id is wrong.
    - `Error [ConcurrencyCapReached]` from agent.spawn means the runtime is at its
      concurrent-agent limit — retrieve pending results before spawning more.
    - Children see the full tool surface and may spawn their own children — depth limit 3.

    ### Recalling earlier work

    Run memory.sessions when resuming work or before duplicating effort —
    it lists what conversations exist:

        Tools.Invoke("memory.sessions", new { })
        → session=<guid> label=root depth=0 entries=42 status=running tier=hot

    memory.recall searches transcripts for earlier decisions, errors, and context:

        Tools.Invoke("memory.recall", new { query = "deploy rollback", scope = "global" })

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

        try { Tools.read(new { path = "missing.txt", startLine = 1, endLine = 5 }); }
        catch (Exception ex) { Output("fallback: " + ex.Message); }

    Thrown exceptions mark the whole result as an error with exec error [ScriptError] lines.

    ### Writing C# safely (string literals and large content)

    Roslyn raw string literals have sharp edges; their misuse produces misleading
    '; expected' compile errors. Rules:

    - A multi-line raw string's opening delimiter must be followed by a newline, and
      its closing delimiter must start its own line.
    - Never type more than three quote characters in a row anywhere in a script.
    - To generate code that itself contains triple-quote delimiters (SQL blocks,
      embedded markdown), build it as a string array joined with newlines instead of
      nesting raw strings:

        var parts = new[] { "var sql = " + q3 + ";", "SELECT 1;" };  // q3 = triple quotes

    - To store large multi-line content (markdown, plans, reports) into state keys,
      write it to a staging file with the write tool, read it inside exec, pass it to
      state.set, then delete the staging file. Do not fight raw-string escaping inline.

    Bounded output helper:

        Output(Tail(r.Stdout, 2000));   // last 2000 chars, never throws on short input

    r.Stdout[^300..] throws on outputs shorter than 300 chars; Tail does not.
    ### Rules

    - Return value is the output. null/void produces empty output.
    - Output over 50,000 characters is truncated; full text saved to [exec:artifact <path>].
    - exec cannot call itself (no nested exec).
    - The script is stopped at your timeoutSeconds budget and also at a 120s hard cap,
      whichever comes first.
    - Every tool call — exec and every action inside scripts — REQUIRES a timeoutSeconds
      argument: a whole-second budget, 1..3600. A call without it fails with MissingParameter,
      and a call exceeding its budget fails with Error [ToolTimeout]; re-issue with a larger
      budget if the work genuinely needs longer. Choose generously but honestly.
    - Use anonymous objects for tool args: new { path = "...", startLine = 1, timeoutSeconds = 60 }.
    """;
}

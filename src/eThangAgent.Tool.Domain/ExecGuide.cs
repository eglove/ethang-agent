namespace eThangAgent.ToolDomain;

public static class ExecGuide
{
    public const string Version = "1.5";

    public const string Text = """
    ## exec — writing PowerShell programs

    `exec` runs a PowerShell program you write. Its only parameter is `program`, a string of
    PowerShell text. The script's output stream is returned to you: strings verbatim, other
    objects as one-line JSON. Write exactly what you want back and nothing else.

    ### Calling tools inside a program

    Registered tools are functions that take ONE hashtable argument:

        read @{ path = "src/App.cs"; startLine = 1; endLine = 50 }

    The generic form behaves identically:

        Invoke-AgentTool -Name read -ToolInput @{ path = "src/App.cs"; startLine = 1; endLine = 50 }

    Discover tools instead of guessing:

        Get-AgentTool

    Full documentation for any action (description + parameter docs):

        Get-AgentAction read

    Providers:

        Get-AgentProvider

    Durable state (claims, evidence, certification):

        state.set @{ key = 'current/head'; value = 'done' }
        state.transition @{ from = 'coding'; to = 'done'; summary = 'work';
            evidence = @('dotnet build') }
        state.verify @{}

    ### Delegating subtasks

    Spawn a child agent for a self-contained subtask. `agent.spawn` is non-blocking: it
    returns immediately with `id=<guid> status=running` and the child runs in the background.
    Never wait for a child inside the spawn call.

        agent.spawn @{ taskPrompt = 'Summarize the auth module'; model = 'provider/cheap-model';
            label = 'research' }
        → id=3fa85f64-591c-4a0e-b3d8-0266a14e5a11 status=running

    - Frame the task so a stranger could complete it; say exactly what the report must contain.
    - Pick a cheap model for grunt work; omit `model` to use the configured default.

    While children run, continue useful work on your own task, or fan out siblings for parallel
    independent subtasks so they run concurrently.

    Poll each child's progress between turns:

        agent.status @{ id = '<guid>' }   → id=<guid> status=running|completed|failed

    When a child is done, fetch its final report:

        agent.result @{ id = '<guid>' }

    - `Error [NotComplete]` means the child is still running — try again later.
    - `Error [NotFound]` means the id is wrong.
    - `Error [ConcurrencyCapReached]` from `agent.spawn` means the runtime is at its
      concurrent-agent limit — retrieve pending results before spawning more.
    - Children see the full tool surface and may spawn their own children — depth limit 3.

    ### Recalling earlier work

    Run `memory.sessions` when resuming work or before duplicating effort — it
    lists what conversations exist:

        memory.sessions @{}
        → session=<guid> label=root depth=0 entries=42 status=running tier=hot

    `memory.recall` searches transcripts for earlier decisions, errors, and context:

        memory.recall @{ query = 'deploy rollback'; scope = 'global' }

    - Literal mode is the default — tokens ANDed: every whitespace-separated token must
      appear in a hit.
    - queryMode = 'regex' switches to bounded regex. Budget errors `regex_pattern_too_large`,
      `invalid_regex`, `regex_timeout` mean simplify the pattern or use literal mode.
    - scope is 'global' or 'session:<id>'. branches is 'active' (default: only lineages
      reaching a root) or 'all' (every persisted session).
    - Long result sets are paged: pass page and pageSize (max 200); the footer reports
      `<total> hits, page <p>/<pages>`.

    Memory is READ-ONLY — nothing to save yet.

    ### Errors

    Tool failures throw terminating errors — catch them with try/catch:

        try { read @{ path = "missing.txt"; startLine = 1; endLine = 5 } }
        catch { Write-Output ("fallback: " + $_.Exception.Message) }

    Error text follows 'Error [Code]: message'. Write-Error marks the whole result as an error.

    ### Rules

    - Output over 50,000 characters is truncated; the full text lands in a file reported as
      [exec:artifact <path>] — read that file with `read`.
    - exec cannot call itself (no nested exec).
    - A 120s timeout stops the pipeline; keep programs small and chunked.
    - Argument hashtables hold strings, numbers, booleans, and arrays only — no scriptblocks
      or live objects.
    """;
}

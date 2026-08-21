namespace eThangAgent.ToolDomain;

public static class ExecGuide
{
    public const string Version = "1.2";

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

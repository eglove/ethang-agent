namespace eThangAgent.ToolDomain;

public static class ExecGuide
{
    public const string Version = "2.0";

    public const string Text = """
    ## exec — writing C# programs

    `exec` runs a C# program you write. Its only parameter is `program`, a string of
    C# text. The script runs in-process with Roslyn scripting. The return value becomes
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

    Discover available tools:

        Tools.List()
        Tools.Describe("read")

    ### Running external commands

    Shell() runs an external process and returns exit code, stdout, and stderr:

        var r = Shell("dotnet", "build");
        if (r.ExitCode != 0) { Output(r.Stderr); return "build failed"; }
        return "build OK";

    Working directory is the agent workspace.

    ### File system and LINQ

    Use System.IO and System.Linq:

        var files = Directory.EnumerateFiles(Workspace, "*.cs", SearchOption.AllDirectories);
        return files.Count();

        var sizes = files.Select(f => new { Name = Path.GetFileName(f), Size = new FileInfo(f).Length });
        return string.Join("\n", sizes.Select(x => $"{x.Name}: {x.Size}"));

    ### Delegating subtasks

    agent.spawn is available via Tools.Invoke():

        var id = Tools.Invoke("agent.spawn", new { taskPrompt = "Summarize auth module", model = "provider/cheap-model", label = "research" });
        return id;

    Poll progress:

        Tools.Invoke("agent.status", new { id = "<guid>" });
        Tools.Invoke("agent.result", new { id = "<guid>" });

    ### Recalling earlier work

        Tools.Invoke("memory.sessions", new { });
        Tools.Invoke("memory.recall", new { query = "deploy rollback", scope = "global" });

    ### State

        Tools.Invoke("state.set", new { key = "current/head", value = "done" });
        Tools.Invoke("state.get", new { key = "current/head" });

    ### Errors

    Tool failures return error text: `Error [Code]: message`. Wrap in try/catch:

        try { Tools.read(new { path = "missing.txt", startLine = 1, endLine = 5 }); }
        catch (Exception ex) { Output("fallback: " + ex.Message); }

    ### Rules

    - Return value is the output. null/void produces empty output.
    - Output over 50,000 characters is truncated; full text saved to [exec:artifact <path>].
    - exec cannot call itself (no nested exec).
    - A 120s timeout stops the script.
    - Use anonymous objects for tool args: new { path = "...", startLine = 1 }.
    """;
}

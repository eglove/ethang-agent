using System.ComponentModel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Stub MCP stdio server for containment tests (issue #105): a REAL MCP server the
// harness spawns as a child process over stdio. Tools model the test behaviors:
//   echo     - echoes the text back (the happy-path tool).
//   dump_env - returns the process environment as text (pins B1: the allowlist).
//   chatty   - writes several stderr lines, then returns text (pins B3).
//   crash    - exits the process immediately (pins the structured reconnect).
// With the --idle argument the server starts, stays alive, and never speaks MCP -
// the failed-handshake containment test's subject.
if (args.Contains("--idle", StringComparer.Ordinal))
{
    await Task.Delay(Timeout.InfiniteTimeSpan, new CancellationToken(canceled: true));
    return 0;
}

McpServerOptions options = new()
{
    ServerInfo = new Implementation { Name = "ethang-mcp-stubserver", Version = "1.0.0" },
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
    ToolCollection = [],
};

options.ToolCollection.Add(McpServerTool.Create(
    ([Description("Echoes the text back.")] string text) => "echo: " + text,
    new McpServerToolCreateOptions { Name = "echo", Description = "Echoes the text back." }));

options.ToolCollection.Add(McpServerTool.Create(
    ([Description("Dumps the process environment.")] () =>
        string.Join("\n", Environment.GetEnvironmentVariables().Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal))),
    new McpServerToolCreateOptions { Name = "dump_env", Description = "Dumps the process environment." }));

options.ToolCollection.Add(McpServerTool.Create(
    ([Description("Writes to stderr then returns text.")] (string text) =>
    {
        Console.Error.WriteLine("stub-stderr-line-1: " + text);
        Console.Error.WriteLine("stub-stderr-line-2");
        return "chatty: " + text;
    }),
    new McpServerToolCreateOptions { Name = "chatty", Description = "Writes to stderr then returns text." }));

options.ToolCollection.Add(McpServerTool.Create(
    [Description("Exits the process immediately.")] () =>
    {
        Environment.Exit(9);
        return "unreachable";
    },
    new McpServerToolCreateOptions { Name = "crash", Description = "Exits the process immediately." }));

McpServer server = McpServer.Create(new StdioServerTransport(options), options);
await server.RunAsync();
return 0;

using System.Collections;
using System.Management.Automation;
using eThangAgent.CapabilityDomain;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

public class ToolBrokerTests
{
    private static CapabilityRegistry Registry(ICapabilityProvider provider)
        => CapabilityRegistry.Create([provider]);

    private static ICapabilityProvider ReadProvider(IFileSystemAccess? files = null)
        => new AgentToolsProvider("agent",
            [new AgentToolBinding(new ReadTool(files ?? new FakeFileSystemAccess()),
                "Read lines from a text file.")]);

    [Fact]
    public void InvokeTool_UnknownAction_Throws_ListingAvailable()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("nope", new Hashtable()));

        Assert.Contains("Error [UnknownAction]:", ex.Message);
        Assert.Contains("read", ex.Message);
    }

    [Fact]
    public void InvokeTool_ByFullRef_Resolves()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var content = broker.InvokeTool("agent.read",
            new Hashtable { ["path"] = "x.txt", ["startLine"] = 1, ["endLine"] = 2 });

        Assert.Contains("[read x.txt lines 1-2 of 2 total]", content);
    }

    [Fact]
    public void InvokeTool_NullInput_Throws()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var ex = Assert.Throws<ExecToolCallException>(() => broker.InvokeTool("read", null));

        Assert.Contains("Error [InvalidToolInput]:", ex.Message);
    }

    [Fact]
    public void InvokeTool_ScriptBlockInput_Throws()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var ex = Assert.Throws<ExecToolCallException>(() => broker.InvokeTool("read",
            new Hashtable { ["path"] = ScriptBlock.Create("{ 1 }") }));

        Assert.Contains("Error [InvalidToolInput]:", ex.Message);
    }

    [Fact]
    public void InvokeTool_ConvertsInput_AndReturnsContent()
    {
        RawToolInput? received = null;
        var tool = new RecordingTool("read", r => received = r, "file content");
        var broker = new ToolBroker(Registry(new AgentToolsProvider("agent",
            [new AgentToolBinding(tool, "Read.")])));

        var content = broker.InvokeTool("read", new Hashtable { ["path"] = "a.txt" });

        Assert.Equal("file content", content);
        Assert.NotNull(received);
        Assert.Equal("read", received!.Name);
        Assert.Contains("\"path\":\"a.txt\"", received.JsonArguments);
    }

    [Fact]
    public void InvokeTool_ActionError_Throws_WithContent()
    {
        var tool = new RecordingTool("read", _ => { }, "Error [FileNotFound]: nope.", isError: true);
        var broker = new ToolBroker(Registry(new AgentToolsProvider("agent",
            [new AgentToolBinding(tool, "Read.")])));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("read", new Hashtable { ["path"] = "a.txt" }));

        Assert.Equal("Error [FileNotFound]: nope.", ex.Message);
    }

    [Fact]
    public void ListActions_CompactListing_NoExec()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var listing = broker.ListActions();

        Assert.Contains("read(path: String, startLine: Integer, endLine: Integer)", listing);
        Assert.DoesNotContain("exec(", listing);
    }

    [Fact]
    public void DescribeAction_ReturnsFullDescriptor()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var doc = broker.DescribeAction("read");

        Assert.Contains("read — Read lines from a text file.", doc);
        Assert.Contains("annotation line", doc);
        Assert.Contains("- path: String —", doc);
    }

    [Fact]
    public void DescribeAction_Unknown_Throws()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        Assert.Throws<ExecToolCallException>(() => broker.DescribeAction("nope"));
    }

    [Fact]
    public void ListProviders_ShowsIdAndCount()
    {
        var broker = new ToolBroker(Registry(ReadProvider()));

        var listing = broker.ListProviders();

        Assert.Equal("agent (1 actions)", listing);
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha", "beta"], 2, 2)));
    }

    private sealed class RecordingTool : ITool
    {
        private readonly Action<RawToolInput> _onExecute;
        private readonly string _content;
        private readonly bool _isError;

        public RecordingTool(string name, Action<RawToolInput> onExecute, string content,
            bool isError = false)
        {
            _onExecute = onExecute;
            _content = content;
            _isError = isError;
            Definition = new ToolDefinition(name, "desc", []);
        }

        public ToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        {
            _onExecute(input);
            return Task.FromResult(new ToolResult(_content, _isError));
        }
    }
}

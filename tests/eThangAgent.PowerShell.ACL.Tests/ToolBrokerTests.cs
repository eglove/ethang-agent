using System.Collections;
using System.Management.Automation;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

public class ToolBrokerTests
{
    [Fact]
    public void WrappableDefinitions_ExcludesExec()
    {
        var registry = new ToolRegistry([
            new FakeTool("read"),
            new FakeTool(ExecTool.ToolName)]);
        var broker = new ToolBroker(registry);

        var definitions = broker.WrappableDefinitions;

        Assert.Single(definitions);
        Assert.Equal("read", definitions[0].Name);
    }

    [Fact]
    public void DescribeTools_FormatsOneLinePerTool_ExcludesExec()
    {
        var registry = new ToolRegistry([
            new ReadTool(new FakeFileSystemAccess()),
            new FakeTool(ExecTool.ToolName)]);
        var broker = new ToolBroker(registry);

        var listing = broker.DescribeTools();

        Assert.Contains("read(", listing);
        Assert.Contains("path: String", listing);
        Assert.DoesNotContain("exec(", listing);
    }

    [Fact]
    public void InvokeTool_UnknownTool_Throws()
    {
        var broker = new ToolBroker(new ToolRegistry([new FakeTool("read")]));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("nope", new Hashtable()));

        Assert.Contains("Error [UnknownTool]: Unknown tool: nope", ex.Message);
    }

    [Fact]
    public void InvokeTool_NullInput_Throws()
    {
        var broker = new ToolBroker(new ToolRegistry([new FakeTool("read")]));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("read", null));

        Assert.Contains("Error [InvalidToolInput]:", ex.Message);
    }

    [Fact]
    public void InvokeTool_ScriptBlockInput_Throws()
    {
        var broker = new ToolBroker(new ToolRegistry([new FakeTool("read")]));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("read",
                new Hashtable { ["path"] = ScriptBlock.Create("{ 1 }") }));

        Assert.Contains("Error [InvalidToolInput]:", ex.Message);
    }

    [Fact]
    public void InvokeTool_ConvertsInput_AndReturnsContent()
    {
        RawToolInput? received = null;
        var tool = new RecordingTool("read", r => received = r, "file content");
        var broker = new ToolBroker(new ToolRegistry([tool]));

        var content = broker.InvokeTool("read", new Hashtable { ["path"] = "a.txt" });

        Assert.Equal("file content", content);
        Assert.NotNull(received);
        Assert.Equal("read", received!.Name);
        Assert.Contains("\"path\":\"a.txt\"", received.JsonArguments);
    }

    [Fact]
    public void InvokeTool_ToolError_Throws_WithToolResultContent()
    {
        var tool = new RecordingTool("read", _ => { }, "Error [FileNotFound]: nope.", isError: true);
        var broker = new ToolBroker(new ToolRegistry([tool]));

        var ex = Assert.Throws<ExecToolCallException>(
            () => broker.InvokeTool("read", new Hashtable { ["path"] = "a.txt" }));

        Assert.Equal("Error [FileNotFound]: nope.", ex.Message);
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha", "beta"], 2, 2)));
    }

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name)
            => Definition = new ToolDefinition(name, "desc", []);

        public ToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
            => Task.FromResult(new ToolResult("ok", false));
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

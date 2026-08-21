using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class AgentToolsProviderTests
{
    private static AgentToolsProvider Create() =>
        new("agent",
            [new AgentToolBinding(new ReadTool(new FakeFileSystemAccess()), "Read lines from a text file.")]);

    [Fact]
    public void Actions_MappedFromToolDefinitions()
    {
        var action = Assert.Single(Create().Actions);

        Assert.Equal("read", action.Name);
        Assert.Equal("Read lines from a text file.", action.Summary);
        Assert.Contains("annotation", action.Description);
        Assert.Equal(3, action.Parameters.Count);
        Assert.Contains(action.Parameters, p => p.Name == "path" && p.Type == "String");
        Assert.Contains(action.Parameters, p => p.Name == "startLine" && p.Type == "Integer");
    }

    [Fact]
    public async Task InvokeAsync_DelegatesToTool_AndReturnsContent()
    {
        var result = await Create().InvokeAsync("read",
            """{"path":"x.txt","startLine":1,"endLine":2}""");

        Assert.False(result.IsError);
        Assert.Contains("[read x.txt lines 1-2 of 2 total]", result.Content);
        Assert.Contains("alpha", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_ToolError_CarriesIsErrorAndGutter()
    {
        var provider = new AgentToolsProvider("agent",
            [new AgentToolBinding(new ReadTool(new FailingFileSystemAccess()), "Read lines.")]);

        var result = await provider.InvokeAsync("read",
            """{"path":"missing.txt","startLine":1,"endLine":5}""");

        Assert.True(result.IsError);
        Assert.Contains("Error [FileNotFound]:", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_UnknownAction_ReturnsError()
    {
        var result = await Create().InvokeAsync("nope", "{}");

        Assert.True(result.IsError);
        Assert.Contains("Error [UnknownAction]: Unknown action: nope", result.Content);
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(new FileRead(["alpha", "beta"], 2, 2)));
    }

    private sealed class FailingFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Failure(
                new Error("FileNotFound", $"File not found: {path}.")));
    }
}

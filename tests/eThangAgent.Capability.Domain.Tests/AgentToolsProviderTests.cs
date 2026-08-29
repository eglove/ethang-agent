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
    ActionDescriptor action = Assert.Single(Create().Actions);

    Assert.Equal("read", action.Name);
    Assert.Equal("Read lines from a text file.", action.Summary);
    Assert.Contains("annotation", action.Description, StringComparison.Ordinal);
    Assert.Equal(4, action.Parameters.Count);
    Assert.Contains(action.Parameters, p => p.Name == ToolTimeout.ParameterName && p.Type == "WholeNumber");
    Assert.Contains(action.Parameters, p => p.Name == "path" && p.Type == "Text");
    Assert.Contains(action.Parameters, p => p.Name == "startLine" && p.Type == "WholeNumber");
  }

  [Fact]
  public async Task InvokeAsync_DelegatesToTool_AndReturnsContent()
  {
    CapabilityInvocationResult result = await Create().InvokeAsync("read",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"path":"x.txt","startLine":1,"endLine":2}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("[read x.txt lines 1-2 of 2 total]", result.Content, StringComparison.Ordinal);
    Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvokeAsync_ToolError_CarriesIsErrorAndGutter()
  {
    AgentToolsProvider provider = new("agent",
        [new AgentToolBinding(new ReadTool(new FailingFileSystemAccess()), "Read lines.")]);

    CapabilityInvocationResult result = await provider.InvokeAsync("read",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"path":"missing.txt","startLine":1,"endLine":5}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [FileNotFound]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvokeAsync_UnknownAction_ReturnsError()
  {
    CapabilityInvocationResult result = await Create().InvokeAsync("nope", "{}", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [UnknownAction]: Unknown action: nope", result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeFileSystemAccess : IFileSystemAccess
  {
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(new FileRead(["alpha", "beta"], 2, 2)));
  }

  private sealed class FailingFileSystemAccess : IFileSystemAccess
  {
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
        => Task.FromResult(Result.Failure<FileRead>(
            new DomainError("FileNotFound", $"File not found: {path}.")));
  }
}

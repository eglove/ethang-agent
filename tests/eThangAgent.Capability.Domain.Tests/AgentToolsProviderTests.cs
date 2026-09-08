using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Capability.Domain.Tests;

public class AgentToolsProviderTests
{
  private static AgentToolsProvider Create() =>
      new("agent",
          [new AgentToolBinding(new ReadTool(new UnrootedPathResolver(), new FakeFileSystemAccess()), "Read lines from a text file.")]);

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
    // The annotation names the resolved (full) path under the unrooted resolver.
    Assert.Contains($"[read {Path.GetFullPath("x.txt")} lines 1-2 of 2 total]", result.Content, StringComparison.Ordinal);
    Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvokeAsync_ToolError_CarriesIsErrorAndGutter()
  {
    AgentToolsProvider provider = new("agent",
        [new AgentToolBinding(new ReadTool(new UnrootedPathResolver(), new FailingFileSystemAccess()), "Read lines.")]);

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

  [Fact]
  public void RootedAt_KeepsAdvertisement_Identical()
  {
    AgentToolsProvider original = Create();
    AgentToolsProvider anchored = original.RootedAt(Path.Combine(Path.GetTempPath(), "atp-anchor"));

    ActionDescriptor expected = Assert.Single(original.Actions);
    ActionDescriptor action = Assert.Single(anchored.Actions);
    Assert.Equal("agent", anchored.Id);
    Assert.Equal(expected.Name, action.Name);
    Assert.Equal(expected.Summary, action.Summary);
    Assert.Equal(expected.Description, action.Description);
    Assert.Equal(expected.Parameters, action.Parameters);
  }

  [Fact]
  public async Task RootedAt_ScopedTool_ResolvesAtAnchor_AndRefusesEscape()
  {
    RecordingFileSystemAccess files = new();
    string anchor = Path.Combine(Path.GetTempPath(), "atp-anchor");
    AgentToolsProvider provider = new("agent",
        [new AgentToolBinding(new ReadTool(new UnrootedPathResolver(), files), "Read lines.")]);

    CapabilityInvocationResult ok = await provider.RootedAt(anchor).InvokeAsync("read",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"path":"inner.txt","startLine":1,"endLine":1}""", ct: TestContext.Current.CancellationToken);

    Assert.False(ok.IsError);
    string expected = Path.GetFullPath(Path.Combine(anchor, "inner.txt"));
    Assert.Equal(expected, Assert.Single(files.Requested));

    CapabilityInvocationResult refused = await provider.RootedAt(anchor).InvokeAsync("read",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"path":"../../../../outside.txt","startLine":1,"endLine":1}""", ct: TestContext.Current.CancellationToken);

    Assert.True(refused.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]:", refused.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RootedAt_UnscopedTool_PassesThroughUnchanged()
  {
    AgentToolsProvider provider = new("agent",
        [new AgentToolBinding(new CycleCheckTool(), "Detect cycles.")]);
    string args =
                             /*lang=json,strict*/
                             """{"timeoutSeconds":30,"edges":[],"entry":[]}""";

    CapabilityInvocationResult before = await provider.InvokeAsync("cycle_check", args, ct: TestContext.Current.CancellationToken);
    CapabilityInvocationResult after = await provider.RootedAt(Path.Combine(Path.GetTempPath(), "atp-anchor"))
        .InvokeAsync("cycle_check", args, ct: TestContext.Current.CancellationToken);

    Assert.Equal(before.IsError, after.IsError);
    Assert.Equal(before.Content, after.Content);
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

  private sealed class RecordingFileSystemAccess : IFileSystemAccess
  {
    public List<string> Requested { get; } = [];

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default)
    {
      Requested.Add(path);
      return Task.FromResult(Result.Success(new FileRead(["alpha"], 1, 1)));
    }
  }
}

using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.Roslyn.ACL.Tests;

public class ScriptGlobalsShellSinkTests
{
  private sealed class RecordingSink : IVerificationLedger
  {
    public List<ShellExecutionRecord> Records { get; } = [];

    public void Append(ShellExecutionRecord record) => Records.Add(record);

    public IReadOnlyList<ShellExecutionRecord> Snapshot() => [.. Records];
  }

  [Fact]
  public void Shell_ReportsCompletedRun_ToSink()
  {
    RecordingSink sink = new();
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        verificationSink: sink);

    _ = globals.Shell("cmd", "/c", "exit 3");

    Assert.Equal(3, Assert.Single(sink.Records).ExitCode);
  }

  [Fact]
  public void Shell_ZeroExit_AlsoReports()
  {
    RecordingSink sink = new();
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        verificationSink: sink);

    _ = globals.Shell("cmd", "/c", "exit 0");

    Assert.Equal(0, Assert.Single(sink.Records).ExitCode);
  }

  [Fact]
  public void Shell_TokensAndTimesCarried()
  {
    RecordingSink sink = new();
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
        verificationSink: sink);

    _ = globals.Shell("cmd", "/c", "exit 0");

    ShellExecutionRecord record = Assert.Single(sink.Records);
    Assert.Equal(4, record.Tokens.Count);
    Assert.Equal("cmd", record.Tokens[0]);
    Assert.True(record.StartedUtc <= record.FinishedUtc);
  }

  [Fact]
  public void Shell_WithoutSink_DoesNotThrow()
  {
    ScriptGlobals globals = new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath());

    ShellResult result = globals.Shell("cmd", "/c", "exit 0");

    Assert.Equal(0, result.ExitCode);
    Assert.True(globals.VerificationSink is null);
  }

  // Shell never resolves tools or invokes capabilities; an empty registry stub
  // satisfies ScriptTools' non-null contract.
  private static EmptyRegistry NoopRegistry() => new();

  private sealed class EmptyRegistry : ICapabilityRegistry
  {
    public Result<ResolvedCapability> Resolve(string nameOrRef) =>
        Result.Failure<ResolvedCapability>(new DomainError("NotFound", "unused"));

    public IReadOnlyList<ProviderCapabilities> Providers => [];

    public Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");
  }
}

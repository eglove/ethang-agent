using System.Text.RegularExpressions;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Doctrine tests (R5): architecture rules that span projects and therefore
///     live in no single domain's test suite. Source-level scans walk the repository
///     from the test assembly's location (the same discovery the ChildHost E2E uses);
///     every failure names the offending file so a violation is actionable.</summary>
public sealed partial class DoctrineTests
{
  private static string RepoRoot()
  {
    DirectoryInfo? dir = new(typeof(DoctrineTests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir!.FullName;
  }

  private static string[] SourceFiles(params string[] parts)
  {
    string path = Path.Combine([.. new[] { RepoRoot() }, .. parts]);
    return [.. Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains("obj", StringComparison.Ordinal) && !f.Contains("bin", StringComparison.Ordinal))];
  }

  [GeneratedRegex(@"Thread\.Sleep|Task\.Delay")]
  private static partial Regex TimedWaitPattern();

  [GeneratedRegex(@"using\s+eThangAgent\.Transport|eThangAgent\.Transport\.ACL")]
  private static partial Regex TransportReferencePattern();

  [GeneratedRegex(@"ListRecentAsync|CountKindForAgentAsync")]
  private static partial Regex AuditReadPattern();

  /// <summary>R5.1: no NEW polling. Wait idioms are event-driven (WhenSettledAsync,
  ///     push delivery); timed waits appear only in the named allowlist — the
  ///     watchdog's bounded settle poll, transport/retry ACLs owning real backoff
  ///     behind an injected delay seam, and the remote supervisor's bounded host
  ///     startup handshake. Anything else fails the doctrine.</summary>
  [Fact]
  public void NoNewPolling_TimedWaits_AppearOnlyInAllowlistedFiles()
  {
    string[] allowlist =
    [
        "src/eThangAgent.Agent.Application/AgentWatchdog.cs",
        "src/eThangAgent.Composition/RemoteHostSupervisor.cs",
        "src/eThangAgent.ChildHost/Program.cs", // bounded accept-retry backoff
        "src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs",
        "src/eThangAgent.Zai.ACL/ZaiModelProvider.cs",
    ];

    List<string> violations = [];
    foreach (string file in SourceFiles("src"))
    {
      string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
      if (allowlist.Contains(relative))
      {
        continue;
      }

      if (TimedWaitPattern().IsMatch(File.ReadAllText(file)))
      {
        violations.Add(relative);
      }
    }

    Assert.True(violations.Count == 0,
        "timed waits outside the polling allowlist: " + string.Join(", ", violations));
  }

  /// <summary>R5.2: the Agent Domain never references the Transport ACL. Process
  ///     independence rides the domain's IAgentRuntime seam; the domain must not
  ///     know the wire, the pipe, or the host executable exist.</summary>
  [Fact]
  public void AgentDomain_NeverReferencesTransportAcl()
  {
    List<string> violations = [];
    foreach (string file in SourceFiles("src", "eThangAgent.Agent.Domain"))
    {
      if (TransportReferencePattern().IsMatch(File.ReadAllText(file)))
      {
        violations.Add(Path.GetFileName(file));
      }
    }

    Assert.True(violations.Count == 0,
        "Agent Domain files referencing the Transport ACL: " + string.Join(", ", violations));
  }

  /// <summary>R5.3: audit is not state. Decision makers never read the audit trail to
  ///     decide — the one designed exception is attempt derivation (counting
  ///     RetrySpawned/RetryDeferred rows the watchdog itself wrote; the AGENTS.md
  ///     documents attempts as DERIVED from audit rows). The runtime, spawner,
  ///     orphan repair, and grant enforcement never touch watchdog_events at all:
  ///     enforcement is never informed by audit.</summary>
  [Fact]
  public void AuditNotState_EnforcementPathsNeverReadWatchdogEvents()
  {
    string[] files =
    [
        "src/eThangAgent.Agent.Infrastructure/InProcessAgentRuntime.cs",
        "src/eThangAgent.Agent.Domain/SubAgentSpawner.cs",
        "src/eThangAgent.Agent.Application/OrphanRepairHandler.cs",
        "src/eThangAgent.Agent.Domain/FilteredToolRegistry.cs",
    ];

    List<string> violations = [];
    foreach (string relative in files)
    {
      if (AuditReadPattern().IsMatch(File.ReadAllText(Path.Combine(RepoRoot(), relative))))
      {
        violations.Add(relative);
      }
    }

    Assert.True(violations.Count == 0,
        "enforcement/runtime paths reading the audit trail (audit is a record, never a state source): "
        + string.Join(", ", violations));
  }
}

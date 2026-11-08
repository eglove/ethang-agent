using System.Text.Json;
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

  [GeneratedRegex(@"Assert\.Skip\s*\(")]
  private static partial Regex SkipCallPattern();

  [GeneratedRegex(@"Trait\s*\(\s*""Category""\s*,\s*""Process""\s*\)")]
  private static partial Regex ProcessTraitPattern();

  [GeneratedRegex(@"Trait\s*\(\s*""Category""\s*,\s*""OptIn""\s*\)")]
  private static partial Regex OptInTraitPattern();

  [GeneratedRegex(@"Trait\s*\(\s*""Category""\s*,\s*""LatencyContract""\s*\)")]
  private static partial Regex LatencyContractTraitPattern();

  [GeneratedRegex(@"static\s+readonly\s+TimeSpan\s+(Limit|TestTimeout|WaitLimit|Timeout)\s*=\s*TimeSpan\.From")]
  private static partial Regex LocalCeilingPattern();

  [GeneratedRegex(@"Assert\.\w+\s*\([^;]*?\.Elapsed\s*<=")]
  private static partial Regex ElapsedAssertionPattern();

  [GeneratedRegex(@"Path\.GetTempPath\(\)|Directory\.CreateTempSubdirectory\(")]
  private static partial Regex AmbientTempPattern();

  [GeneratedRegex(@"Environment\.SetEnvironmentVariable\s*\(\s*""ETHANG_AGENT_DB""")]
  private static partial Regex DatabaseGlobalWritePattern();

  [GeneratedRegex(@":\s*SessionOwningTest\b")]
  private static partial Regex SessionOwningDerivationPattern();

  [GeneratedRegex(@"SetCurrentDirectory\s*\(|Environment\.CurrentDirectory\s*=")]
  private static partial Regex WorkingDirectoryWritePattern();

  [GeneratedRegex(@"\[Collection\(""")]
  private static partial Regex CollectionAttributePattern();

  [GeneratedRegex("<Project\\s+Path=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
  private static partial Regex SolutionProjectPattern();

  /// <summary>The projects a solution filter keeps, as the filter file spells them.</summary>
  private static string[] FilterProjects(string filterPath)
  {
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), filterPath)));
    return [.. document.RootElement.GetProperty("solution").GetProperty("projects")
        .EnumerateArray()
        .Select(project => project.GetString()!.Replace('\\', '/'))];
  }

  /// <summary>R5.1: no NEW polling. Wait idioms are event-driven (WhenSettledAsync,
  ///     push delivery); timed waits appear only in the named allowlist — the
  ///     watchdog's bounded settle poll, transport/retry ACLs owning real backoff
  ///     behind an injected delay seam, and the remote supervisor's bounded host
  ///     startup handshake. The ComputerUse broker ACL and the native broker Host join
  ///     the same allowlist: the ACL's spec 1.3 cold-start backoff schedule and one
  ///     restart backoff after a lost connection, the broker's bounded clipboard-sequence
  ///     consumption poll, and hold_key's one-shot key-up schedule. Anything else fails
  ///     the doctrine.</summary>
  [Fact]
  public void NoNewPolling_TimedWaits_AppearOnlyInAllowlistedFiles()
  {
    string[] allowlist =
    [
        "src/eThangAgent.Agent.Application/AgentWatchdog.cs",
        "src/eThangAgent.Composition/RemoteHostSupervisor.cs",
        "src/eThangAgent.ChildHost/Program.cs", // bounded accept-retry backoff
        "src/eThangAgent.OpenRouter.ACL/OpenRouterModelProvider.cs",
        "src/eThangAgent.ComputerUse.ACL/BrokerSupervisor.cs", // ONE bounded restart backoff after a lost broker connection (transport/retry ACL)
        "src/eThangAgent.ComputerUse.ACL/NotReadyPolicy.cs", // production half of the INotReadyDelayer seam - the spec 1.3 cold-start backoff schedule
        "src/eThangAgent.ComputerUse.Host/ClipboardPaster.cs", // native broker process: bounded clipboard-sequence consumption poll
        "src/eThangAgent.ComputerUse.Host/InputDispatch.cs", // native broker process: hold_key's one-shot key-up schedule, not a poll
        "src/eThangAgent.Composition/SkillDirectoryWatcher.cs", // spec #26: the sweep is the NAMED polling fallback for hot reload (FSW misses buffer overflows and atomic-save renames); the debounce is an event-burst coalesce, not a poll
        "src/eThangAgent.Desktop/ViewModels/GitBranchWatcher.cs", // the debounce is an event-burst coalesce over FSW .git writes (one query per git gesture), not a poll — the SkillDirectoryWatcher precedent
        "src/eThangAgent.Desktop/ViewModels/GitViewViewModel.cs", // same shape over the WORKSPACE ROOT: the debounce coalesces an FSW write burst into one git query, so a build or a multi-file save is one refresh, not one per file — never a poll
        "src/eThangAgent.Desktop/ViewModels/PullRequestPaneViewModel.cs", // issue #10: gh-backed data has NO push channel (design D6), so the GitHub view's 20 s visible-only poll is the realtime story — it runs only while selected and skips entirely while degraded
        "src/eThangAgent.FileSystem.ACL/WorkspaceFileWatcher.cs", // issue #348: the debounce is an event-burst coalesce over FSW writes (one pass per burst), not a poll — the SkillDirectoryWatcher precedent
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
  ///     RetrySpawned/RetryDeferred rows the watchdog itself wrote, because attempts are
  ///     DERIVED from audit rows rather than stored). The runtime, spawner,
  ///     orphan repair, and grant enforcement never touch watchdog_events at all:
  ///     enforcement is never informed by audit.</summary>
  [Fact]
  public void AuditNotState_EnforcementPathsNeverReadWatchdogEvents()
  {
    string[] files =
    [
        "src/eThangAgent.Agent.Infrastructure/InProcessAgentRuntime.cs",
        "src/eThangAgent.Agent.Domain/AgentRunLoop.cs",
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

  /// <summary>Issue #366: a test that does not run is not a test. Every test either runs or is
  ///     deleted; the one exception is an opt-in test, which is marked by its class-level
  ///     <c>Category=OptIn</c> trait and skipped through <c>OptInTest.SkipUnlessEnabled</c>, so the
  ///     skip names the switch that turns it on and the default run reports it as a skip rather
  ///     than a silent pass.</summary>
  [Fact]
  public void NoSkippedTests_EveryTestRunsOrIsDeleted()
  {
    // The helper is where the one permitted skip lives; it is not a test file.
    string[] allowlist = ["tests/eThangAgent.Tests.Shared/OptInTest.cs"];

    List<string> violations = [];
    foreach (string file in SourceFiles("tests"))
    {
      string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
      if (allowlist.Contains(relative))
      {
        continue;
      }

      string text = File.ReadAllText(file);
      if (SkipCallPattern().IsMatch(text) && !OptInTraitPattern().IsMatch(text))
      {
        violations.Add(relative);
      }
    }

    Assert.True(violations.Count == 0,
        "tests skipped without an opt-in trait (a test either runs or is deleted; an opt-in test carries "
        + "[Trait(\"Category\", \"OptIn\")] and skips through OptInTest.SkipUnlessEnabled): "
        + string.Join(", ", violations));
  }

  /// <summary>Issue #366: a wall-clock bound is a CEILING, not a latency contract. Under full-suite
  ///     load the machine is saturated by the suite's own child-host processes, so a bound tuned to
  ///     an idle machine expires before the work settles and the assertion proves nothing about the
  ///     code. Ceilings have one source of truth (<c>TestCeiling</c> in Tests.Shared); a test that
  ///     genuinely measures latency declares it with <c>Category=LatencyContract</c>.</summary>
  [Fact]
  public void NoWallClockLatencyBounds_EveryWaitIsACeilingOrDeclaresLatency()
  {
    List<string> violations = [];
    foreach (string file in SourceFiles("tests"))
    {
      string text = File.ReadAllText(file);
      if (LatencyContractTraitPattern().IsMatch(text))
      {
        continue;
      }

      string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
      if (LocalCeilingPattern().IsMatch(text))
      {
        violations.Add(relative + " (local wall-clock ceiling constant)");
      }

      if (ElapsedAssertionPattern().IsMatch(text))
      {
        violations.Add(relative + " (asserts elapsed time)");
      }
    }

    Assert.True(violations.Count == 0,
        "wall-clock latency bounds outside a declared latency contract (use TestCeiling, or mark the "
        + "class [Trait(\"Category\", \"LatencyContract\")] when latency IS the contract): "
        + string.Join(", ", violations));
  }

  /// <summary>Issue #371: a test's scratch state lives under the run's temp root, never in the
  ///     ambient temp directory. A test that reads the ambient directory directly creates debris
  ///     the run cannot sweep: it is indistinguishable from every other process's, so the one
  ///     recursive delete at teardown cannot reach it and nothing else ever reclaims it. The
  ///     measured cost of that habit was 242,000 files and 58 GB in one machine's temp directory,
  ///     with every failed cleanup swallowed by a bare catch.</summary>
  [Fact]
  public void NoAmbientTemp_EveryScratchPathLivesUnderTheRunRoot()
  {
    // The root is the one place the ambient directory is read.
    string[] allowlist = ["tests/eThangAgent.TestSupport/TestTempRoot.cs"];

    List<string> violations = [];
    foreach (string file in SourceFiles("tests"))
    {
      string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
      if (allowlist.Contains(relative))
      {
        continue;
      }

      if (AmbientTempPattern().IsMatch(File.ReadAllText(file)))
      {
        violations.Add(relative);
      }
    }

    Assert.True(violations.Count == 0,
        "tests reading the ambient temp directory instead of the run root (use TestTempRoot.Root "
        + "for a path and TestTempRoot.CreateDirectory for a directory): " + string.Join(", ", violations));
  }

  /// <summary>Issue #370: the suite runs as three commands - the batch filter plus the two heavyweight
  ///     projects on their own - so a test project that joins the solution must join exactly one of
  ///     them. A project in none of the three is a project whose tests never run, and a project in two
  ///     is a project that runs twice.</summary>
  [Fact]
  public void TestProjects_AreEachRunByExactlyOneOfTheDocumentedCommands()
  {
    string[] heavyweight =
    [
      "tests/eThangAgent.Composition.Tests/eThangAgent.Composition.Tests.csproj",
      "tests/eThangAgent.Storage.ACL.Tests/eThangAgent.Storage.ACL.Tests.csproj",
    ];

    string[] inSolution = [.. SolutionProjectPattern()
        .Matches(File.ReadAllText(Path.Combine(RepoRoot(), "eThangAgent.slnx")))
        .Select(match => match.Groups[1].Value.Replace('\\', '/'))
        .Where(path => path.StartsWith("tests/", StringComparison.Ordinal)
            && path.EndsWith(".Tests.csproj", StringComparison.Ordinal))];
    string[] inBatch = FilterProjects("eThangAgent.Batch.slnf");

    List<string> violations = [];
    foreach (string project in inSolution)
    {
      int commands = (inBatch.Contains(project) ? 1 : 0) + (heavyweight.Contains(project) ? 1 : 0);
      if (commands != 1)
      {
        violations.Add(project + " (" + (commands == 0 ? "no command runs it" : "run by more than one command") + ")");
      }
    }

    Assert.True(violations.Count == 0,
        "test projects not covered by exactly one documented command (add it to eThangAgent.Batch.slnf, "
        + "or to the heavyweight list in this test and the README): " + string.Join(", ", violations));
  }


  /// <summary>Issue #373: the fast tier is only as trustworthy as its boundary. A test that
  ///     starts a process, opens a listener, or builds a composed container carries
  ///     <c>Category=Process</c>; a test that carries the trait does one of those things. The
  ///     marker list below is what "does one of those things" MEANS in this repository - the
  ///     real child host, the language-server daemon and its pipe client, a stub MCP or
  ///     computer-use host, a mock HTTP server, a real shell, and the composed-container
  ///     helpers - and it is checked in both directions, because a trait that is missing
  ///     leaks work into the fast loop and a trait that is unearned takes a test out of it.
  ///     <para>Both directions matter for the same reason: the fast command is a NEGATIVE
  ///     filter (<c>--filter-not-trait</c>), so an untagged process test runs on every edit
  ///     and a wrongly tagged pure test silently stops running there at all.</para></summary>
  [Fact]
  public void ProcessTier_EveryTestThatSpawnsCarriesTheTraitAndEveryTraitEarnsItsPlace()
  {
    string[] markers =
    [
        "Process.Start",
        "new Process(",
        "ProcessStartInfo",
        "Process.GetProcessById",
        "System.Diagnostics.Process",
        "DirectShellAccess",
        "Shell(\"",
        "TcpListener",
        "HttpListener",
        "LoopbackCallbackListener.Start",
        "NamedPipeServerStream",
        "NamedPipeClientStream",
        "AddEThangAgentCore",
        "AgentSessionFactory",
        "SessionOwningTest",
        "HostHarness",
        "ScriptedRootHost",
        "StubHostBuilder",
        "StubServerBuilder",
        "MockOpenRouterServer",
        "FakeBroker",
        "FakeConversationHost",
        "NonReadingPeer",
        "LspPipeClient",
        "DaemonHarness",
        "Avalonia.Headless",
        "using eThangAgent.ChildHost;",
    ];

    // A test attribute, whatever the runner that supplies it: xUnit's Fact/Theory and the
    // Avalonia headless variants, which are the ones whose tests drive a real UI thread.
    string[] testAttributes = ["[Fact", "[Theory", "[AvaloniaFact", "[AvaloniaTheory"];

    string[] allowlist =
    [
        // This file names every marker as data, so it matches its own list by construction.
        "tests/eThangAgent.Agent.Application.Tests/DoctrineTests.cs",
        // The engine's semantics are pure; only the four Shell tests in it spawn, and they
        // carry the trait on the METHOD - the one shape the file-level check cannot see.
        "tests/eThangAgent.Roslyn.ACL.Tests/CSharpScriptExecEngineTests.cs",
    ];

    List<string> missingTrait = [];
    List<string> unearnedTrait = [];
    foreach (string file in SourceFiles("tests"))
    {
      string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
      if (allowlist.Contains(relative))
      {
        continue;
      }

      string text = File.ReadAllText(file);
      if (!testAttributes.Any(attribute => text.Contains(attribute, StringComparison.Ordinal)))
      {
        continue;
      }

      bool spawns = markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
      bool carries = ProcessTraitPattern().IsMatch(text);
      if (spawns && !carries)
      {
        missingTrait.Add(relative);
      }
      else if (!spawns && carries)
      {
        unearnedTrait.Add(relative);
      }
    }

    Assert.True(missingTrait.Count == 0,
        "tests that spawn a process, open a listener, or build a composed container without "
        + "[Trait(\"Category\", \"Process\")] (the fast tier runs everything else, so these would "
        + "run on every edit): " + string.Join(", ", missingTrait));
    Assert.True(unearnedTrait.Count == 0,
        "tests carrying [Trait(\"Category\", \"Process\")] that spawn nothing (the fast tier is a "
        + "negative filter, so an unearned trait silently stops the test running there): "
        + string.Join(", ", unearnedTrait));
  }

  /// <summary>Issue #366: every test assembly takes its ceiling from Tests.Shared. A project that
  ///     does not reference it can only express a bound by writing its own, which is the shape the
  ///     ceiling rule exists to prevent.</summary>
  [Fact]
  public void TestCeilings_HaveOneSourceOfTruth()
  {
    List<string> violations = [];
    foreach (string project in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tests"), "*.csproj",
        SearchOption.AllDirectories))
    {
      string relative = Path.GetRelativePath(RepoRoot(), project).Replace('\\', '/');
      if (relative.EndsWith("eThangAgent.Tests.Shared/eThangAgent.Tests.Shared.csproj", StringComparison.Ordinal)
          || relative.EndsWith("eThangAgent.TestSupport/eThangAgent.TestSupport.csproj", StringComparison.Ordinal)
          || relative.Contains("/Stub", StringComparison.Ordinal))
      {
        continue;
      }

      if (!File.ReadAllText(project).Contains("eThangAgent.TestSupport.csproj", StringComparison.Ordinal))
      {
        violations.Add(relative);
      }
    }

    Assert.True(violations.Count == 0,
        "test projects without a Tests.Shared reference (the ceiling has one source of truth): "
        + string.Join(", ", violations));
  }

  /// <summary>Issue #372: process-global state is serialized by RULE, not by convention. A test
  ///     that sets <c>ETHANG_AGENT_DB</c> or moves the process working directory mutates state
  ///     every other test in the run shares, so it must join a serialized collection - the
  ///     collection is what makes the write safe, so the write and the membership are checked
  ///     together.
  ///     <para>Nothing enforced this before, and three classes had escaped: a race here fails a
  ///     test in a file its author never touched, which is the least debuggable failure the
  ///     suite can produce.</para></summary>
  [Fact]
  public void ProcessGlobalState_IsSerializedByRuleNotConvention()
  {
    string[] testAttributes = ["[Fact", "[Theory", "[AvaloniaFact", "[AvaloniaTheory"];

    // This file names every marker as data, so it matches its own list by construction.
    string[] allowlist = ["tests/eThangAgent.Agent.Application.Tests/DoctrineTests.cs"];

    List<string> violations = [];
    foreach (string file in SourceFiles("tests"))
    {
      string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
      if (allowlist.Contains(relative))
      {
        continue;
      }

      string text = File.ReadAllText(file);
      if (!testAttributes.Any(attribute => text.Contains(attribute, StringComparison.Ordinal)))
      {
        continue;
      }

      if (CollectionAttributePattern().IsMatch(text))
      {
        continue;
      }

      if (DatabaseGlobalWritePattern().IsMatch(text)
          || WorkingDirectoryWritePattern().IsMatch(text)
          || SessionOwningDerivationPattern().IsMatch(text))
      {
        violations.Add(relative);
      }
    }

    Assert.True(violations.Count == 0,
        "tests touching process-global state outside a serialized collection (join a "
        + "[Collection] whose definition says it runs serially, or stop touching the global): "
        + string.Join(", ", violations));
  }
}

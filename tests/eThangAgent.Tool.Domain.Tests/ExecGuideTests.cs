using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public partial class ExecGuideTests
{
  [Fact]
  public void Guide_IsVersionedAndNonEmpty()
  {
    Assert.Equal("2.26", ExecGuide.Version);
    Assert.True(ExecGuide.Text.Length >= 500);
  }

  [Fact]
  public void Guide_DocumentsPlanCapabilityProvider()
  {
    // The plan capability provider is taught under '### Calling tools'.
    int calling = ExecGuide.Text.IndexOf("### Calling tools", StringComparison.Ordinal);
    int shell = ExecGuide.Text.IndexOf("### Running external commands", StringComparison.Ordinal);
    Assert.True(calling >= 0 && shell > calling, "Calling tools section missing");
    Assert.True(calling >= 0 && shell > calling, "Calling tools section missing");
    string section = ExecGuide.Text[calling..shell];

    Assert.Contains("plan.create", section, StringComparison.Ordinal);
    Assert.Contains("[plan] created #", section, StringComparison.Ordinal);
  }

  // Issue #283: a run reports what it CHANGED, and the zero case is the one that
  // catches a no-op replace - so the guide must teach both shapes and say why zero matters.
  [Fact]
  public void Guide_TeachesTheChangeReport()
  {
    Assert.Contains("[exec: modified 0 workspace files", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("[exec: modified 1 workspace file", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("re-read the file and retry with the `edit` tool", ExecGuide.Text, StringComparison.Ordinal);
  }

  // search_files was deleted (grand plan: exec scripts search the workspace directly);
  // the guide must no longer advertise or teach it.
  [Fact]
  public void Guide_DoesNotTeachRemovedSearchFilesTool() =>
      Assert.DoesNotContain("search_files", ExecGuide.Text, StringComparison.Ordinal);

  // The relative-path leak (2026-10-22): the guide used to say "use relative paths
  // or Workspace", which is FALSE for a raw File.*/Directory.* call - a relative path
  // there resolves against the app's launch directory, so a personal-vault session
  // wrote its files into the ethang-agent repo. The guide must now name the rule.
  [Fact]
  public void Guide_TeachesThatRelativePathsNeedTheWorkspace()
  {
    Assert.Contains("ResolvePath", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Path.Combine(Workspace", ExecGuide.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("use relative paths or Workspace", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_DocumentsWorkspaceVsLaunchDirectoryAnnotation()
  {
    // The workspace-vs-launch-directory trap (session 4ebb01ae): the guide must
    // teach the rule AND the [exec: workspace note] annotation that surfaces it.
    Assert.Contains("Directory.GetCurrentDirectory() is", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("APP's launch directory", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("[exec: workspace note", ExecGuide.Text, StringComparison.Ordinal);
  }
  // Issue #93: an observer must be able to see what a RUNNING agent is doing. The guide
  // teaches the status line's progress suffix and states that it is a PULL surface —
  // progress still never wakes a wait.
  [Fact]
  public void Guide_TeachesRunningAgentProgressOnStatus()
  {
    string section = OutcomeSection();

    Assert.Contains("progress phase=model-call|tool-exec|draining", section, StringComparison.Ordinal);
    Assert.Contains("never content", section, StringComparison.Ordinal);
    Assert.Contains("PULL surface", section, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_DocumentsDurableState()
  {
    Assert.Contains("Tools.Invoke(\"state.set\", new {", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Tools.Invoke(\"state.verify\", new { timeoutSeconds = 30 })", ExecGuide.Text, StringComparison.Ordinal);
  }

  /// <summary>Issue #334: the guide states the TWO ways an agent comes into existence and
  ///     that nothing a model does creates one. If a spawn tool, a fan-out, or a depth limit
  ///     is put back, this test fails.</summary>
  [Fact]
  public void Guide_StatesTheWaysAnAgentExists_AndTeachesNoSpawnTool()
  {
    string section = ExistenceSection();

    Assert.Contains("An agent IS a session", section, StringComparison.Ordinal);
    Assert.Contains("a CHAIN STEP", section, StringComparison.Ordinal);
    Assert.Contains("no spawn tool and no fan-out", section, StringComparison.Ordinal);
    Assert.Contains("nothing caps an agent by nesting", section, StringComparison.Ordinal);
    Assert.Contains("CREATES AN AGENT", section, StringComparison.Ordinal);
    // Issue #338: the third way is the model's OWN path, and it starts a sibling.
    // Issue #338 F4: the section must also say what BOUNDS a launch. A launch is a session and
    // its own host process, which no host-capacity gate governs, so 'host capacity caps you'
    // would be false here — the model has to know that each launch costs a process.
    // Collapsed: the claims wrap across lines in the guide, and this is about what it SAYS.
    string claims = Collapse(section);
    Assert.Contains("each launch is a session and its own host process", claims, StringComparison.Ordinal);
    Assert.Contains("start the ones you need", claims, StringComparison.Ordinal);
    Assert.Contains("no parked state for this action", claims, StringComparison.Ordinal);

    int[] markers =
    [
      IndexOf(section, "An agent IS a session"),
      IndexOf(section, "a CHAIN STEP"),
      IndexOf(section, "no spawn tool"),
      IndexOf(section, "WHAT A LAUNCH COSTS"),
    ];
    Assert.All(markers, m => Assert.True(m >= 0, "teaching marker missing"));
    Assert.Equal(markers.OrderBy(m => m), markers);
  }

  /// <summary>Issue #338: the guide teaches agent.launch as starting a SIBLING, and teaches
  ///     no nesting, no depth limit, and no fan-out beside it. A guide that read as delegation
  ///     would be the exact revert this issue exists to prevent, so the words that would carry
  ///     that reading are pinned out of the section that introduces the action.</summary>
  [Fact]
  public void Guide_TeachesAgentLaunchAsASibling_WithNoNesting()
  {
    string section = ExistenceSection();

    Assert.Contains("agent.launch", section, StringComparison.Ordinal);
    Assert.Contains("SIBLING, NOT A SUBORDINATE", section, StringComparison.Ordinal);
    Assert.Contains("carries no owner and no nesting fact", section, StringComparison.Ordinal);
    Assert.Contains("indistinguishable from a cell the user opened by hand", section, StringComparison.Ordinal);
    // It returns an id and returns: reading the outcome is the caller's own later act.
    Assert.Contains("RETURNS the new agent's id and returns", section, StringComparison.Ordinal);
    Assert.Contains("not a join the launch performs", section, StringComparison.Ordinal);
    // Issue #338 F5: agent.resume cannot address a launched id, so the
    // section must not advertise them — a documented continuation the runtime refuses is the
    // #288 defect, and this is where it would be taught.
    Assert.Contains("use agent.result and agent.status", Collapse(section), StringComparison.Ordinal);
    Assert.Contains("cannot address the host a launched agent runs in yet", Collapse(section), StringComparison.Ordinal);
    // The sibling language, not the retired delegation language.
    foreach (string retired in new[]
    {
      "spawn a child", "subagent", "grandchild", "depth limit", "nesting limit",
      "children may spawn", "delegate the work to",
    })
    {
      Assert.True(section.IndexOf(retired, StringComparison.OrdinalIgnoreCase) < 0,
          "the existence section still reads as delegation: '" + retired + "'");
    }
  }
  /// <summary>THE GUIDE MAY NAME NO agent ACTION THAT DOES NOT EXIST. A blocklist of retired
  ///     words is unfalsifiable - a revert that teaches delegation as "a background worker you
  ///     start with agent.start" slips past any list of spellings. This asserts the invariant
  ///     instead: every agent.* action the guide names must be one the provider actually exposes,
  ///     so teaching a start verb fails no matter what it is called.</summary>
  [Fact]
  public void Guide_NamesOnlyAgentActionsThatExist()
  {
    // The actions the agent provider exposes. Pinned here because the Tool Domain may not
    // reference the Agent Domain; ActionNames_ExposeNoSpawnAndNoFanout pins the other side.
    // agent.launch joined them in issue #338 - starting a SIBLING, which records no parent.
    // agent.wait left them in issue #335 - blocking on a peer's outcome is supervision.
    string[] exposed =
        ["agent.status", "agent.result", "agent.resume", "agent.cancel", "agent.launch"];

    string[] named =
    [
      .. AgentActionPattern().Matches(ExecGuide.Text)
          .Select(match => match.Value)
          .Distinct(StringComparer.Ordinal),
    ];

    Assert.NotEmpty(named);
    foreach (string action in named)
    {
      Assert.True(exposed.Contains(action),
          "the guide teaches '" + action + "', which is not an action the agent provider exposes. "
          + "Exposed: " + string.Join(", ", exposed) + ".");
    }
  }

  /// <summary>The retired vocabulary appears NOWHERE in the guide, for the reader who meets it
  ///     in prose rather than in an action name.</summary>
  [Fact]
  public void Guide_TeachesNoRetiredSpawnVocabulary_Anywhere()
  {
    foreach (string retired in new[]
    {
      "agent.spawn", "agent.fanout", "agent.wait", "depth limit", "spawn a child", "subagent",
      "children may spawn", "grandchild", "child agent", "spawns",
    })
    {
      Assert.True(ExecGuide.Text.IndexOf(retired, StringComparison.OrdinalIgnoreCase) < 0,
          "the guide still teaches the retired vocabulary '" + retired + "'");
    }
  }

  /// <summary>Issue #316 retired the agent-to-agent messaging surface, so the guide must
  ///     stop teaching it (a documented action that 404s is worse than none - the #288
  ///     finding) AND must say so, because a model that has seen messaging elsewhere will
  ///     otherwise go looking for it. This pins both halves.</summary>
  [Fact]
  public void Guide_TeachesThatAgentsDoNotMessageEachOther()
  {
    int delegationStart = ExecGuide.Text.IndexOf("### How an agent comes into existence", StringComparison.Ordinal);
    int errorsStart = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
    Assert.True(delegationStart >= 0 && errorsStart > delegationStart, "section anchors missing");
    string section = ExecGuide.Text[delegationStart..errorsStart];

    Assert.Contains("AGENTS DO NOT MESSAGE EACH OTHER", section, StringComparison.Ordinal);
    Assert.Contains("agents communicate by STATUS only", section, StringComparison.Ordinal);
    Assert.Contains("Steering a running agent is the HUMAN's channel", section, StringComparison.Ordinal);

    // And it teaches NONE of the retired surface, anywhere in the guide.
    foreach (string retired in new[]
    {
      "agent.send", "agent.receipts", "agent.route", "agent.escalate",
      "agent.notify-subtree", "agent.notify-ancestors",
    })
    {
      Assert.True(ExecGuide.Text.IndexOf(retired, StringComparison.Ordinal) < 0,
          "the guide still teaches the retired action '" + retired + "'");
    }
  }

  [Fact]
  public void Guide_CarriesHelperAndReservedWordRules()
  {
    Assert.Contains("Only these script-level helpers exist", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("there is no `Head`", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("`new` is a reserved word", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_Headings_AppearExactlyOnce()
  {
    foreach (string heading in new[] { "### Calling tools", "### Rules", "### Errors", "### Writing output", "### How an agent comes into existence", "### Reading another agent's outcome" })
    {
      int count = Regex.Count(ExecGuide.Text, "^" + Regex.Escape(heading) + "\\s*$", RegexOptions.Multiline);
      Assert.True(count == 1, $"'{heading}' appears {count} times, expected exactly once.");
    }
  }
  private static string ExistenceSection()
  {
    int start = ExecGuide.Text.IndexOf("### How an agent comes into existence", StringComparison.Ordinal);
    int end = ExecGuide.Text.IndexOf("### Reading another agent's outcome", StringComparison.Ordinal);
    Assert.True(start >= 0, "How an agent comes into existence section missing");
    Assert.True(end > start, "Reading another agent's outcome section missing after it");
    return ExecGuide.Text[start..end];
  }

  private static string OutcomeSection()
  {
    int start = ExecGuide.Text.IndexOf("### Reading another agent's outcome", StringComparison.Ordinal);
    int end = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
    Assert.True(start >= 0, "Reading another agent's outcome section missing");
    Assert.True(end > start, "Errors section missing after it");
    return ExecGuide.Text[start..end];
  }

  private static int IndexOf(string text, string marker)
      => text.IndexOf(marker, StringComparison.Ordinal);

  [Fact]
  public void Guide_DocumentsShell_NativeSpawnAndTokenSemantics()
  {
    int start = ExecGuide.Text.IndexOf("### Running external commands", StringComparison.Ordinal);
    int end = ExecGuide.Text.IndexOf("### File system", StringComparison.Ordinal);
    Assert.True(start >= 0 && end > start, "Running external commands section missing");
    string section = ExecGuide.Text[start..end];

    // The route and its reason are documented verbatim: direct native spawn,
    // no shell intermediary anywhere.
    Assert.DoesNotContain("powershell", section, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("spawned directly", section, StringComparison.Ordinal);
    Assert.Contains("ONE native argument", section, StringComparison.Ordinal);
    Assert.Contains("no join-and-re-split", section, StringComparison.Ordinal);
    Assert.Contains("exit code propagates", section, StringComparison.Ordinal);
    // The canonical example uses pre-split tokens.
    Assert.Contains("Shell(\"git\", \"status\", \"--short\")", section, StringComparison.Ordinal);
  }

  [Fact]
  public void ExecTool_Description_StatesShellContract()
  {
    ExecTool tool = new(
        new NullExecEngine(), ExecOptions.Default,
        NullExecActivitySink.Instance);
    Assert.DoesNotContain("powershell", tool.Definition.Description, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("spawned directly", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("ONE native argument", tool.Definition.Description, StringComparison.Ordinal);
  }

  private sealed class NullExecEngine : IExecEngine
  {
    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([]));
    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(new ExecRunResult(ExecRunStatus.Completed, "", []));
  }

  [Fact]
  public void Guide_DocumentsIntrospection()
  {
    Assert.Contains("Tools.List()", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Tools.Describe(", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_DocumentsCoreCallPatterns()
  {
    Assert.Contains("Tools.read(new {", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Tools.Invoke(", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("try/catch", ExecGuide.Text, StringComparison.Ordinal);
  }

  // Two sessions produced empty exec output by calling Tools.Invoke(...) as a bare
  // statement (the result was discarded); the guide must teach the capture pattern
  // the harness now hints at.
  [Fact]
  public void Guide_TeachesCapturingNestedCallResults()
  {
    Assert.Contains("Tools.read(new { path = \"notes.txt\" });", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("var r = Tools.read(new { path = \"notes.txt\" });", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("[exec: empty output", ExecGuide.Text, StringComparison.Ordinal);
  }

  // Issue #260: the guide described the raw-string hazard and two remedies, and
  // neither was the tool-domain primitive that removes the hazard. `lines` must be
  // the NAMED primary remedy, with a concrete call example.
  [Fact]
  public void Guide_Names_Lines_As_The_RawString_Remedy()
  {
    string section = SafeWritingSection();

    Assert.Contains("`lines`", section, StringComparison.Ordinal);
    Assert.Contains("Tools.write(new {", section, StringComparison.Ordinal);
    Assert.Contains("lines = new[] {", section, StringComparison.Ordinal);
    // The remedy outranks the in-script workaround: it is named before it.
    int remedy = section.IndexOf("pass `lines` to the write tool", StringComparison.Ordinal);
    int workaround = section.IndexOf("string array joined with newlines", StringComparison.Ordinal);
    Assert.True(remedy >= 0, "the `lines` remedy is not named in the safe-writing section");
    Assert.True(workaround > remedy, "the in-script array workaround must follow the `lines` remedy");
  }

  // The staging-file route is now scoped to content originating OUTSIDE the script;
  // it used to be the advice for content the model authors, which `lines` handles
  // in one call.
  [Fact]
  public void Guide_Scopes_The_Staging_File_Route_To_External_Content()
  {
    string section = SafeWritingSection();
    Assert.Contains("ORIGINATES OUTSIDE the script", section, StringComparison.Ordinal);
  }

  // The guide and the write tool description must agree about how `lines` joins:
  // per-line, the target file's convention, empty element = blank line, empty array
  // = empty file. A contradiction here is what makes the model guess.
  [Fact]
  public void Guide_And_WriteTool_Agree_On_The_Lines_Contract()
  {
    // Whitespace-normalized: the contract is about what the text SAYS, so a reflow
    // of the guide must not fail this test.
    string section = Collapse(SafeWritingSection());
    WriteTool tool = new(new WorkspacePathResolver(TestTempRoot.Root), new NoopFileWriteAccess());
    // The join contract lives in the tool description AND its 'lines' parameter text;
    // the guide must agree with both.
    string description = Collapse(tool.Definition.Description + " " +
        string.Join(' ', tool.Definition.Parameters.Where(p => p.Name == "lines").Select(p => p.Description)));

    foreach (string claim in new[] { "empty element", "empty array", "line-ending", "carried verbatim" })
    {
      Assert.Contains(claim, section, StringComparison.OrdinalIgnoreCase);
      Assert.Contains(claim, description, StringComparison.OrdinalIgnoreCase);
    }
  }

  private static string Collapse(string text) =>
      string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

  private static string SafeWritingSection()
  {
    int start = ExecGuide.Text.IndexOf("### Writing C# safely", StringComparison.Ordinal);
    int end = ExecGuide.Text.IndexOf("### Rules", StringComparison.Ordinal);
    Assert.True(start >= 0, "Writing C# safely section missing");
    Assert.True(end > start, "Rules section missing after the safe-writing section");
    return ExecGuide.Text[start..end];
  }

  // The guide/tool-description agreement test only reads Definition, so the seam
  // behind it is inert.
  /// <summary>Every `agent.*` action name the guide mentions.</summary>
  [GeneratedRegex(@"agent\.[a-z_]+")]
  private static partial Regex AgentActionPattern();

  private sealed class NoopFileWriteAccess : IFileWriteAccess
  {
    public Task<Result<FileWriteOutcome>> WriteFileAsync(string path, string content, bool overwrite, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(string path, byte[] bytes, bool overwrite, CancellationToken ct = default) =>
        throw new NotSupportedException();

  }
}

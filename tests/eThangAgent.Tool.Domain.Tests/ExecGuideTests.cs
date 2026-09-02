using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecGuideTests
{
  [Fact]
  public void Guide_IsVersionedAndNonEmpty()
  {
    Assert.Equal("2.8", ExecGuide.Version);
    Assert.True(ExecGuide.Text.Length >= 500);
  }

  [Fact]
  public void Guide_DocumentsDurableState()
  {
    Assert.Contains("Tools.Invoke(\"agent.spawn\", new {", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Delegating subtasks", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("depth limit 3", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Tools.Invoke(\"state.set\", new {", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Tools.Invoke(\"state.verify\", new { timeoutSeconds = 30 })", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_TeachesNonBlockingDelegation_InOrder()
  {
    string section = DelegationSection();

    // (1) spawn returns immediately — never wait inside the spawn call.
    Assert.Contains("returns immediately with `id=<guid> status=running`", section, StringComparison.Ordinal);
    Assert.Contains("Never wait", section, StringComparison.Ordinal);
    // (2) continue useful work or fan out siblings.
    Assert.Contains("continue useful work", section, StringComparison.Ordinal);
    Assert.Contains("fan out siblings", section, StringComparison.Ordinal);
    // (3) poll agent.status between turns.
    Assert.Contains("Tools.Invoke(\"agent.status\", new { timeoutSeconds = 30, id = \"<guid>\" })", section, StringComparison.Ordinal);
    Assert.Contains("between turns", section, StringComparison.Ordinal);
    // (4) fetch agent.result — NotComplete = later, NotFound = wrong id.
    Assert.Contains("Tools.Invoke(\"agent.result\", new { timeoutSeconds = 60, id = \"<guid>\" })", section, StringComparison.Ordinal);
    Assert.Contains("`Error [NotComplete]`", section, StringComparison.Ordinal);
    Assert.Contains("try again later", section, StringComparison.Ordinal);
    Assert.Contains("`Error [NotFound]`", section, StringComparison.Ordinal);
    Assert.Contains("id is wrong", section, StringComparison.Ordinal);
    // (5) cap reached — retrieve pending results before spawning more.
    Assert.Contains("`Error [ConcurrencyCapReached]`", section, StringComparison.Ordinal);
    Assert.Contains("retrieve pending results before spawning more", section, StringComparison.Ordinal);
    // (6) depth limit unchanged.
    Assert.Contains("depth limit 3", section, StringComparison.Ordinal);

    int[] markers =
    [
            IndexOf(section, "returns immediately"),
            IndexOf(section, "continue useful work"),
            IndexOf(section, "Tools.Invoke(\"agent.status\""),
            IndexOf(section, "Tools.Invoke(\"agent.result\""),
            IndexOf(section, "ConcurrencyCapReached"),
            IndexOf(section, "depth limit 3"),
        ];
    Assert.All(markers, m => Assert.True(m >= 0, "teaching marker missing"));
    Assert.Equal(markers.OrderBy(m => m), markers);
  }

  [Fact]
  public void Guide_TeachesRecallingEarlierWork_InOrder()
  {
    string section = RecallSection();

    int delegationEnd = ExecGuide.Text.IndexOf("depth limit 3", StringComparison.Ordinal);
    int recallStart = ExecGuide.Text.IndexOf("### Recalling earlier work", StringComparison.Ordinal);
    Assert.True(delegationEnd >= 0, "delegation section marker missing");
    Assert.True(recallStart > delegationEnd, "recall section must come after the delegation section");

    // (1) memory.sessions lists what conversations exist — run it when resuming work.
    Assert.Contains("Tools.Invoke(\"memory.sessions\", new { timeoutSeconds = 30 })", section, StringComparison.Ordinal);
    Assert.Contains("lists what conversations exist", section, StringComparison.Ordinal)
        ;
    Assert.Contains("resuming work", section, StringComparison.Ordinal);
    Assert.Contains("before duplicating effort", section, StringComparison.Ordinal);
    // (2) memory.recall searches transcripts — literal default, tokens ANDed.
    Assert.Contains("Tools.Invoke(\"memory.recall\", new {", section, StringComparison.Ordinal);
    Assert.Contains("searches transcripts", section, StringComparison.Ordinal);
    Assert.Contains("tokens ANDed", section, StringComparison.Ordinal);
    // (2b) regex mode optional; budget errors mean simplify or go literal.
    Assert.Contains("queryMode = \"regex\"", section, StringComparison.Ordinal);
    Assert.Contains("`regex_pattern_too_large`", section, StringComparison.Ordinal);
    Assert.Contains("`invalid_regex`", section, StringComparison.Ordinal);
    Assert.Contains("`regex_timeout`", section, StringComparison.Ordinal);
    Assert.Contains("simplify the pattern", section, StringComparison.Ordinal);
    Assert.Contains("literal mode", section, StringComparison.Ordinal);
    // (3) scopes and branches.
    Assert.Contains("\"global\"", section, StringComparison.Ordinal);
    Assert.Contains("\"session:<id>\"", section, StringComparison.Ordinal);
    Assert.Contains("\"active\"", section, StringComparison.Ordinal);
    Assert.Contains("\"all\"", section, StringComparison.Ordinal);
    // (4) paging for long result sets.
    Assert.Contains("page", section, StringComparison.Ordinal);
    Assert.Contains("pageSize", section, StringComparison.Ordinal);
    Assert.Contains("200", section, StringComparison.Ordinal);
    // (5) memory is read-only.
    Assert.Contains("READ-ONLY", section, StringComparison.Ordinal);
    Assert.Contains("nothing to save yet", section, StringComparison.Ordinal);

    int[] markers =
    [
            IndexOf(section, "Tools.Invoke(\"memory.sessions\""),
            IndexOf(section, "Tools.Invoke(\"memory.recall\""),
            IndexOf(section, "queryMode = \"regex\""),
            IndexOf(section, "\"session:<id>\""),
            IndexOf(section, "pageSize"),
            IndexOf(section, "READ-ONLY"),
        ];
    Assert.All(markers, m => Assert.True(m >= 0, "teaching marker missing"));
    Assert.Equal(markers.OrderBy(m => m), markers);
  }

  [Fact]
  public void Guide_TeachesTheBroadcastActions()
  {
    int delegationStart = ExecGuide.Text.IndexOf("### Delegating subtasks", StringComparison.Ordinal);
    int recallStart = ExecGuide.Text.IndexOf("### Recalling earlier work", StringComparison.Ordinal);
    Assert.True(delegationStart >= 0 && recallStart > delegationStart, "section anchors missing");
    string section = ExecGuide.Text[delegationStart..recallStart];

    Assert.Contains("agent.notify-subtree", section, StringComparison.Ordinal);
    Assert.Contains("agent.notify-ancestors", section, StringComparison.Ordinal);
    Assert.Contains("hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull", section, StringComparison.Ordinal);
    Assert.Contains("reached=<count> delivered=<count>", section, StringComparison.Ordinal);
    Assert.Contains("reached=root delivered=<count>", section, StringComparison.Ordinal);
    Assert.Contains("never retried", section, StringComparison.Ordinal);
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
    foreach (string heading in new[] { "### Calling tools", "### Rules", "### Errors", "### Writing output", "### Delegating subtasks" })
    {
      int count = Regex.Count(ExecGuide.Text, "^" + Regex.Escape(heading) + "\\s*$", RegexOptions.Multiline);
      Assert.True(count == 1, $"'{heading}' appears {count} times, expected exactly once.");
    }
  }
  private static string RecallSection()
  {
    int start = ExecGuide.Text.IndexOf("### Recalling earlier work", StringComparison.Ordinal);
    int end = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
    Assert.True(start >= 0, "Recalling earlier work section missing");
    Assert.True(end > start, "Errors section missing after recall section");
    return ExecGuide.Text[start..end];
  }

  private static string DelegationSection()
  {
    int start = ExecGuide.Text.IndexOf("### Delegating subtasks", StringComparison.Ordinal);
    int end = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
    Assert.True(start >= 0, "Delegating subtasks section missing");
    Assert.True(end > start, "Errors section missing after delegation section");
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
    Assert.Contains("one token", section, StringComparison.Ordinal);
    Assert.Contains("re-parsed", section, StringComparison.Ordinal);
    Assert.Contains("exit code propagates", section, StringComparison.Ordinal);
    // The canonical example uses pre-split tokens.
    Assert.Contains("Shell(\"git\", \"status\", \"--short\")", section, StringComparison.Ordinal);
  }

  [Fact]
  public void ExecTool_Description_StatesShellContract()
  {
    ExecTool tool = new(
        new NullExecEngine(), ExecOptions.Default,
        new NullOutputStore(), NullExecActivitySink.Instance);
    Assert.DoesNotContain("powershell", tool.Definition.Description, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("spawned directly", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("one token", tool.Definition.Description, StringComparison.Ordinal);
  }

  private sealed class NullExecEngine : IExecEngine
  {
    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([]));
    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(new ExecRunResult(ExecRunStatus.Completed, "", []));
  }

  private sealed class NullOutputStore : IExecOutputStore
  {
    public Task<string> WriteAsync(string content, CancellationToken ct = default) =>
        Task.FromResult("");
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
    Assert.Contains("[exec:artifact", ExecGuide.Text, StringComparison.Ordinal);
  }
}

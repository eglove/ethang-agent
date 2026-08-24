using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecGuideTests
{
    [Fact]
    public void Guide_IsVersionedAndNonEmpty()
    {
        Assert.Equal("2.1", ExecGuide.Version);
        Assert.True(ExecGuide.Text.Length >= 500);
    }

    [Fact]
    public void Guide_DocumentsDurableState()
    {
        Assert.Contains("Tools.Invoke(\"agent.spawn\", new {", ExecGuide.Text);
        Assert.Contains("Delegating subtasks", ExecGuide.Text);
        Assert.Contains("depth limit 3", ExecGuide.Text);
        Assert.Contains("Tools.Invoke(\"state.set\", new {", ExecGuide.Text);
        Assert.Contains("Tools.Invoke(\"state.verify\", new { })", ExecGuide.Text);
    }

    [Fact]
    public void Guide_TeachesNonBlockingDelegation_InOrder()
    {
        var section = DelegationSection();

        // (1) spawn returns immediately — never wait inside the spawn call.
        Assert.Contains("returns immediately with `id=<guid> status=running`", section);
        Assert.Contains("Never wait", section);
        // (2) continue useful work or fan out siblings.
        Assert.Contains("continue useful work", section);
        Assert.Contains("fan out siblings", section);
        // (3) poll agent.status between turns.
        Assert.Contains("Tools.Invoke(\"agent.status\", new { id = \"<guid>\" })", section);
        Assert.Contains("between turns", section);
        // (4) fetch agent.result — NotComplete = later, NotFound = wrong id.
        Assert.Contains("Tools.Invoke(\"agent.result\", new { id = \"<guid>\" })", section);
        Assert.Contains("`Error [NotComplete]`", section);
        Assert.Contains("try again later", section);
        Assert.Contains("`Error [NotFound]`", section);
        Assert.Contains("id is wrong", section);
        // (5) cap reached — retrieve pending results before spawning more.
        Assert.Contains("`Error [ConcurrencyCapReached]`", section);
        Assert.Contains("retrieve pending results before spawning more", section);
        // (6) depth limit unchanged.
        Assert.Contains("depth limit 3", section);

        var markers = new[]
        {
            IndexOf(section, "returns immediately"),
            IndexOf(section, "continue useful work"),
            IndexOf(section, "Tools.Invoke(\"agent.status\""),
            IndexOf(section, "Tools.Invoke(\"agent.result\""),
            IndexOf(section, "ConcurrencyCapReached"),
            IndexOf(section, "depth limit 3"),
        };
        Assert.All(markers, m => Assert.True(m >= 0, "teaching marker missing"));
        Assert.Equal(markers.OrderBy(m => m), markers);
    }

    [Fact]
    public void Guide_TeachesRecallingEarlierWork_InOrder()
    {
        var section = RecallSection();

        var delegationEnd = ExecGuide.Text.IndexOf("depth limit 3", StringComparison.Ordinal);
        var recallStart = ExecGuide.Text.IndexOf("### Recalling earlier work", StringComparison.Ordinal);
        Assert.True(delegationEnd >= 0, "delegation section marker missing");
        Assert.True(recallStart > delegationEnd, "recall section must come after the delegation section");

        // (1) memory.sessions lists what conversations exist — run it when resuming work.
        Assert.Contains("Tools.Invoke(\"memory.sessions\", new { })", section);
        Assert.Contains("lists what conversations exist", section)
            ; Assert.Contains("resuming work", section);
        Assert.Contains("before duplicating effort", section);
        // (2) memory.recall searches transcripts — literal default, tokens ANDed.
        Assert.Contains("Tools.Invoke(\"memory.recall\", new {", section);
        Assert.Contains("searches transcripts", section);
        Assert.Contains("tokens ANDed", section);
        // (2b) regex mode optional; budget errors mean simplify or go literal.
        Assert.Contains("queryMode = \"regex\"", section);
        Assert.Contains("`regex_pattern_too_large`", section);
        Assert.Contains("`invalid_regex`", section);
        Assert.Contains("`regex_timeout`", section);
        Assert.Contains("simplify the pattern", section);
        Assert.Contains("literal mode", section);
        // (3) scopes and branches.
        Assert.Contains("\"global\"", section);
        Assert.Contains("\"session:<id>\"", section);
        Assert.Contains("\"active\"", section);
        Assert.Contains("\"all\"", section);
        // (4) paging for long result sets.
        Assert.Contains("page", section);
        Assert.Contains("pageSize", section);
        Assert.Contains("200", section);
        // (5) memory is read-only.
        Assert.Contains("READ-ONLY", section);
        Assert.Contains("nothing to save yet", section);

        var markers = new[]
        {
            IndexOf(section, "Tools.Invoke(\"memory.sessions\""),
            IndexOf(section, "Tools.Invoke(\"memory.recall\""),
            IndexOf(section, "queryMode = \"regex\""),
            IndexOf(section, "\"session:<id>\""),
            IndexOf(section, "pageSize"),
            IndexOf(section, "READ-ONLY"),
        };
        Assert.All(markers, m => Assert.True(m >= 0, "teaching marker missing"));
        Assert.Equal(markers.OrderBy(m => m), markers);
    }

    private static string RecallSection()
    {
        var start = ExecGuide.Text.IndexOf("### Recalling earlier work", StringComparison.Ordinal);
        var end = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
        Assert.True(start >= 0, "Recalling earlier work section missing");
        Assert.True(end > start, "Errors section missing after recall section");
        return ExecGuide.Text[start..end];
    }

    private static string DelegationSection()
    {
        var start = ExecGuide.Text.IndexOf("### Delegating subtasks", StringComparison.Ordinal);
        var end = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
        Assert.True(start >= 0, "Delegating subtasks section missing");
        Assert.True(end > start, "Errors section missing after delegation section");
        return ExecGuide.Text[start..end];
    }

    private static int IndexOf(string text, string marker)
        => text.IndexOf(marker, StringComparison.Ordinal);

    [Fact]
    public void Guide_DocumentsShell_PowerShellRoutingAndTokenSemantics()
    {
        var start = ExecGuide.Text.IndexOf("### Running external commands", StringComparison.Ordinal);
        var end = ExecGuide.Text.IndexOf("### File system", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Running external commands section missing");
        var section = ExecGuide.Text[start..end];

        // The route and its reason are documented verbatim.
        Assert.Contains("powershell -NoProfile", section);
        Assert.Contains("one token", section);
        Assert.Contains("re-parsed", section);
        Assert.Contains("exit code propagates", section);
        // The canonical example uses pre-split tokens.
        Assert.Contains("Shell(\"git\", \"status\", \"--short\")", section);
    }

    [Fact]
    public void ExecTool_Description_StatesShellContract()
    {
        var tool = new ExecTool(
            new NullExecEngine(), ExecOptions.Default,
            new NullOutputStore(), NullExecActivitySink.Instance);
        Assert.Contains("powershell -NoProfile", tool.Definition.Description);
        Assert.Contains("one token", tool.Definition.Description);
    }

    private sealed class NullExecEngine : IExecEngine
    {
        public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<ExecParseError>>.Success([]));
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
        Assert.Contains("Tools.List()", ExecGuide.Text);
        Assert.Contains("Tools.Describe(", ExecGuide.Text);
    }

    [Fact]
    public void Guide_DocumentsCoreCallPatterns()
    {
        Assert.Contains("Tools.read(new {", ExecGuide.Text);
        Assert.Contains("Tools.Invoke(", ExecGuide.Text);
        Assert.Contains("try/catch", ExecGuide.Text);
        Assert.Contains("[exec:artifact", ExecGuide.Text);
    }
}

using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>skill_invoke (spec #28 task 2): appends the invocation System
///     message out-of-turn through the IConversationSink seam (reloader
///     precedent) and returns a one-line confirmation; unknown/ambiguous
///     names fail with verbatim codes; oversize bodies confirm without
///     re-printing the body.</summary>
public class SkillInvokeToolTests
{
  private static SkillDefinition Skill(string name, bool manual = false, string? body = null) => new(
      name, "Does " + name + " things", body ?? ("Body of " + name), 1,
      manual ? SkillSource.File : SkillSource.BuiltIn,
      null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, manual, null);

  [Fact]
  public async Task Invoke_AppendsSystemMessage_AndConfirms()
  {
    FakeSink sink = new();
    string? notice = null;
    SkillInvokeTool tool = MakeTool(Skill("deploy"), sink, m => notice = m);

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"deploy\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill invoked: deploy] (no args)", result.Content, StringComparison.Ordinal);
    _ = Assert.Single(sink.Lines, l => l.StartsWith("[skill invoked: deploy]", StringComparison.Ordinal));
    Assert.NotNull(notice);
  }

  [Fact]
  public async Task Invoke_WithArgs_ConfirmationCarriesThem()
  {
    FakeSink sink = new();
    SkillInvokeTool tool = MakeTool(Skill("deploy"), sink, _ => { });

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"deploy\",\"args\":\"staging now\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill invoked: deploy] (args: staging now)", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Invoke_ManualSkill_SameOutputShape()
  {
    FakeSink sink = new();
    SkillInvokeTool tool = MakeTool(Skill("deploy", manual: true), sink, _ => { });

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"deploy\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill invoked: deploy] (no args)", result.Content, StringComparison.Ordinal);
    _ = Assert.Single(sink.Lines);
  }

  [Fact]
  public async Task Invoke_OversizeBody_ConfirmsWithoutBody()
  {
    FakeSink sink = new();
    SkillInvokeTool tool = MakeTool(Skill("big", body: new string('x', 5000)), sink, _ => { });

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"big\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill invoked: big]", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain(new string('x', 5000), result.Content, StringComparison.Ordinal);
    _ = Assert.Single(sink.Lines, l => l.Contains("body too large to inline", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Unknown_Name_FailsVerbatim()
  {
    SkillInvokeTool tool = MakeTool(Skill("deploy"), null, _ => { });
    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"ghost\"}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [SkillNotFound]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Ambiguous_Name_FailsVerbatim()
  {
    SkillInvokeTool tool = MakeTool(Skill("deploy"), Skill("Deploy"), null, _ => { });
    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"deploy\"}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [AmbiguousSkill]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Null_Conversation_StillSucceeds()
  {
    SkillInvokeTool tool = MakeTool(Skill("deploy"), null, _ => { });
    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_invoke", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"deploy\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ "{\"timeoutSeconds\":120}")]
  [InlineData(/*lang=json,strict*/ "{\"timeoutSeconds\":120,\"name\":\"\"}")]
  public async Task Strict_Input_Validation(string args)
  {
    SkillInvokeTool tool = MakeTool(Skill("deploy"), null, _ => { });
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_invoke", args), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
  }

  private static SkillInvokeTool MakeTool(SkillDefinition skill, IConversationSink? conversation, Action<string> notice) =>
      new(new FakePort(skill), conversation, notice);

  private static SkillInvokeTool MakeTool(SkillDefinition a, SkillDefinition b, IConversationSink? conversation, Action<string> notice) =>
      new(new FakePort(a, b), conversation, notice);

  /// <summary>Fake port: renders the real service's System-line contract
  ///     (inline body, or the oversize fallback) for the skill(s) it knows;
  ///     unknown names fail SkillNotFound, multiple skills fail
  ///     AmbiguousSkill — mirroring SkillInvocationService's resolution.</summary>
  private sealed class FakePort(params SkillDefinition[] skills) : ISkillInvocationPort
  {
    public Task<Result<SkillInvocationPortResult>> InvokeAsync(string name, string? arguments, CancellationToken ct = default)
    {
      SkillDefinition[] matches = [.. skills.Where(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))];
      if (matches.Length == 0)
      {
        return Task.FromResult(Result.Failure<SkillInvocationPortResult>(new DomainError("SkillNotFound",
            $"No skill named '{name.Trim()}' is in the catalog.")));
      }

      if (matches.Length > 1)
      {
        return Task.FromResult(Result.Failure<SkillInvocationPortResult>(new DomainError("AmbiguousSkill",
            $"'{name.Trim()}' matches multiple skills: {string.Join(", ", matches.Select(m => m.Name))}.")));
      }

      SkillDefinition skill = matches[0];
      string description = skill.Description.Length <= 60 ? skill.Description : skill.Description[..60] + "…";
      string header = $"[skill invoked: {skill.Name}]\n{description}";
      string argsLine = string.IsNullOrWhiteSpace(arguments) ? string.Empty : $"\narguments: {arguments.Trim()}";
      string line = skill.Body.Length <= 4000
          ? $"{header}\n{skill.Body}{argsLine}"
          : $"{header}{argsLine}\n[body too large to inline ({skill.Body.Length} characters) — load it with skill_view]";
      return Task.FromResult(Result.Success(new SkillInvocationPortResult(
          skill.Name, line, BodyInlined: skill.Body.Length <= 4000, SkipReason: skill.Body.Length <= 4000 ? null : "oversize")));
    }
  }

  private sealed class FakeSink : IConversationSink
  {
    public List<string> Lines { get; } = [];

    public void AddSystemMessage(string text) => Lines.Add(text);
  }
}

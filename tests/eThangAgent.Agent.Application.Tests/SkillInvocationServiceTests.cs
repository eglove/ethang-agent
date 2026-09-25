using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>SkillInvocationService (spec #28 task 1): resolution + verbatim
///     message render + 4,000-char size guard; manual skills resolve the same
///     way; no skill_usage rows are ever written.</summary>
public class SkillInvocationServiceTests
{
  private const string Body = "Step one. Step two.";

  private static SkillDefinition Skill(string name, bool manual = false, string? body = null) => new(
      name, "Does " + name + " things", body ?? Body, 1, manual ? SkillSource.File : SkillSource.BuiltIn,
      null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, manual, null);

  private static SkillInvocationService Make(params SkillDefinition[] skills) =>
      new(new FakeCatalog(skills));

  private static Result<SkillInvocation> Invoke(SkillInvocationService svc, string name, string? args = null) =>
      svc.InvokeAsync(name, args, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

  [Fact]
  public void Found_Skill_RendersVerbatimMessage()
  {
    Result<SkillInvocation> r = Invoke(Make(Skill("deploy")), "deploy");
    Assert.True(r.IsSuccess);
    Assert.Equal("[skill invoked: deploy]\nDoes deploy things\n" + Body, r.Value.SystemLine);
    Assert.True(r.Value.BodyInlined);
    Assert.Null(r.Value.SkipReason);
  }

  [Fact]
  public void Arguments_CarriedAsFinalLine()
  {
    Result<SkillInvocation> r = Invoke(Make(Skill("deploy")), "deploy", "staging now");
    Assert.True(r.IsSuccess);
    Assert.Equal("[skill invoked: deploy]\nDoes deploy things\n" + Body + "\narguments: staging now", r.Value.SystemLine);
  }

  [Fact]
  public void Manual_Skill_ResolvedIdentically()
  {
    Result<SkillInvocation> r = Invoke(Make(Skill("deploy-proc", manual: true)), "deploy-proc");
    Assert.True(r.IsSuccess);
    Assert.Contains("[skill invoked: deploy-proc]", r.Value.SystemLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Unknown_Name_FailsSkillNotFound()
  {
    Result<SkillInvocation> r = Invoke(Make(Skill("deploy")), "nope");
    Assert.False(r.IsSuccess);
    Assert.Equal("SkillNotFound", r.Error.Code);
  }

  [Fact]
  public void Ambiguous_MultiHit_FailsListingMatches()
  {
    Result<SkillInvocation> r = Invoke(Make(Skill("deploy"), Skill("Deploy")), "deploy");
    Assert.False(r.IsSuccess);
    Assert.Equal("AmbiguousSkill", r.Error.Code);
    Assert.Contains("deploy", r.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Oversize_Body_FallsBackToPointer()
  {
    string big = string.Create(4500, 'x', static (span, c) => span.Fill(c));
    Result<SkillInvocation> r = Invoke(Make(Skill("big", body: big)), "big");
    Assert.True(r.IsSuccess);
    Assert.False(r.Value.BodyInlined);
    Assert.Equal("oversize", r.Value.SkipReason);
    Assert.Contains("skill_view", r.Value.SystemLine, StringComparison.Ordinal);
    Assert.DoesNotContain(big, r.Value.SystemLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Exactly4000_Characters_StillInline()
  {
    string body = string.Create(4000, 'x', static (span, c) => span.Fill(c));
    Result<SkillInvocation> r = Invoke(Make(Skill("edge", body: body)), "edge");
    Assert.True(r.IsSuccess);
    Assert.True(r.Value.BodyInlined);
  }

  private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(skills));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
      SkillDefinition? m = skills.FirstOrDefault(s => s.Name == name);
      return Task.FromResult(m is not null
          ? Result.Success(m)
          : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
    }
  }
}
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>Registry address grammar (spec #27 task 1): owner/repo,
/// owner/repo/skill, git URLs, and skills.sh entries parse into a typed
/// record; everything else fails InvalidAddress with the reason.</summary>
public class SkillAddressTests
{
  [Theory]
  [InlineData("owner/repo", SkillAddressKind.OwnerRepo, "owner", "repo", null)]
  [InlineData("owner/repo.git", SkillAddressKind.OwnerRepo, "owner", "repo", null)]
  [InlineData("Owner/Repo.GIT", SkillAddressKind.OwnerRepo, "Owner", "Repo", null)]
  [InlineData("owner/repo/SKILL", SkillAddressKind.OwnerRepoSkill, "owner", "repo", "SKILL")]
  [InlineData("owner/repo/skill.git", SkillAddressKind.OwnerRepoSkill, "owner", "repo", "skill.git")]
  [InlineData("https://github.com/owner/repo", SkillAddressKind.GitUrl, "owner", "repo", null)]
  [InlineData("https://github.com/owner/repo.git", SkillAddressKind.GitUrl, "owner", "repo", null)]
  [InlineData("http://gitlab.com/o/r", SkillAddressKind.GitUrl, "o", "r", null)]
  [InlineData("my-deploy-skill", SkillAddressKind.SkillsShEntry, "", "", null)]
  public void Create_AcceptedForms_ParseKindOwnerRepoSubSkill(
      string raw, SkillAddressKind kind, string owner, string repo, string? subSkill)
  {
    Result<SkillAddress> r = SkillAddress.Create(raw);
    Assert.True(r.IsSuccess);
    Assert.Equal(kind, r.Value.Kind);
    Assert.Equal(owner, r.Value.Owner);
    Assert.Equal(repo, r.Value.Repo);
    Assert.Equal(subSkill, r.Value.SubSkill);
  }

  [Fact]
  public void Create_TrimsSurroundingWhitespace_RawIsTrimmed()
  {
    Result<SkillAddress> r = SkillAddress.Create("  owner/repo  ");
    Assert.True(r.IsSuccess);
    Assert.Equal("owner/repo", r.Value.Raw);
  }

  [Theory]
  [InlineData("owner/repo", "https://github.com/owner/repo.git")]
  [InlineData("owner/repo/skill.git", "https://github.com/owner/repo.git")]
  [InlineData("http://gitlab.com/o/r", "https://gitlab.com/o/r.git")]
  [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo.git")]
  public void ToCloneUrl_NormalizesToHttpsAndAppendsGit(string raw, string expected)
  {
    Result<SkillAddress> r = SkillAddress.Create(raw);
    Assert.True(r.IsSuccess);
    Assert.Equal(new Uri(expected), r.Value.ToCloneUrl());
  }

  [Fact]
  public void ToCloneUrl_SkillsShEntry_IsProgrammerError()
  {
    Result<SkillAddress> r = SkillAddress.Create("my-deploy-skill");
    Assert.True(r.IsSuccess);
    _ = Assert.Throws<InvalidOperationException>(r.Value.ToCloneUrl);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("a/b/c/d")]
  [InlineData("a//b")]
  [InlineData("/b")]
  [InlineData("a/")]
  [InlineData("https://github.com/owner/repo/")]
  [InlineData("a/.")]
  [InlineData("a/..")]
  [InlineData("foo.bar")]
  [InlineData("htp://x")]
  [InlineData("ftp://host/o/r")]
  public void Create_MalformedInput_FailsInvalidAddress(string raw)
  {
    Result<SkillAddress> r = SkillAddress.Create(raw);
    Assert.False(r.IsSuccess);
    Assert.NotNull(r.Error);
    Assert.Equal("InvalidAddress", r.Error.Code);
    Assert.False(string.IsNullOrWhiteSpace(r.Error.Message));
  }

  [Fact]
  public void Create_OverlongToken_FailsInvalidAddress()
  {
    string raw = string.Create(300, 'a', static (span, c) => span.Fill(c));
    Result<SkillAddress> r = SkillAddress.Create(raw);
    Assert.False(r.IsSuccess);
    Assert.NotNull(r.Error);
    Assert.Equal("InvalidAddress", r.Error.Code);
  }
}

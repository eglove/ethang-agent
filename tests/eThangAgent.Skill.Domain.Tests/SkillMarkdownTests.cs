using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

public class SkillMarkdownTests
{
    private const string Doc = "---\nname: test-skill\ndescription: Does things.\n---\n\n# Body here\n\nLine two";

    [Fact]
    public void WellFormedDoc_ParsesNameDescriptionAndVerbatimBody()
    {
        var r = SkillMarkdown.Parse(Doc);
        Assert.True(r.IsSuccess);
        Assert.Equal("test-skill", r.Value!.Name);
        Assert.Equal("Does things.", r.Value.Description);
        Assert.Equal("# Body here\n\nLine two", r.Value.Body);
    }

    [Fact]
    public void CrlfDocs_Parse()
    {
        var r = SkillMarkdown.Parse(Doc.Replace("\n", "\r\n"));
        Assert.True(r.IsSuccess);
        Assert.Equal("test-skill", r.Value!.Name);
    }

    [Fact]
    public void MissingOpeningFence_Fails()
    {
        var r = SkillMarkdown.Parse("name: x\n---\nbody");
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingFrontmatter", r.Error!.Code);
    }

    [Theory]
    [InlineData("---\ndescription: d\n---\nb")]
    [InlineData("---\nname: n\n---\nb")]
    public void MissingRequiredKey_Fails(string doc)
    {
        var r = SkillMarkdown.Parse(doc);
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingKey", r.Error!.Code);
    }

    [Fact]
    public void EmptyDescription_Fails()
    {
        var r = SkillMarkdown.Parse("---\nname: n\ndescription:\n---\nb");
        Assert.False(r.IsSuccess);
        Assert.Equal("EmptyDescription", r.Error!.Code);
    }

    [Fact]
    public void UnknownKeys_Tolerated()
    {
        var doc = "---\nname: n\ndescription: d\nversion: 9\nsomething-else: x\n---\nB";
        var r = SkillMarkdown.Parse(doc);
        Assert.True(r.IsSuccess);
        Assert.Equal("B", r.Value!.Body);
    }

    [Fact]
    public void NoClosingFence_Fails()
    {
        var r = SkillMarkdown.Parse("---\nname: n\ndescription: d\nno fence");
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingFrontmatter", r.Error!.Code);
    }
}

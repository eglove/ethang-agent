
namespace eThangAgent.ToolDomain.Tests;

/// <summary>skill_search + skill_registry tool contracts (plan #29 task 9):
/// strict input validation, verbatim output lines, error codes passed
/// through verbatim from the service.</summary>
public class SkillRegistryToolTests
{
  [Fact]
  public async Task Search_HappyPath_RendersBoundedTable()
  {
    FakeSkillsSh skillsSh = new();
    skillsSh.Respond("deploy", "{\"skills\":[{\"id\":\"o/r/deploy\",\"name\":\"deploy\",\"installs\":5},{\"id\":\"o/r/b\",\"name\":\"b\",\"installs\":2}]}");
    SkillSearchTool tool = MakeSearchTool(skillsSh.AsAccess());

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_search", "{\"timeoutSeconds\":120,\"query\":\"deploy\"}"), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill search: deploy]", result.Content, StringComparison.Ordinal);
    Assert.Contains("1. deploy — 5 installs — o/r/deploy", result.Content, StringComparison.Ordinal);
    Assert.Contains("2. b — 2 installs — o/r/b", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Search_TruncatesAtFifteen_WithMarker()
  {
    IEnumerable<string> entries = Enumerable.Range(1, 20)
        .Select(i => "{\"id\":\"o/r/s" + i + "\",\"name\":\"s" + i + "\",\"installs\":" + i + "}");
    FakeSkillsSh skillsSh = new();
    skillsSh.Respond("many", "{\"skills\":[" + string.Join(",", entries) + "]}");
    SkillSearchTool tool = MakeSearchTool(skillsSh.AsAccess());

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_search", "{\"timeoutSeconds\":120,\"query\":\"many\"}"), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("+5 more (call skill_search again to refine)", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Search_ZeroResults_NeverSilent()
  {
    FakeSkillsSh skillsSh = new();
    skillsSh.Respond("ghost", "{\"skills\":[]}");
    SkillSearchTool tool = MakeSearchTool(skillsSh.AsAccess());

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("skill_search", "{\"timeoutSeconds\":120,\"query\":\"ghost\"}"), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill search: ghost] no results", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("{\"timeoutSeconds\":120}")]
  [InlineData("{\"timeoutSeconds\":120,\"query\":\"\"}")]
  [InlineData("{\"timeoutSeconds\":120,\"query\":\"x\",\"extra\":1}")]
  public async Task Search_StrictInputValidation(string args)
  {
    SkillSearchTool tool = MakeSearchTool(new FakeSkillsSh().AsAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_search", args), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
  }

  [Fact]
  public async Task Registry_Install_ReportsVerbatim()
  {
    using SkillRegistryServiceHarness h = MakeRegistryHarness();
    h.Registry.StageSkills("o/r", ("alpha", "body"));

    ToolResult result = await h.Tool.ExecuteAsync(
        new RawToolInput("skill_registry", "{\"timeoutSeconds\":300,\"action\":\"Install\",\"address\":\"o/r\",\"target\":\"workspace\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill-registry] installed 1 skill(s) into workspace: alpha", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Registry_Install_AdvisoryGate_ErrorVerbatim()
  {
    using SkillRegistryServiceHarness h = MakeRegistryHarness();
    h.Registry.StageSkills("o/r", ("alpha", "key sk-abc123def456ghi789jkl012"));

    ToolResult result = await h.Tool.ExecuteAsync(
        new RawToolInput("skill_registry", "{\"timeoutSeconds\":300,\"action\":\"Install\",\"address\":\"o/r\",\"target\":\"workspace\"}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [AdvisoryFindings]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Registry_Uninstall_RemovedLine()
  {
    using SkillRegistryServiceHarness h = MakeRegistryHarness();
    h.Registry.StageSkills("o/r", ("alpha", "body"));
    _ = await h.Tool.ExecuteAsync(
        new RawToolInput("skill_registry", "{\"timeoutSeconds\":300,\"action\":\"Install\",\"address\":\"o/r\",\"target\":\"workspace\"}"),
        TestContext.Current.CancellationToken);

    ToolResult result = await h.Tool.ExecuteAsync(
        new RawToolInput("skill_registry", "{\"timeoutSeconds\":300,\"action\":\"Uninstall\",\"name\":\"alpha\",\"target\":\"workspace\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Contains("[skill-registry] removed alpha from workspace", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("{\"timeoutSeconds\":300,\"action\":\"Bogus\"}")]
  [InlineData("{\"timeoutSeconds\":300,\"action\":\"Install\"}")]
  [InlineData("{\"timeoutSeconds\":300,\"action\":\"Install\",\"address\":\"o/r\",\"target\":\"both\"}")]
  [InlineData("{\"timeoutSeconds\":300,\"action\":\"Update\",\"address\":\"o/r\"}")]
  [InlineData("{\"timeoutSeconds\":300,\"action\":\"Uninstall\",\"target\":\"workspace\"}")]
  public async Task Registry_StrictInputValidation(string args)
  {
    using SkillRegistryServiceHarness h = MakeRegistryHarness();
    ToolResult result = await h.Tool.ExecuteAsync(new RawToolInput("skill_registry", args), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
  }

  private static SkillSearchTool MakeSearchTool(ISkillsShAccess access) =>
      new(SkillRegistryServiceHarness.MakeServiceOnly(access, []));

  private static SkillRegistryServiceHarness MakeRegistryHarness()
  {
    string dir = Path.Combine(Path.GetTempPath(), "regtool-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(dir);
    return new SkillRegistryServiceHarness(dir);
  }

  private sealed class SkillRegistryServiceHarness : IDisposable
  {
    private readonly FakeSkillsSh _skillsSh = new();
    private readonly string _dir;
    public FakeRegistryAccess Registry { get; } = new();
    public SkillRegistryTool Tool { get; }

    public SkillRegistryServiceHarness(string dir)
    {
      _dir = dir;
      Tool = new SkillRegistryTool(new SkillRegistryService(
          Registry.AsAccess(), _skillsSh.AsAccess(), new FakeCatalog().AsCatalog(),
          () => "workspace", () => [dir]));
    }

    internal static SkillRegistryService MakeServiceOnly(ISkillsShAccess access, IReadOnlyList<string> dirs) =>
        new(new FakeRegistryAccess().AsAccess(), access, new FakeCatalog().AsCatalog(), () => "workspace", () => dirs);

    public void Dispose()
    {
      try
      {
        Directory.Delete(_dir, true);
      }
      catch (IOException)
      {
        // best-effort temp cleanup
      }
    }
  }
}

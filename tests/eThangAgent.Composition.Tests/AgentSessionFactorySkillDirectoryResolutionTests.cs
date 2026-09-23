// The raw JSON literals below are the unit under test: the resolution rules over
// hand-written stored shapes. JSON002 fires only in the format/IDE host; the pragma
// pair mirrors SkillDirectoryPreferencesTests.
#pragma warning disable JSON002
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Fix-round coverage for <see cref="AgentSessionFactory.ResolveSkillDirectories"/> -
///     the resolution site where the duplicate-path rule, Global-before-Workspace
///     ordering, enabled filtering, and parse-failure-to-empty-list semantics live
///     (skill-routing Phase 1 Task 6). The method is an internal static pure seam
///     over the two raw stored lists; the tests pin the rule matrix directly
///     (eThangAgent.Composition declares InternalsVisibleTo for this assembly).
///     The parse's shape matrix is pinned by SkillDirectoryPreferencesTests; these
///     tests cover what resolution adds on top of a successful parse.</summary>
public class AgentSessionFactorySkillDirectoryResolutionTests
{
  [Fact]
  public void Resolve_Global_And_Distinct_Workspace_Entry_Both_Present_Global_First()
  {
    string globalStored = @"[{""path"":""C:\\skills\\global-only\\"",""enabled"":true}]";
    string workspaceStored = @"[{""path"":""C:\\proj\\skills\\"",""enabled"":true}]";

    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        globalStored, workspaceStored, @"C:\proj");

    Assert.Equal(2, resolved.Count);
    Assert.Equal(new SkillDirectory(@"C:\skills\global-only\", SkillDirectoryScope.Global), resolved[0]);
    Assert.Equal(new SkillDirectory(@"C:\proj\skills\", SkillDirectoryScope.Workspace), resolved[1]);
  }

  [Fact]
  public void Resolve_Workspace_Path_Equal_To_Global_Case_Insensitive_Trailing_Separator_Skips_Workspace_Entry()
  {
    string globalStored = @"[{""path"":""C:\\Skills\\Shared"",""enabled"":true}]";
    string workspaceStored = @"[{""path"":""c:\\skills\\shared\\"",""enabled"":true}]";

    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        globalStored, workspaceStored, @"C:\proj");

    SkillDirectory expected = new(@"C:\Skills\Shared", SkillDirectoryScope.Global);
    Assert.Equal(expected, Assert.Single(resolved));
  }

  [Fact]
  public void Resolve_Disabled_Workspace_Duplicate_Is_Filtered_Not_Counted_As_Skip()
  {
    string globalStored = @"[{""path"":""C:\\skills\\shared"",""enabled"":true},{""path"":""C:\\skills\\other"",""enabled"":true}]";
    string workspaceStored = @"[{""path"":""C:\\skills\\shared"",""enabled"":false},{""path"":""C:\\skills\\workspace-only"",""enabled"":true}]";

    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        globalStored, workspaceStored, @"C:\proj");

    Assert.Equal(3, resolved.Count);
    Assert.Equal(@"C:\skills\shared", resolved[0].Path);
    Assert.Equal(SkillDirectoryScope.Global, resolved[0].Scope);
    Assert.Equal(@"C:\skills\other", resolved[1].Path);
    Assert.Equal(@"C:\skills\workspace-only", resolved[2].Path);
    Assert.Equal(SkillDirectoryScope.Workspace, resolved[2].Scope);
  }

  [Fact]
  public void Resolve_Disabled_Global_Does_Not_Reserve_Path_Workspace_Entry_Survives()
  {
    string globalStored = @"[{""path"":""C:\\skills\\shared"",""enabled"":false}]";
    string workspaceStored = @"[{""path"":""C:\\skills\\shared"",""enabled"":true}]";

    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        globalStored, workspaceStored, @"C:\proj");

    Assert.Equal(new SkillDirectory(@"C:\skills\shared", SkillDirectoryScope.Workspace), Assert.Single(resolved));
  }

  [Fact]
  public void Resolve_Corrupt_Stored_Json_Returns_Empty_List_Without_Throwing()
  {
    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        "not json at all", @"{""path"":""x""}", @"C:\proj");

    Assert.Empty(resolved);
  }

  [Fact]
  public void Resolve_Corrupt_Global_List_With_Valid_Workspace_List_Resolves_Workspace_Entries()
  {
    string workspaceStored = @"[{""path"":""C:\\ws\\skills\\"",""enabled"":true}]";

    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        "not json at all", workspaceStored, @"C:\proj");

    Assert.Equal(new SkillDirectory(@"C:\ws\skills\", SkillDirectoryScope.Workspace), Assert.Single(resolved));
  }

  [Fact]
  public void Resolve_Null_Stored_Values_Returns_Empty_List()
  {
    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(null, null, @"C:\proj");

    Assert.Empty(resolved);
  }

  [Fact]
  public void Resolve_Relative_Workspace_Path_Normalized_Against_Root_Deduplicates_Against_Global()
  {
    string globalStored = @"[{""path"":""C:\\proj\\skills"",""enabled"":true}]";
    string workspaceStored = @"[{""path"":""skills"",""enabled"":true}]";

    List<SkillDirectory> resolved = AgentSessionFactory.ResolveSkillDirectories(
        globalStored, workspaceStored, @"C:\proj");

    SkillDirectory single = Assert.Single(resolved);
    Assert.Equal(@"C:\proj\skills", single.Path);
    Assert.Equal(SkillDirectoryScope.Global, single.Scope);
  }
}
#pragma warning restore JSON002

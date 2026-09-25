using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Skill autocomplete popup (plan #30 task 4): pure view-model state
///     machine over a catalog source. Open rule is the RAW input's first
///     character '/' (no trim); manual skills list first; filter reads only the
///     name token after '/'; navigation wraps; Tab/Enter accept autofills the
///     full name; Esc closes.</summary>
public class SkillAutocompleteViewModelTests
{
  private static SkillOption Opt(string name, bool manual = false, string? description = null) =>
      new(name, description ?? ("Does " + name + " things"), manual);

  private static SkillAutocompleteViewModel Make(params SkillOption[] options) =>
      new(() => options);

  private static readonly SkillOption[] Catalog =
  [
      Opt("deploy-app"),
      Opt("dep-check"),
      Opt("Other", manual: true),
  ];

  [Fact]
  public void Opens_Only_On_Raw_First_Character_Slash()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update(" /deploy");
    Assert.False(vm.IsOpen);
    vm.Update("/deploy");
    Assert.True(vm.IsOpen);
  }

  [Fact]
  public void Lists_All_Skills_Manual_First_Then_Name_Ordinal()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/");
    Assert.True(vm.IsOpen);
    Assert.Equal(3, vm.Options.Count);
    Assert.Equal("Other", vm.Options[0].Name);
    Assert.Equal("dep-check", vm.Options[1].Name);
    Assert.Equal("deploy-app", vm.Options[2].Name);
  }

  [Fact]
  public void Filter_Is_CaseInsensitive_On_Name()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/DEP");
    Assert.Equal(["dep-check", "deploy-app"], vm.Options.Select(o => o.Name));
  }

  [Fact]
  public void Filter_Reads_Only_The_Name_Token()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/deploy staging now");
    Assert.Equal(["deploy-app"], vm.Options.Select(o => o.Name));
  }

  [Fact]
  public void Navigation_Wraps_At_Bounds()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/dep");
    Assert.Equal(0, vm.SelectedIndex);
    vm.MoveDown();
    vm.MoveDown();
    Assert.Equal(0, vm.SelectedIndex); // wrapped forward past the last
    vm.MoveUp();
    Assert.Equal(1, vm.SelectedIndex); // wrapped backward past the first
  }

  [Fact]
  public void Accept_Returns_Full_Name_And_Closes()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/dep");
    string? accepted = vm.Accept();
    Assert.Equal("dep-check", accepted);
    Assert.False(vm.IsOpen);
  }

  [Fact]
  public void Accept_Respects_Selection()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/dep");
    vm.MoveDown();
    Assert.Equal("deploy-app", vm.Accept());
  }

  [Fact]
  public void Esc_Closes()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/dep");
    vm.Close();
    Assert.False(vm.IsOpen);
  }

  [Fact]
  public void Click_Accepts_The_Clicked_Option()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/dep");
    string? accepted = vm.Click(1);
    Assert.Equal("deploy-app", accepted);
    Assert.False(vm.IsOpen);
  }

  [Fact]
  public void Accept_When_Closed_Returns_Null()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    Assert.Null(vm.Accept());
  }

  [Fact]
  public void No_Matches_Shows_Empty_Options_Still_Open()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/zzz");
    Assert.True(vm.IsOpen);
    Assert.Empty(vm.Options);
  }

  [Fact]
  public void NonSlash_Input_Closes_Again()
  {
    SkillAutocompleteViewModel vm = Make(Catalog);
    vm.Update("/dep");
    Assert.True(vm.IsOpen);
    vm.Update("plain message");
    Assert.False(vm.IsOpen);
  }
}

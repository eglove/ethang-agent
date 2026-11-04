using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ComposerReferenceViewModelTests
{
  private static readonly ReferenceOption[] Catalog =
  [
    new("grill", "Interview the user", ReferenceKind.Skill, "@grill"),
    new("src/AgentView.cs", "", ReferenceKind.File, "@src/AgentView.cs"),
  ];

  private static ComposerReferenceViewModel Vm(params ReferenceOption[] options)
    => new(query => [.. options.Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))]);

  [Fact]
  public void OpensOnAnAtTokenAndClosesWhenTheCaretLeavesIt()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("see @gr", 7);
    Assert.True(vm.IsOpen);
    vm.Update("see @gr and more", 16);
    Assert.False(vm.IsOpen);
  }

  [Fact]
  public void AcceptingDoesNotReopenForTheCommittedToken()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@gr", 3);
    ReferenceCommit commit = vm.Accept()!;
    Assert.Equal("@grill ", commit.InsertText);
    Assert.Equal(7, commit.CaretIndex);
    Assert.False(vm.IsOpen);
    vm.Update("@grill ", 7);
    Assert.False(vm.IsOpen);
  }

  [Fact]
  public void SkillsSortAboveFiles()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@", 1);
    Assert.Equal(ReferenceKind.Skill, vm.Options[0].Kind);
  }

  [Fact]
  public void EmptyOptionsLeaveNoSelection()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@zzz", 4);
    Assert.Equal(-1, vm.SelectedIndex);
    Assert.Null(vm.Accept());
  }

  [Fact]
  public void NavigationWrapsAndClickCommitsTheClickedRow()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@", 1);
    Assert.Equal(0, vm.SelectedIndex);
    vm.MoveUp();
    Assert.Equal(1, vm.SelectedIndex);
    vm.MoveDown();
    Assert.Equal(0, vm.SelectedIndex);

    ReferenceCommit clicked = vm.Click(1)!;
    Assert.Equal("@src/AgentView.cs ", clicked.InsertText);
    Assert.Equal(0, clicked.Start);
    Assert.Equal(1, clicked.Length);
    Assert.Equal(clicked.InsertText.Length, clicked.CaretIndex);
    Assert.False(vm.IsOpen);
  }

  [Fact]
  public void AcceptingADifferentTokenReopens()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@gr", 3);
    Assert.NotNull(vm.Accept());
    vm.Update("@gr and @gr", 11);
    Assert.True(vm.IsOpen);
  }

  [Fact]
  public void ClosedPopupIgnoresNavigationAndAcceptsNothing()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("plain text", 10);
    vm.MoveDown();
    vm.MoveUp();
    Assert.False(vm.IsOpen);
    Assert.Equal(-1, vm.SelectedIndex);
    Assert.Null(vm.Accept());
    Assert.Null(vm.Click(0));
  }
}

using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

public class ComposerReferenceViewModelTests
{
  private static readonly ReferenceOption[] Catalog =
  [
    new("grill", "Interview the user", ReferenceKind.Skill, "@grill"),
    new("src/AgentView.cs", "", ReferenceKind.File, "@src/AgentView.cs"),
    new("new", "Start a new session", ReferenceKind.Command, "/new"),
  ];

  private static ComposerReferenceViewModel Vm(params ReferenceOption[] options)
    => new((trigger, query) => [.. options
        .Where(o => trigger == ReferenceTrigger.Command
            ? o.Kind == ReferenceKind.Command
            : o.Kind != ReferenceKind.Command)
        .Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))]);

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
  public void ASlashTokenListsCommandsOnly()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("/", 1);
    Assert.True(vm.IsOpen);
    ReferenceOption only = Assert.Single(vm.Options);
    Assert.Equal(ReferenceKind.Command, only.Kind);
    Assert.Equal("new", only.Name);
  }

  [Fact]
  public void AnAtTokenNeverListsCommands()
  {
    // The regression this pins: '@new' resolved and appeared in the file search, because the
    // popup mixed every kind under both triggers. A command is not a reference.
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@new", 4);
    Assert.False(vm.IsOpen);
    Assert.DoesNotContain(vm.Options, o => o.Kind == ReferenceKind.Command);
  }

  [Fact]
  public void AnAtTokenListsSkillsAndFiles()
  {
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@", 1);
    Assert.True(vm.IsOpen);
    Assert.Equal(2, vm.Options.Count);
    Assert.DoesNotContain(vm.Options, o => o.Kind == ReferenceKind.Command);
  }

  [Fact]
  public void NoMatchClosesThePopup()
  {
    // Spec 6: the popup closes on no match. An empty box that still owns Enter would make
    // the next keystroke un-sendable.
    ComposerReferenceViewModel vm = Vm(Catalog);
    vm.Update("@zzzz", 5);
    Assert.False(vm.IsOpen);
    Assert.Empty(vm.Options);
    Assert.Equal(-1, vm.SelectedIndex);
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

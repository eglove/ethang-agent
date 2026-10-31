using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #285: the dialog's completion-gate section - the three-way choice, the rule
///     that NOTHING is auto-filled, and the two controls staying mutually exclusive.
///     <para>THE THREE ANSWERS ARE THE USER'S DECISION (D1/D2): declare commands, decline (and be
///     remembered), or say nothing and decide later. The controls must be able to express all
///     three, and an untouched dialog must persist NOTHING - an accidental decline on a user's
///     behalf would be a decision they never made.</para></summary>
public class NewAgentGateSectionTests
{
  private static readonly IReadOnlyList<ProviderOption> Options =
      [new("openrouter", "OpenRouter")];

  private static NewAgentViewModel CreateVm() => new(Options, "openrouter");

  // ── nothing is auto-filled (D2) ──────────────────────────────────────────

  [Fact]
  public void AWorkspaceNobodyAsked_StartsWithAnEmptyBox_NotASuggestedCommand()
  {
    NewAgentViewModel vm = CreateVm();

    vm.ApplyGateState(WorkspaceGateState.NeverAsked);

    // THE USER'S REJECTION OF THE FIRST TWO OPTIONS, pinned: a pre-filled box they click past
    // is a default wearing a decision's clothes.
    Assert.Equal(string.Empty, vm.GateCommands);
    Assert.False(vm.GateDeclined);
    Assert.True(vm.GateNeedsAnswer);
    Assert.True(vm.ShowGateQuestion);
  }

  [Fact]
  public void ASuggestion_IsTextBesideAnEmptyBox_NeverAValue()
  {
    // The reasoning is shown, the box stays empty: the user reads it and types or ignores it.
    NewAgentViewModel vm = CreateVm();
    vm.ApplyGateState(WorkspaceGateState.NeverAsked);
    vm.GateSuggestion = "This looks like a .NET project; checks usually run the build and tests.";

    Assert.Equal(string.Empty, vm.GateCommands);
    Assert.NotNull(vm.GateSuggestion);
  }

  // ── the three answers ────────────────────────────────────────────────────

  [Fact]
  public void Untouched_DeclaresNothing_SoTheWorkspaceKeepsWhateverItHad()
  {
    NewAgentViewModel vm = CreateVm();
    vm.ApplyGateState(WorkspaceGateState.NeverAsked);

    Assert.Equal(GateDeclarationKind.Unchanged, vm.GateChoice().Kind);
  }

  [Fact]
  public void TypingCommands_IsADeclaration_OnePerLine()
  {
    NewAgentViewModel vm = CreateVm();
    vm.ApplyGateState(WorkspaceGateState.NeverAsked);
    vm.GateCommands = "dotnet build\n\n  dotnet test  \n";

    GateDeclaration choice = vm.GateChoice();

    Assert.Equal(GateDeclarationKind.Declare, choice.Kind);
    // Blank lines are dropped and surrounding whitespace trimmed: a blank command is not a
    // command, and it would fail the gate for a reason nobody can act on.
    Assert.Equal(["dotnet build", "dotnet test"], choice.Commands);
  }

  [Fact]
  public void TickingDecline_IsADecline_AndIsRemembered()
  {
    NewAgentViewModel vm = CreateVm();
    vm.ApplyGateState(WorkspaceGateState.NeverAsked);
    vm.GateDeclined = true;

    GateDeclaration choice = vm.GateChoice();

    Assert.Equal(GateDeclarationKind.Decline, choice.Kind);
    Assert.Empty(choice.Commands);
  }

  // ── the controls cannot contradict each other ────────────────────────────

  [Fact]
  public void TypingCommands_ClearsATickedDecline()
  {
    // Two contradictory statements would reach the store and the last writer would win
    // invisibly. Whichever control states an answer LAST wins, visibly.
    NewAgentViewModel vm = CreateVm();
    vm.ApplyGateState(WorkspaceGateState.NeverAsked);
    vm.GateDeclined = true;

    vm.GateCommands = "go test ./...";

    Assert.False(vm.GateDeclined);
    Assert.Equal(GateDeclarationKind.Declare, vm.GateChoice().Kind);
  }

  [Fact]
  public void TickingDecline_ClearsTypedCommands()
  {
    NewAgentViewModel vm = CreateVm();
    vm.ApplyGateState(WorkspaceGateState.NeverAsked);
    vm.GateCommands = "go test ./...";

    vm.GateDeclined = true;

    Assert.Equal(string.Empty, vm.GateCommands);
    Assert.Equal(GateDeclarationKind.Decline, vm.GateChoice().Kind);
  }

  // ── what an already-answered workspace shows ─────────────────────────────

  [Fact]
  public void ADeclaredWorkspace_PrefillsItsOwnCommands_ForEditing()
  {
    NewAgentViewModel vm = CreateVm();

    vm.ApplyGateState(new WorkspaceGateState(GateStateKind.Declared, ["npm ci", "npm test"]));

    Assert.Equal("npm ci\nnpm test", vm.GateCommands);
    Assert.False(vm.GateNeedsAnswer);
    // The section stays visible so the user can CHANGE what they declared.
    Assert.True(vm.ShowGateQuestion);
  }

  [Fact]
  public void ADeclinedWorkspace_IsNotAskedAgain()
  {
    // The nagging the issue forbids: a declined workspace shows no question.
    NewAgentViewModel vm = CreateVm();

    vm.ApplyGateState(WorkspaceGateState.Declined);

    Assert.False(vm.GateNeedsAnswer);
    Assert.False(vm.ShowGateQuestion);
    Assert.True(vm.GateDeclined);
  }

  [Fact]
  public void AnUnknownWorkspace_AsksRatherThanAssumingADecision()
  {
    // An unreadable workspace must still open the dialog; asking again is recoverable.
    NewAgentViewModel vm = CreateVm();

    vm.ApplyGateState(WorkspaceGateState.Unknown);

    Assert.True(vm.GateNeedsAnswer);
  }

  // ── the choice carries the decision out of the dialog ────────────────────

  [Fact]
  public void TheConfirmedChoice_CarriesTheGateDecision()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SelectedProvider = Options[0];
    vm.WorkspaceRoot = @"C:\ws";
    vm.GateCommands = "dotnet test";
    List<NewAgentChoice> carried = [];
    vm.OpenRequested += (_, choice) => carried.Add(choice);

    vm.OpenCommand.Execute(null);

    NewAgentChoice confirmed = Assert.Single(carried);
    GateDeclaration gate = Assert.IsType<GateDeclaration>(confirmed.Gate);
    Assert.Equal(GateDeclarationKind.Declare, gate.Kind);
    Assert.Equal(["dotnet test"], gate.Commands);
  }

  [Fact]
  public void TheConfirmedChoice_WithNoAnswer_CarriesAnUnchangedDecision()
  {
    NewAgentViewModel vm = CreateVm();
    vm.SelectedProvider = Options[0];
    vm.WorkspaceRoot = @"C:\ws";
    List<NewAgentChoice> carried = [];
    vm.OpenRequested += (_, choice) => carried.Add(choice);

    vm.OpenCommand.Execute(null);

    GateDeclaration gate = Assert.IsType<GateDeclaration>(Assert.Single(carried).Gate);
    Assert.Equal(GateDeclarationKind.Unchanged, gate.Kind);
  }
}

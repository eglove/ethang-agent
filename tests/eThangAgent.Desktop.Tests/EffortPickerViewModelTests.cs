using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.Tests;

public class EffortPickerViewModelTests
{
  [Fact]
  public void Rows_Are_Model_Default_First_Then_The_Seven_Levels()
  {
    EffortPickerViewModel vm = new(ReasoningEffort.High);

    Assert.Null(vm.Rows[0].Level);
    Assert.Equal(
        [
            ReasoningEffort.Max, ReasoningEffort.ExtraHigh, ReasoningEffort.High,
            ReasoningEffort.Medium, ReasoningEffort.Low, ReasoningEffort.Minimal, ReasoningEffort.None,
        ],
        vm.Rows.Skip(1).Select(r => r.Level!.Value));
  }

  [Fact]
  public void Ctor_Preselects_The_Session_Current_Level()
  {
    EffortPickerViewModel vm = new(ReasoningEffort.Minimal);

    Assert.Equal(ReasoningEffort.Minimal, vm.SelectedRow!.Level);
    Assert.True(vm.ConfirmCommand.CanExecute(null));
  }

  [Fact]
  public void Ctor_Without_Current_Preselects_The_Model_Default_Row()
  {
    EffortPickerViewModel vm = new(null);

    Assert.Null(vm.SelectedRow!.Level);
    Assert.True(vm.ConfirmCommand.CanExecute(null));
  }

  [Fact]
  public void Confirm_Emits_The_Selected_Level()
  {
    EffortPickerViewModel vm = new(ReasoningEffort.High);
    EffortChoice? received = null;
    vm.ConfirmRequested += (_, choice) => received = choice;
    vm.SelectedRow = vm.Rows.First(r => r.Level == ReasoningEffort.Low);

    vm.ConfirmCommand.Execute(null);

    Assert.Equal(ReasoningEffort.Low, received!.Level);
  }

  [Fact]
  public void Confirm_Emits_Null_Level_For_The_Model_Default_Row()
  {
    EffortPickerViewModel vm = new(ReasoningEffort.High);
    EffortChoice? received = null;
    vm.ConfirmRequested += (_, choice) => received = choice;
    vm.SelectedRow = vm.Rows[0]; // model default

    vm.ConfirmCommand.Execute(null);

    Assert.NotNull(received); // default is a real choice, not a cancel
    Assert.Null(received!.Level);
  }

  [Fact]
  public void Confirm_Without_Selection_Emits_Nothing()
  {
    EffortPickerViewModel vm = new(ReasoningEffort.High);
    EffortChoice? received = null;
    vm.ConfirmRequested += (_, choice) => received = choice;
    vm.SelectedRow = null;

    Assert.False(vm.ConfirmCommand.CanExecute(null));
    vm.ConfirmCommand.Execute(null); // ICommand.Execute bypasses CanExecute

    Assert.Null(received);
  }

  [Fact]
  public void Display_Names_Are_Distinct_And_Name_The_Default()
  {
    ReasoningEffort?[] levels =
    [
        null, ReasoningEffort.Max, ReasoningEffort.ExtraHigh, ReasoningEffort.High,
        ReasoningEffort.Medium, ReasoningEffort.Low, ReasoningEffort.Minimal, ReasoningEffort.None,
    ];

    List<string> names = [.. levels.Select(EffortLevels.DisplayName)];

    Assert.Equal(names.Count, names.Distinct().Count());
    Assert.Equal("Model default", names[0]);
  }
}

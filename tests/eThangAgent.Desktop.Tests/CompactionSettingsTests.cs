using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Zai.ACL;

namespace eThangAgent.Desktop.Tests;


public class CompactionSettingsTests
{
  [Fact]
  public void Automatic_IsDefault_AndCarriesNullModelId()
  {
    SettingsViewModel vm = new(null, null, ZaiEndpointMode.CodingPlan);

    Assert.Equal(CompactionModelOption.Automatic, vm.SelectedCompactionModel);
    Assert.Null(vm.SelectedCompactionModel.ModelId);
    _ = Assert.Single(vm.CompactionModels);
  }

  [Fact]
  public void SaveRequest_CarriesSelectedModelId_AutomaticCarriesNull()
  {
    SettingsViewModel vm = new(null, null, ZaiEndpointMode.CodingPlan,
        compactionModels: [CompactionModelOption.Automatic, new CompactionModelOption("glm-5.3-flash", "glm-5.3-flash")],
        selectedCompactionModel: new CompactionModelOption("glm-5.3-flash", "glm-5.3-flash"));
    SettingsUpdate? received = null;
    vm.SaveRequested += (_, update) => received = update;

    vm.SelectedCompactionModel = new CompactionModelOption("glm-5.3-flash", "glm-5.3-flash");
    vm.SaveCommand.Execute(null);

    Assert.NotNull(received);
    Assert.Equal("glm-5.3-flash", received.CompactionModelId);

    vm.SelectedCompactionModel = CompactionModelOption.Automatic;
    vm.SaveCommand.Execute(null);

    Assert.Null(received.CompactionModelId); // Automatic persists as unset (delete)
  }

  [Fact]
  public void PreferenceKeyShape_IsProviderAndWorkspaceQualified()
  {
    Assert.Equal("compaction_model:openrouter:C:\\ws",
        CompactionModelResolver.PreferenceKey("openrouter", @"C:\ws"));
  }
}

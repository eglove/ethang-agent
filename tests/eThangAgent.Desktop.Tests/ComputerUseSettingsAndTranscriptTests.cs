using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Task 20: the Settings Agents-tab computer-use toggle (prefill + save) and the
///     transcript tool-result entry carrying image parts with a pre-decoded bitmap.</summary>
public class ComputerUseSettingsAndTranscriptTests
{
  [Fact]
  public void ComputerUse_Toggle_Prefills_And_Saves()
  {
    SettingsViewModel vm = new(null, CommitStyle.Conventional, computerUse: true);
    Assert.True(vm.ComputerUse);

    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.ComputerUse = false;
    vm.SaveCommand.Execute(null);

    Assert.NotNull(saved);
    Assert.False(saved.ComputerUse);
  }

  [Fact]
  public void ComputerUse_Default_Is_Off()
  {
    SettingsViewModel vm = new(null);
    SettingsUpdate? saved = null;
    vm.SaveRequested += (_, update) => saved = update;
    vm.SaveCommand.Execute(null);
    Assert.NotNull(saved);
    Assert.False(saved.ComputerUse);
  }

  [AvaloniaFact]
  public void ToolResultEntry_Carries_DecodedImage()
  {
    byte[] png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    TranscriptImage image = new(png);
    ToolResultEntry entry = new("computer", "ok", "state", IsError: false,
        Images: [image]);
    Assert.NotNull(entry.Images);
    TranscriptImage single = Assert.Single(entry.Images);
    Assert.NotNull(single.Bitmap);
    Assert.True(single.Bitmap.PixelSize.Width >= 1);
    image.Dispose();
  }
}

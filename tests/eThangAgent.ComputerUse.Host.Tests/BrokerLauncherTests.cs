
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>The broker launcher contract (task 16): the token comes from
///     ETHANG_COMPUTER_USE_TOKEN, the pipe name from argv[0] with an ETHANG_COMPUTER_USE_PIPE
///     env override, logs go to a bounded file under the app data dir, and bad
///     configuration is an exit-code 2 outcome, never a crash dump.</summary>
public class BrokerLauncherTests
{
  [Fact]
  public void MissingToken_IsConfigError()
  {
    BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(
      ["ethang-cu-x"],
      _ => null,
      _ => null);
    Assert.NotNull(options.Error);
    Assert.Equal(2, options.ExitCode);
  }

  [Fact]
  public void MissingPipeName_IsConfigError()
  {
    BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(
      [],
      _ => "tok",
      _ => null);
    Assert.NotNull(options.Error);
    Assert.Equal(2, options.ExitCode);
  }

  [Fact]
  public void PipeNameFromArg_TokenFromEnv()
  {
    BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(
      ["ethang-cu-abc"],
      _ => "tok",
      _ => null);
    Assert.Null(options.Error);
    Assert.Equal("ethang-cu-abc", options.PipeName);
    Assert.Equal("tok", options.Token);
  }

  [Fact]
  public void EmptyPipeNameArg_IsConfigError()
  {
    BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(
      ["  "],
      _ => "tok",
      _ => null);
    Assert.NotNull(options.Error);
  }

  [Fact]
  public void EnvPipeNameOverride_WinsWhenArgAbsent()
  {
    BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(
      [],
      _ => "tok",
      name => name == "ETHANG_COMPUTER_USE_PIPE" ? "ethang-cu-env" : null);
    Assert.Null(options.Error);
    Assert.Equal("ethang-cu-env", options.PipeName);
  }

  [Fact]
  public void LogPath_IsBoundedLogFileUnderAppData()
  {
    BrokerLaunchOptions options = BrokerLaunchOptions.Resolve(
      ["ethang-cu-l"],
      _ => "tok",
      _ => null);
    Assert.Contains("computer-use", options.LogFilePath, StringComparison.OrdinalIgnoreCase);
    Assert.EndsWith(".log", options.LogFilePath, StringComparison.OrdinalIgnoreCase);
  }
}

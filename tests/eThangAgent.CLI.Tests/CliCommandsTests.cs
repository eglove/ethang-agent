namespace eThangAgent.CLI.Tests;

public class CliCommandsTests
{
    [Fact]
    public void All_ContainsExitQuitAndHelp()
    {
        var names = CliCommands.All.Select(c => c.Name).ToArray();

        Assert.Contains("/exit", names);
        Assert.Contains("/quit", names);
        Assert.Contains("/help", names);
    }

    [Fact]
    public void All_NamesAreUniqueAndSlashPrefixed()
    {
        var names = CliCommands.All.Select(c => c.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("/", n));
    }

    [Fact]
    public void All_EveryCommandHasDescription()
    {
        Assert.All(CliCommands.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Theory]
    [InlineData("/exit", true)]
    [InlineData("/quit", true)]
    [InlineData("/help", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void IsQuit_RecognizesQuitAliasesOnly(string input, bool expected)
    {
        Assert.Equal(expected, CliCommands.IsQuit(input));
    }

    [Theory]
    [InlineData("/help", true)]
    [InlineData("/exit", false)]
    [InlineData("hello", false)]
    public void IsHelp_RecognizesHelpCommandOnly(string input, bool expected)
    {
        Assert.Equal(expected, CliCommands.IsHelp(input));
    }

    [Fact]
    public void Describe_ListsEveryRegisteredCommand()
    {
        var text = CliCommands.Describe();

        Assert.All(CliCommands.All, c => Assert.Contains(c.Name, text));
        Assert.All(CliCommands.All, c => Assert.Contains(c.Description, text));
    }
}

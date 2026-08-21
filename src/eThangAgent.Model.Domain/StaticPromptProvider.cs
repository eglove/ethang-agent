namespace eThangAgent.ModelDomain;

public sealed class StaticPromptProvider(string text) : ISystemPromptProvider
{
    public string Build() => text;
}

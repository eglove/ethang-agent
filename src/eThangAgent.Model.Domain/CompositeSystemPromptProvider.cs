namespace eThangAgent.ModelDomain;

public sealed class CompositeSystemPromptProvider : ISystemPromptProvider
{
    private readonly IReadOnlyList<ISystemPromptProvider> _providers;

    public CompositeSystemPromptProvider(IEnumerable<ISystemPromptProvider> providers)
        => _providers = providers.ToList();

    public string Build()
        => string.Join("\n\n", _providers
            .Select(p => p.Build())
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

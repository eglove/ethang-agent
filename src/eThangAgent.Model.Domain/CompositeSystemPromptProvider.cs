namespace eThangAgent.ModelDomain;

public sealed class CompositeSystemPromptProvider(IEnumerable<ISystemPromptProvider> providers) : ISystemPromptProvider
{
  private readonly IReadOnlyList<ISystemPromptProvider> _providers = [.. providers];

  public string Build()
      => string.Join("\n\n", _providers
          .Select(p => p.Build())
          .Where(s => !string.IsNullOrWhiteSpace(s)));
}

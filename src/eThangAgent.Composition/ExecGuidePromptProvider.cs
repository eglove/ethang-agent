using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

public sealed class ExecGuidePromptProvider : ISystemPromptProvider
{
    private readonly Lazy<ICapabilityRegistry> _registry;

    public ExecGuidePromptProvider(Lazy<ICapabilityRegistry> registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string Build() => $"{ExecGuide.Text}\n\n{CapabilityReferenceRenderer.Render(_registry.Value)}";
}

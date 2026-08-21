using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

public sealed class ExecGuidePromptProvider : ISystemPromptProvider
{
    private readonly ICapabilityRegistry _registry;

    public ExecGuidePromptProvider(ICapabilityRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string Build() => $"{ExecGuide.Text}\n\n{CapabilityReferenceRenderer.Render(_registry)}";
}

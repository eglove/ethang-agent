using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

public sealed class ExecGuidePromptProvider : ISystemPromptProvider
{
    public string Build() => ExecGuide.Text;
}

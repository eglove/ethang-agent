using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class PathResolutionContractTests
{
    [Fact]
    public void WorkspacePathResolver_Satisfies_IPathResolver_Contract()
    {
        IPathResolver resolver = new WorkspacePathResolver("C:\\tmp\\ws");
        var result = resolver.Resolve("C:\\tmp\\ws\\file.txt");
        Assert.True(result.IsSuccess);
    }
}

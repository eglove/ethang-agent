using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class PathResolutionContractTests
{
  [Fact]
  public void WorkspacePathResolver_Satisfies_IPathResolver_Contract()
  {
    WorkspacePathResolver resolver = new("C:\\tmp\\ws");
    Result<string> result = resolver.Resolve("C:\\tmp\\ws\\file.txt");
    Assert.True(result.IsSuccess);
  }
}

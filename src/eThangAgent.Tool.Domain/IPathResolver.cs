using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Seam for turning a model-supplied path argument into an absolute path.</summary>
public interface IPathResolver
{
  Result<string> Resolve(string path);
}

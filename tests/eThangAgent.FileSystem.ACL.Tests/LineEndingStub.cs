using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>A policy that always answers the same convention, for tests whose subject
///     is the write path rather than convention discovery.</summary>
internal sealed class LineEndingStub(LineEnding ending) : ILineEndingPolicy
{
  public static LineEndingStub For(LineEnding ending) => new(ending);

  public LineEnding Resolve(string filePath) => ending;
}

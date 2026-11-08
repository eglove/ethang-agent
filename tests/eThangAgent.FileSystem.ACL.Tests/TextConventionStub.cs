using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>A policy that always answers the same convention, for tests whose subject
///     is the write path rather than convention discovery. The final newline is off by
///     default so a test that does not name it observes the text it wrote byte for byte.</summary>
internal sealed class TextConventionStub(TextConvention convention) : ITextConventionPolicy
{
  public static TextConventionStub For(LineEnding ending) => new(new TextConvention(ending, false));

  public static TextConventionStub InsertingFinalNewline(LineEnding ending) => new(new TextConvention(ending, true));

  public TextConvention Resolve(string filePath) => convention;
}

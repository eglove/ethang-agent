using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The edit tool's AnchorNotFound hint is a format contract: the description
///     must tell the model what the miss error carries (nearest region, gutter format)
///     so a retry corrects the anchor from the error alone.</summary>
public class EditToolAnchorHintDescriptionTests
{
  [Fact]
  public void Edit_Description_StatesTheNearestMatchHint()
  {
    EditTool tool = new(new UnrootedPathResolver(), new StubEditAccess());
    string d = tool.Definition.Description;
    Assert.Contains("AnchorNotFound", d, StringComparison.Ordinal);
    Assert.Contains("Nearest match at line", d, StringComparison.Ordinal);
    Assert.Contains("retry without re-reading", d, StringComparison.Ordinal);
  }

  private sealed class StubEditAccess : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
        => throw new NotImplementedException("not exercised by description tests");

    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default)
        => throw new NotImplementedException("not exercised by description tests");
  }
}

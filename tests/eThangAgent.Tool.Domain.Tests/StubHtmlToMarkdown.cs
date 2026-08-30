namespace eThangAgent.ToolDomain.Tests;

/// <summary>Deterministic converter stub: emits a fixed markdown body.</summary>
internal sealed class StubHtmlToMarkdown : IHtmlToMarkdown
{
  public const string Output = "# Hi";

  public string Convert(string html, Uri baseUrl) => Output;
}

/// <summary>Converter that fails the test if the tool ever calls it.</summary>
internal sealed class ThrowingStubConverter : IHtmlToMarkdown
{
  public string Convert(string html, Uri baseUrl) =>
      throw new InvalidOperationException("converter must not run for non-html content");
}

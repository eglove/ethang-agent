using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace eThangAgent.Analyzers.Tests;

/// <summary>Compiles a source fragment as a test assembly and runs the analyzer over it, so a rule
///     is exercised on the exact shape it governs.</summary>
internal static class AnalyzerHarness
{
  /// <summary>The ceiling the analyzer recognises.</summary>
  private const string Preamble = """
    namespace eThangAgent.TestSupport
    {
      public static class TestCeiling
      {
        public static System.TimeSpan Value { get; } = System.TimeSpan.FromSeconds(120);
      }
    }

    """;

  /// <summary>A stub test framework, emitted as an assembly NAMED for the framework: the analyzer
  ///     governs assemblies that reference one, so the reference has to be real.</summary>
  private static readonly PortableExecutableReference Framework = EmitFramework();

  private static PortableExecutableReference EmitFramework()
  {
    const string Stub = """
      namespace Xunit
      {
        public sealed class FactAttribute : System.Attribute { }
        public sealed class TraitAttribute : System.Attribute
        {
          public TraitAttribute(string name, string value) { }
        }
      }
      """;

    CSharpCompilation stub = CSharpCompilation.Create(
        "xunit.core",
        [CSharpSyntaxTree.ParseText(Stub)],
        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using MemoryStream stream = new();
    Microsoft.CodeAnalysis.Emit.EmitResult emitted = stub.Emit(stream);
    return emitted.Success
        ? MetadataReference.CreateFromImage(stream.ToArray())
        : throw new InvalidOperationException("the stub test framework did not compile");
  }

  /// <summary>Runs the analyzer over <paramref name="source"/> and answers its diagnostics.</summary>
  public static async Task<ImmutableArray<Diagnostic>> DiagnoseAsync(string source)
  {
    CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
    SyntaxTree tree = CSharpSyntaxTree.ParseText(Preamble + source, parseOptions);
    MetadataReference[] references =
    [
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(Thread).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(System.Diagnostics.Stopwatch).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(System.Runtime.GCSettings).Assembly.Location),
      Framework,
    ];

    // The analyzer keys on a test-framework reference, so the assembly must name one.
    CSharpCompilation compilation = CSharpCompilation.Create(
        "xunit.tests",
        [tree],
        references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
        [new TimedWaitAnalyzer()],
        new CompilationWithAnalyzersOptions(
            new AnalyzerOptions([]),
            onAnalyzerException: null,
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false));
    return await withAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
  }

  /// <summary>The diagnostics of one rule, as 'line: message' lines.</summary>
  public static string Describe(ImmutableArray<Diagnostic> diagnostics, string id)
  {
    return string.Join(" | ", diagnostics
        .Where(diagnostic => diagnostic.Id == id)
        .Select(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line + ": "
            + diagnostic.GetMessage(CultureInfo.InvariantCulture)));
  }
}

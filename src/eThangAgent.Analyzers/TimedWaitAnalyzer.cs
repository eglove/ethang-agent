using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace eThangAgent.Analyzers;

/// <summary>Finds a test's wait whose termination is bounded by a wall clock rather than by
///     <c>eThangAgent.TestSupport.TestCeiling.Value</c>. The rule reads the semantic model, so it
///     catches the SHAPE of a load-sensitive wait - a deadline variable, a raced delay, a
///     cancellation timeout, a fixed sleep - instead of one spelling of it.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TimedWaitAnalyzer : DiagnosticAnalyzer
{
  /// <summary>A wait bounded by a clock value that is not the ceiling.</summary>
  public const string ClockBoundId = "ETA0001";

  /// <summary>A fixed-duration wait: the duration IS the bound.</summary>
  public const string FixedWaitId = "ETA0002";

  private static readonly ImmutableArray<DiagnosticDescriptor> Descriptors =
      [ClockBoundRules.Descriptors.ClockBound, ClockBoundRules.Descriptors.FixedWait];

  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors;

  public override void Initialize(AnalysisContext context)
  {
    if (context is null)
    {
      throw new ArgumentNullException(nameof(context));
    }

    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start =>
    {
      if (!ReferencesTestFramework(start.Compilation))
      {
        return;
      }

      ValueTracer tracer = new(start.Compilation);
      start.RegisterSyntaxNodeAction(
          nodeContext => AnalyzeLoop(nodeContext, tracer),
          SyntaxKind.WhileStatement, SyntaxKind.DoStatement, SyntaxKind.ForStatement);
      start.RegisterSyntaxNodeAction(
          nodeContext => AnalyzeInvocation(nodeContext, tracer),
          SyntaxKind.InvocationExpression);
      start.RegisterSyntaxNodeAction(
          nodeContext => AnalyzeCreation(nodeContext, tracer),
          SyntaxKind.ObjectCreationExpression);
      start.RegisterSyntaxNodeAction(
          nodeContext => AnalyzeCreation(nodeContext, tracer),
          SyntaxKind.ImplicitObjectCreationExpression);
    });
  }

  /// <summary>Test code is what the rule governs: an assembly that references the test framework.
  ///     Production code answers to the polling doctrine instead.</summary>
  private static bool ReferencesTestFramework(Compilation compilation)
  {
    return compilation.ReferencedAssemblyNames.Any(
        name => name.Name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
  }

  private static void AnalyzeLoop(SyntaxNodeAnalysisContext context, ValueTracer tracer)
  {
    SyntaxNode loop = context.Node;
    if (ClockBoundRules.DeclaresLatencyContract(loop))
    {
      return;
    }

    ExpressionSyntax? condition = loop switch
    {
      WhileStatementSyntax whileStatement => whileStatement.Condition,
      DoStatementSyntax doStatement => doStatement.Condition,
      ForStatementSyntax forStatement => forStatement.Condition,
      _ => null,
    };

    if (condition is not null)
    {
      ClockBoundRules.ReportIfClockBound(context, tracer, condition);
    }

    // A loop that exits through a guarded break or throw is bounded by that guard, so the
    // guard is where a clock bound hides when the loop header says 'while (true)'.
    foreach (IfStatementSyntax guard in ClockBoundRules.ExitGuards(loop))
    {
      ClockBoundRules.ReportIfClockBound(context, tracer, guard.Condition);
    }
  }

  private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, ValueTracer tracer)
  {
    InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
    if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
        is not IMethodSymbol method
        || ClockBoundRules.DeclaresLatencyContract(invocation)
        || method.ContainingType?.ToDisplayString() is not string type)
    {
      return;
    }

    ClockBoundRules.ReportWaitIfBounded(context, tracer, invocation, method, type);
  }

  /// <summary>A cancellation timeout bounds whatever waits on the token, so its duration is a
  ///     wait bound like any other.</summary>
  private static void AnalyzeCreation(SyntaxNodeAnalysisContext context, ValueTracer tracer)
  {
    ArgumentListSyntax? arguments = context.Node is ObjectCreationExpressionSyntax explicitCreation
        ? explicitCreation.ArgumentList
        : ((ImplicitObjectCreationExpressionSyntax)context.Node).ArgumentList;
    if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol
        is not IMethodSymbol constructor
        || constructor.ContainingType?.ToDisplayString() != "System.Threading.CancellationTokenSource"
        || ClockBoundRules.DeclaresLatencyContract(context.Node)
        || arguments is null
        || arguments.Arguments.Count == 0)
    {
      return;
    }

    ClockBoundRules.ReportCancellationTimeout(context, tracer, arguments.Arguments[0].Expression);
  }
}

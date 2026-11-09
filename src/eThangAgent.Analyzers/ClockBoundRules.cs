using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace eThangAgent.Analyzers;

/// <summary>The shapes a wait takes, and the one question each of them turns on: is the bound the
///     ceiling? Every member answers that question from the semantic model, never from text.</summary>
internal static class ClockBoundRules
{
  private const string LatencyContract = "LatencyContract";

  /// <summary>Types whose duration argument bounds a wait.</summary>
  private static readonly Dictionary<string, string[]> BoundingMethods = new()
  {
    ["System.Threading.Tasks.Task"] = ["WaitAsync", "Wait"],
    ["System.Threading.Thread"] = ["Join"],
    ["System.Diagnostics.Process"] = ["WaitForExit"],
    ["System.Threading.CancellationTokenSource"] = ["CancelAfter"],
  };

  /// <summary>Reports a loop condition, or a loop's exit guard, whose clock comparison is bounded
  ///     by something other than the ceiling.</summary>
  public static void ReportIfClockBound(SyntaxNodeAnalysisContext context, ValueTracer tracer,
      ExpressionSyntax condition)
  {
    if (IsCeilingBound(condition, tracer, context.SemanticModel, context.CancellationToken, out string? bound)
        || bound is null)
    {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(Descriptors.ClockBound, condition.GetLocation(), bound));
  }

  /// <summary>Reports whichever shape of wait <paramref name="invocation"/> is, if any: a
  ///     duration-bounded API, a sleep, or a delay raced inside <c>Task.WhenAny</c>.</summary>
  public static void ReportWaitIfBounded(SyntaxNodeAnalysisContext context, ValueTracer tracer,
      InvocationExpressionSyntax invocation, IMethodSymbol method, string type)
  {
    if (BoundingMethods.TryGetValue(type, out string[]? bounding) && bounding.Contains(method.Name))
    {
      ReportDurationIfNotCeiling(context, tracer, invocation, method);
    }
    else if (type == "System.Threading.Thread" && method.Name == "Sleep")
    {
      ReportFixedWait(context, tracer, invocation, method);
    }
    else if (type == "System.Threading.Tasks.Task" && method.Name == "Delay")
    {
      ReportRacedDelay(context, tracer, invocation, method);
    }
  }

  /// <summary>Reports a wait whose duration argument is not the ceiling.</summary>
  public static void ReportDurationIfNotCeiling(SyntaxNodeAnalysisContext context, ValueTracer tracer,
      InvocationExpressionSyntax invocation, IMethodSymbol method)
  {
    if (DurationArgument(invocation, method) is ExpressionSyntax duration
        && !tracer.IsCeilingBound(duration, context.SemanticModel, context.CancellationToken))
    {
      Report(context, Descriptors.ClockBound, duration);
    }
  }

  /// <summary>Reports a sleep or a delay that IS the wait. A poll interval inside a loop bounded by
  ///     the ceiling schedules a retry and is silent, and so is a delay raced against a bound that
  ///     is itself the ceiling.</summary>
  public static void ReportFixedWait(SyntaxNodeAnalysisContext context, ValueTracer tracer,
      InvocationExpressionSyntax invocation, IMethodSymbol method)
  {
    if (DurationArgument(invocation, method) is not ExpressionSyntax duration
        || IsUnbounded(duration, context.SemanticModel, context.CancellationToken)
        || IsPollInterval(invocation, tracer, context.CancellationToken)
        || IsRacedAgainstCeiling(invocation, tracer, context.CancellationToken)
        || tracer.IsCeilingBound(duration, context.SemanticModel, context.CancellationToken))
    {
      return;
    }

    Report(context, Descriptors.FixedWait, duration);
  }

  /// <summary>Reports a cancellation timeout whose duration is not the ceiling: the token bounds
  ///     whatever waits on it, so its duration is a wait bound like any other.</summary>
  public static void ReportCancellationTimeout(SyntaxNodeAnalysisContext context, ValueTracer tracer,
      ExpressionSyntax duration)
  {
    if (!IsUnbounded(duration, context.SemanticModel, context.CancellationToken)
        && !tracer.IsCeilingBound(duration, context.SemanticModel, context.CancellationToken))
    {
      Report(context, Descriptors.ClockBound, duration);
    }
  }

  /// <summary>Reports a delay raced inside <c>Task.WhenAny</c> whose duration is not the ceiling.
  ///     A delay that is not raced at all is a poll interval, and the loop it paces owns that
  ///     question.</summary>
  public static void ReportRacedDelay(SyntaxNodeAnalysisContext context, ValueTracer tracer,
      InvocationExpressionSyntax delay, IMethodSymbol method)
  {
    if (!IsRaced(delay)
        || IsNegativeArm(delay, context.CancellationToken)
        || IsRacedAgainstCeiling(delay, tracer, context.CancellationToken)
        || DurationArgument(delay, method) is not ExpressionSyntax duration
        || IsUnbounded(duration, context.SemanticModel, context.CancellationToken)
        || tracer.IsCeilingBound(duration, context.SemanticModel, context.CancellationToken))
    {
      return;
    }

    Report(context, Descriptors.FixedWait, duration);
  }

  /// <summary>Whether the delay is one arm of a <c>Task.WhenAny</c>.</summary>
  private static bool IsRaced(InvocationExpressionSyntax delay)
  {
    return delay.Ancestors()
        .OfType<InvocationExpressionSyntax>()
        .Any(IsWhenAny);
  }

  private static bool IsWhenAny(InvocationExpressionSyntax call)
  {
    return call.ArgumentList.Arguments.Count > 1
        && call.Expression is MemberAccessExpressionSyntax member
        && member.Name.Identifier.ValueText == "WhenAny";
  }

  /// <summary>Whether the delay is the NEGATIVE arm of the race: the test asserts that the other
  ///     arm did NOT win, so the delay exists to prove a wait has not settled yet and a short one
  ///     is the assertion rather than a bound on anything.</summary>
  private static bool IsNegativeArm(InvocationExpressionSyntax delay, CancellationToken ct)
  {
    foreach (InvocationExpressionSyntax race in delay.Ancestors().OfType<InvocationExpressionSyntax>())
    {
      if (!IsWhenAny(race))
      {
        continue;
      }

      if (race.FirstAncestorOrSelf<VariableDeclaratorSyntax>() is not { } declarator)
      {
        continue;
      }

      SyntaxNode scope = declarator.FirstAncestorOrSelf<MethodDeclarationSyntax>()
          ?? (SyntaxNode?)declarator.FirstAncestorOrSelf<BlockSyntax>()
          ?? declarator.SyntaxTree.GetRoot(ct);
      string name = declarator.Identifier.ValueText;
      return scope.DescendantNodes()
          .Where(node => node is InvocationExpressionSyntax or BinaryExpressionSyntax)
          .Any(node => IsNegativeCheck(node, name));
    }

    return false;
  }

  /// <summary>Whether a node asserts that the race did NOT resolve to the other arm: an
  ///     <c>Assert.NotSame</c>/<c>NotEqual</c> on the race's result, or a reference comparison
  ///     against the other arm that leads to a failure.</summary>
  private static bool IsNegativeCheck(SyntaxNode node, string name)
  {
    return node switch
    {
      InvocationExpressionSyntax call => IsNotSameAssertion(call, name),
      BinaryExpressionSyntax => false,
      _ => false,
    };
  }

  /// <summary>Whether the call asserts that the race did NOT resolve to the other arm. The
  ///     delay in that shape exists to prove a wait has not settled yet, so a short one is the
  ///     assertion rather than a bound on anything.</summary>
  private static bool IsNotSameAssertion(InvocationExpressionSyntax call, string name)
  {
    return call.Expression is MemberAccessExpressionSyntax member
        && member.Name.Identifier.ValueText is "NotSame" or "NotEqual"
        && call.ArgumentList.Arguments
            .Any(argument => argument.Expression is IdentifierNameSyntax identifier
                && identifier.Identifier.ValueText == name);
  }

  private static void Report(SyntaxNodeAnalysisContext context, DiagnosticDescriptor descriptor,
      ExpressionSyntax duration)
    => context.ReportDiagnostic(Diagnostic.Create(descriptor, duration.GetLocation(), duration.ToString()));

  /// <summary>The duration argument of a wait or a sleep: the first one, whatever its type.</summary>
  public static ExpressionSyntax? DurationArgument(InvocationExpressionSyntax invocation, IMethodSymbol method)
  {
    ArgumentSyntax? named = invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
        argument.NameColon is not null && IsDuration(NamedParameter(method, argument)?.Type));
    return named is not null ? named.Expression : PositionalDuration(invocation, method);
  }

  private static ExpressionSyntax? PositionalDuration(InvocationExpressionSyntax invocation, IMethodSymbol method)
  {
    for (int index = 0; index < invocation.ArgumentList.Arguments.Count && index < method.Parameters.Length; index++)
    {
      if (IsDuration(method.Parameters[index].Type))
      {
        return invocation.ArgumentList.Arguments[index].Expression;
      }
    }

    return null;
  }

  private static IParameterSymbol? NamedParameter(IMethodSymbol method, ArgumentSyntax argument)
  {
    string name = argument.NameColon!.Name.Identifier.ValueText;
    return method.Parameters.FirstOrDefault(parameter => parameter.Name == name);
  }

  private static bool IsDuration(ITypeSymbol? type)
  {
    return type is not null
        && (type.SpecialType == SpecialType.System_Int32 || type.ToDisplayString() == "System.TimeSpan");
  }

  /// <summary>Whether <paramref name="condition"/> compares a clock-derived value against a bound,
  ///     and whether that bound traces to the ceiling. <paramref name="bound"/> names the expression
  ///     that is not the ceiling, so a diagnostic can point at it.</summary>
  public static bool IsCeilingBound(ExpressionSyntax condition, ValueTracer tracer, SemanticModel model,
      CancellationToken ct, out string? bound)
  {
    bound = null;
    foreach (BinaryExpressionSyntax comparison in condition.DescendantNodesAndSelf()
        .OfType<BinaryExpressionSyntax>())
    {
      ExpressionSyntax? operand = ClockBoundOperand(comparison, tracer, model, ct);
      if (operand is null)
      {
        continue;
      }

      if (tracer.IsCeilingBound(operand, model, ct))
      {
        return true;
      }

      bound ??= operand.ToString();
    }

    return false;
  }

  /// <summary>The durations a clock comparison measures against, or null when the comparison is
  ///     not a clock comparison at all.</summary>
  private static ExpressionSyntax? ClockBoundOperand(BinaryExpressionSyntax comparison,
      ValueTracer tracer, SemanticModel model, CancellationToken ct)
  {
    return IsComparison(comparison.Kind())
        ? (tracer.IsClock(comparison.Left, model, ct), tracer.IsClock(comparison.Right, model, ct)) switch
        {
          (true, _) => comparison.Right,
          (_, true) => comparison.Left,
          _ => null,
        }
        : null;
  }

  private static bool IsComparison(SyntaxKind kind)
  {
    return kind is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
        or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
        or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;
  }

  /// <summary>The guards inside a loop that end it: a break, a throw, a return, or a block holding
  ///     one of those.</summary>
  public static IEnumerable<IfStatementSyntax> ExitGuards(SyntaxNode loop)
  {
    return loop.DescendantNodes().OfType<IfStatementSyntax>().Where(guard => guard.Statement
        .DescendantNodesAndSelf().Any(node => node
            is BreakStatementSyntax or ThrowStatementSyntax or ReturnStatementSyntax));
  }

  /// <summary>Whether a sleep is a poll interval: the loop it paces is bounded by the ceiling, so the
  ///     duration schedules the retry instead of bounding the wait. The loop may be the one the
  ///     sleep sits in, or the one every caller of the sleep's helper sits in - a retry helper is
  ///     where the suite's sleeps actually live, and reachability is what makes the rule see them.</summary>
  private static bool IsPollInterval(SyntaxNode node, ValueTracer tracer, CancellationToken ct)
  {
    if (IsInsideCeilingBoundedLoop(node, tracer, ct))
    {
      return true;
    }

    if (node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } helper
        || tracer.ModelOf(helper).GetDeclaredSymbol(helper, ct) is not IMethodSymbol method)
    {
      return false;
    }

    IReadOnlyList<InvocationExpressionSyntax> callSites = tracer.CallSitesOf(method);
    return callSites.Count > 0
        && callSites.All(site => IsInsideCeilingBoundedLoop(site, tracer, ct));
  }

  private static bool IsInsideCeilingBoundedLoop(SyntaxNode node, ValueTracer tracer, CancellationToken ct)
  {
    return node.Ancestors()
        .Where(ancestor => ancestor is WhileStatementSyntax or DoStatementSyntax
            or ForStatementSyntax or ForEachStatementSyntax)
        .Any(loop => IsCeilingBounded(loop, tracer, ct));
  }

  private static bool IsCeilingBounded(SyntaxNode loop, ValueTracer tracer, CancellationToken ct)
  {
    SemanticModel model = tracer.ModelOf(loop);
    ExpressionSyntax? condition = loop switch
    {
      WhileStatementSyntax whileStatement => whileStatement.Condition,
      DoStatementSyntax doStatement => doStatement.Condition,
      ForStatementSyntax forStatement => forStatement.Condition,
      _ => null,
    };

    return (condition is not null && IsCeilingBound(condition, tracer, model, ct, out _))
        || ExitGuards(loop).Any(guard => IsCeilingBound(guard.Condition, tracer, model, ct, out _));
  }

  /// <summary>Whether the delay is one arm of a race whose other arm is bounded by the ceiling:
  ///     the ceiling is then the bound and the delay only decides which arm wins.</summary>
  private static bool IsRacedAgainstCeiling(InvocationExpressionSyntax delay, ValueTracer tracer,
      CancellationToken ct)
  {
    IEnumerable<InvocationExpressionSyntax> races = delay.Ancestors()
        .OfType<InvocationExpressionSyntax>()
        .Where(call => call.ArgumentList.Arguments.Count > 1
            && call.Expression is MemberAccessExpressionSyntax member
            && member.Name.Identifier.ValueText == "WhenAny");
    return races.Any(race => race.ArgumentList.Arguments
        .Where(argument => !argument.Span.Contains(delay.Span))
        .Any(argument => ContainsCeilingBoundDelay(argument.Expression, tracer, ct)));
  }

  private static bool ContainsCeilingBoundDelay(ExpressionSyntax expression, ValueTracer tracer,
      CancellationToken ct)
  {
    return expression.DescendantNodesAndSelf()
        .OfType<InvocationExpressionSyntax>()
        .Where(call => call.Expression is MemberAccessExpressionSyntax member
            && member.Name.Identifier.ValueText == "Delay"
            && call.ArgumentList.Arguments.Count > 0)
        .Any(call => tracer.IsCeilingBound(
            call.ArgumentList.Arguments[0].Expression, tracer.ModelOf(call), ct));
  }

  /// <summary>Whether the duration is unbounded: an infinite wait asserts nothing about latency.</summary>
  private static bool IsUnbounded(ExpressionSyntax duration, SemanticModel model, CancellationToken ct)
  {
    string text = duration.ToString();
    if (text.EndsWith("Infinite", StringComparison.Ordinal)
        || text.EndsWith("InfiniteTimeSpan", StringComparison.Ordinal))
    {
      return true;
    }

    Optional<object?> constant = model.GetConstantValue(duration, ct);
    return constant.HasValue && constant.Value is int value && value == Timeout.Infinite;
  }

  /// <summary>Whether the enclosing type declares the latency contract, which exempts it.</summary>
  public static bool DeclaresLatencyContract(SyntaxNode node)
  {
    return node.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.AttributeLists
        .SelectMany(list => list.Attributes)
        .Where(attribute => attribute.Name.ToString() is "Trait" or "TraitAttribute")
        .Where(attribute => attribute.ArgumentList is not null)
        .Any(attribute => IsLatencyContract(attribute.ArgumentList!)));
  }

  private static bool IsLatencyContract(AttributeArgumentListSyntax arguments)
  {
    string[] values = [.. arguments.Arguments.Select(argument => argument.Expression.ToString().Trim('"'))];
    return values.Contains("Category") && values.Contains(LatencyContract);
  }

  /// <summary>The rule descriptors, shared with the analyzer.</summary>
  public static class Descriptors
  {
    /// <summary>A wait bounded by a clock value that is not the ceiling.</summary>
    public static readonly DiagnosticDescriptor ClockBound = new(
        TimedWaitAnalyzer.ClockBoundId,
        "Wall-clock bound is not the test ceiling",
        "'{0}' bounds this wait and does not come from TestCeiling.Value: a bound tuned to an idle "
        + "machine expires before the work settles under load, so the assertion proves nothing about "
        + "the code. Wait for the fact, bounded by TestCeiling.Value.",
        "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A test asserts that work SETTLES, never that it settles within a duration tuned "
        + "to an idle machine. The one wall-clock bound a test may hold is TestCeiling.Value; a test "
        + "whose subject really is latency declares it with [Trait(\"Category\", \"LatencyContract\")].");

    /// <summary>A fixed-duration wait: the duration IS the bound.</summary>
    public static readonly DiagnosticDescriptor FixedWait = new(
        TimedWaitAnalyzer.FixedWaitId,
        "Fixed-duration wait in test code",
        "'{0}' is the whole bound: a fixed duration is a latency assumption, and under load it expires "
        + "before the work settles. Wait for the fact, bounded by TestCeiling.Value.",
        "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A sleep or delay is a yield inside a loop that waits for a fact, or it is the "
        + "wait itself. When it is the wait, its duration is the bound and must be TestCeiling.Value.");
  }
}

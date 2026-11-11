using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace eThangAgent.Analyzers;

/// <summary>Answers the questions the ceiling rule turns on, through the semantic model rather than
///     through text: does this value come from a wall clock, does it carry
///     <c>eThangAgent.TestSupport.TestCeiling.Value</c>, and does it carry anything that is neither?
///     A local, a field, a property and a parameter's call sites are all followed, so a bound
///     written as <c>Limit</c>, passed as <c>limit</c>, or held in a <c>static readonly</c> field is
///     recognised by what it HOLDS. A deadline built as <c>UtcNow + TestCeiling.Value</c> is
///     therefore a ceiling bound: the clock term is what makes it a deadline, and the ceiling term
///     is what makes it safe.</summary>
internal sealed class ValueTracer
{
  private const string CeilingType = "eThangAgent.TestSupport.TestCeiling";

  private readonly Compilation _compilation;
  private readonly ConcurrentDictionary<ISymbol, Origin> _origins;
  private readonly Lazy<Dictionary<IMethodSymbol, List<InvocationExpressionSyntax>>> _callSites;

  public ValueTracer(Compilation compilation)
  {
    _compilation = compilation;
    _origins = new ConcurrentDictionary<ISymbol, Origin>(SymbolEqualityComparer.Default);
    _callSites = new Lazy<Dictionary<IMethodSymbol, List<InvocationExpressionSyntax>>>(
        IndexCallSites, LazyThreadSafetyMode.ExecutionAndPublication);
  }

  /// <summary>The semantic model of the tree <paramref name="node"/> belongs to.</summary>
  public SemanticModel ModelOf(SyntaxNode node) => _compilation.GetSemanticModel(node.SyntaxTree);

  /// <summary>Every call site of a method, or an empty list when it is never called.</summary>
  public IReadOnlyList<InvocationExpressionSyntax> CallSitesOf(IMethodSymbol method)
    => _callSites.Value.TryGetValue(method.OriginalDefinition, out List<InvocationExpressionSyntax>? sites)
        ? sites
        : [];

  /// <summary>What an expression is made of.</summary>
  private readonly struct Origin(bool clock, bool ceiling, bool foreign)
  {
    /// <summary>Whether a wall clock is read.</summary>
    public bool Clock { get; } = clock;

    /// <summary>Whether <c>TestCeiling.Value</c> is read.</summary>
    public bool Ceiling { get; } = ceiling;

    /// <summary>Whether the value carries a duration that is neither the ceiling nor a clock.</summary>
    public bool Alien { get; } = foreign;

    /// <summary>A value that is a clock read and nothing else.</summary>
    public bool IsPureClock => Clock && !Ceiling && !Alien;

    /// <summary>A value that carries the ceiling and nothing that is not the ceiling or a clock.</summary>
    public bool IsBoundedByCeiling => Ceiling && !Alien;
  }

  /// <summary>Whether the value is read from a wall clock.</summary>
  public bool IsClock(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    => Classify(expression, model, [with(comparer: SymbolEqualityComparer.Default)], ct).IsPureClock;

  /// <summary>Whether the value is bounded by <c>TestCeiling.Value</c>: it carries the ceiling and
  ///     nothing that is neither the ceiling nor a clock read.</summary>
  public bool IsCeilingBound(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    => Classify(expression, model, [with(comparer: SymbolEqualityComparer.Default)], ct).IsBoundedByCeiling;

  private Origin Classify(ExpressionSyntax? expression, SemanticModel model, HashSet<ISymbol> visiting,
      CancellationToken ct)
  {
    ExpressionSyntax stripped = Strip(expression ?? throw new ArgumentNullException(nameof(expression)));
    ISymbol? symbol = model.GetSymbolInfo(stripped, ct).Symbol;
    return Known(symbol) ?? ClassifyShape(stripped, symbol, model, visiting, ct);
  }

  /// <summary>The origin of a symbol that IS a clock read or the ceiling, or null when it is
  ///     neither and has to be traced further.</summary>
  private static Origin? Known(ISymbol? symbol)
  {
    return symbol switch
    {
      _ when IsCeilingValue(symbol) => new Origin(clock: false, ceiling: true, foreign: false),
      _ when IsClockSymbol(symbol) => new Origin(clock: true, ceiling: false, foreign: false),
      _ => null,
    };
  }

  private Origin ClassifyShape(ExpressionSyntax stripped, ISymbol? symbol, SemanticModel model,
      HashSet<ISymbol> visiting, CancellationToken ct)
  {
    return stripped switch
    {
      BinaryExpressionSyntax binary => ClassifyBinary(binary, model, visiting, ct),
      InvocationExpressionSyntax invocation => ClassifyInvocation(invocation, model, visiting, ct),
      MemberAccessExpressionSyntax member => ClassifyMember(member, model, visiting, ct),
      LiteralExpressionSyntax => Foreign(),
      _ => ClassifySymbol(symbol, visiting, ct),
    };
  }

  private static Origin Foreign() => new(clock: false, ceiling: false, foreign: true);

  private static Origin Combine(Origin left, Origin right)
    => new(left.Clock || right.Clock, left.Ceiling || right.Ceiling, left.Alien || right.Alien);

  /// <summary>An addition or a subtraction carries whatever its operands carry:
  ///     <c>UtcNow + TestCeiling.Value</c> is a clock AND a ceiling, and <c>UtcNow + 300</c> is a
  ///     clock and a foreign duration.</summary>
  private Origin ClassifyBinary(BinaryExpressionSyntax binary, SemanticModel model,
      HashSet<ISymbol> visiting, CancellationToken ct)
  {
    return binary.IsKind(SyntaxKind.AddExpression) || binary.IsKind(SyntaxKind.SubtractExpression)
        ? Combine(
            Classify(binary.Left, model, visiting, ct),
            Classify(binary.Right, model, visiting, ct))
        : Foreign();
  }

  /// <summary>A unit conversion or a clock offset is whatever it was built from:
  ///     <c>TimeSpan.FromSeconds(x)</c> is <c>x</c>, and <c>now.AddSeconds(x)</c> is <c>now</c>
  ///     combined with <c>x</c>.</summary>
  private Origin ClassifyInvocation(InvocationExpressionSyntax invocation, SemanticModel model,
      HashSet<ISymbol> visiting, CancellationToken ct)
  {
    if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method || !IsUnitConversion(method))
    {
      return Foreign();
    }

    Origin combined = new(clock: false, ceiling: false, foreign: false);
    if (invocation.Expression is MemberAccessExpressionSyntax member)
    {
      combined = Combine(combined, Classify(member.Expression, model, visiting, ct));
    }

    foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
    {
      combined = Combine(combined, Classify(argument.Expression, model, visiting, ct));
    }

    return combined;
  }

  /// <summary>A member read off a value carries that value's origin: <c>Value.TotalMilliseconds</c>
  ///     is the ceiling in another unit.</summary>
  private Origin ClassifyMember(MemberAccessExpressionSyntax member, SemanticModel model,
      HashSet<ISymbol> visiting, CancellationToken ct)
  {
    Origin receiver = Classify(member.Expression, model, visiting, ct);
    return receiver.Ceiling || receiver.Clock
        ? receiver
        : ClassifySymbol(model.GetSymbolInfo(member, ct).Symbol, visiting, ct);
  }

  private Origin ClassifySymbol(ISymbol? symbol, HashSet<ISymbol> visiting, CancellationToken ct)
  {
    if (symbol is null)
    {
      return Foreign();
    }

    if (_origins.TryGetValue(symbol, out Origin memoised))
    {
      return memoised;
    }

    if (!visiting.Add(symbol))
    {
      return Foreign();
    }

    Origin origin;
    try
    {
      origin = ClassifySymbolCore(symbol, visiting, ct);
    }
    finally
    {
      _ = visiting.Remove(symbol);
    }

    _origins[symbol] = origin;
    return origin;
  }

  private Origin ClassifySymbolCore(ISymbol symbol, HashSet<ISymbol> visiting, CancellationToken ct)
  {
    return Known(symbol) ?? symbol switch
    {
      IParameterSymbol parameter => ClassifyParameter(parameter, visiting, ct),
      IPropertySymbol or IFieldSymbol or ILocalSymbol => ClassifyDeclarations(symbol, visiting, ct),
      _ => Foreign(),
    };
  }

  /// <summary>A property, field or local is whatever it is declared with and assigned: one value
  ///     that is neither the ceiling nor a clock makes the symbol foreign, because the wait may see
  ///     that value.</summary>
  private Origin ClassifyDeclarations(ISymbol symbol, HashSet<ISymbol> visiting, CancellationToken ct)
  {
    Origin combined = new(clock: false, ceiling: false, foreign: false);
    bool sawValue = false;
    foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
    {
      SyntaxNode declaration = reference.GetSyntax(ct);
      SemanticModel model = ModelOf(declaration);
      foreach (ExpressionSyntax value in DeclaredValues(declaration))
      {
        sawValue = true;
        combined = Combine(combined, Classify(value, model, visiting, ct));
      }

      foreach (ExpressionSyntax assigned in AssignedValues(declaration, symbol, model, ct))
      {
        sawValue = true;
        combined = Combine(combined, Classify(assigned, model, visiting, ct));
      }
    }

    return sawValue ? combined : Foreign();
  }

  /// <summary>A parameter holds whatever every call site passes it: one call site passing a
  ///     literal is exactly the violation the rule exists to catch.</summary>
  private Origin ClassifyParameter(IParameterSymbol parameter, HashSet<ISymbol> visiting,
      CancellationToken ct)
  {
    if (parameter.ContainingSymbol is not IMethodSymbol method || parameter.Ordinal < 0)
    {
      return Foreign();
    }

    if (!_callSites.Value.TryGetValue(method.OriginalDefinition, out List<InvocationExpressionSyntax>? sites))
    {
      return Foreign();
    }

    Origin combined = new(clock: false, ceiling: false, foreign: false);
    bool sawSite = false;
    foreach (InvocationExpressionSyntax site in sites)
    {
      if (ArgumentAt(site.ArgumentList, parameter) is ExpressionSyntax value)
      {
        sawSite = true;
        combined = Combine(combined, Classify(value, ModelOf(site), visiting, ct));
      }
    }

    return sawSite ? combined : Foreign();
  }

  private static IEnumerable<ExpressionSyntax> AssignedValues(SyntaxNode declaration, ISymbol symbol,
      SemanticModel model, CancellationToken ct)
  {
    if (declaration is not (VariableDeclaratorSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax))
    {
      return [];
    }

    SyntaxNode scope = declaration.FirstAncestorOrSelf<BlockSyntax>() ?? declaration.SyntaxTree.GetRoot(ct);
    return scope.DescendantNodes()
        .OfType<AssignmentExpressionSyntax>()
        .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, ct).Symbol, symbol))
        .Select(assignment => assignment.Right);
  }

  private static ExpressionSyntax? ArgumentAt(ArgumentListSyntax arguments, IParameterSymbol parameter)
  {
    ArgumentSyntax? named = arguments.Arguments.FirstOrDefault(argument => argument.NameColon is not null
        && argument.NameColon.Name.Identifier.ValueText == parameter.Name);
    return named is not null ? named.Expression : Positional(arguments, parameter);
  }

  private static ExpressionSyntax? Positional(ArgumentListSyntax arguments, IParameterSymbol parameter)
    => parameter.Ordinal < arguments.Arguments.Count
        ? arguments.Arguments[parameter.Ordinal].Expression
        : null;

  private Dictionary<IMethodSymbol, List<InvocationExpressionSyntax>> IndexCallSites()
  {
    Dictionary<IMethodSymbol, List<InvocationExpressionSyntax>> index = [with(comparer: SymbolEqualityComparer.Default)];
    foreach (SyntaxTree tree in _compilation.SyntaxTrees)
    {
      SemanticModel model = _compilation.GetSemanticModel(tree);
      foreach (InvocationExpressionSyntax invocation in tree.GetRoot()
          .DescendantNodes().OfType<InvocationExpressionSyntax>())
      {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
          continue;
        }

        IMethodSymbol key = method.OriginalDefinition;
        if (!index.TryGetValue(key, out List<InvocationExpressionSyntax>? sites))
        {
          sites = [];
          index[key] = sites;
        }

        sites.Add(invocation);
      }
    }

    return index;
  }

  private static IEnumerable<ExpressionSyntax> DeclaredValues(SyntaxNode declaration)
  {
    return declaration switch
    {
      VariableDeclaratorSyntax { Initializer: not null } declarator => [declarator.Initializer.Value],
      PropertyDeclarationSyntax { Initializer: not null } property => [property.Initializer.Value],
      PropertyDeclarationSyntax { ExpressionBody: not null } property => [property.ExpressionBody.Expression],
      PropertyDeclarationSyntax { AccessorList: not null } property => [.. property.AccessorList.Accessors
          .SelectMany(accessor => accessor.DescendantNodes().OfType<ReturnStatementSyntax>())
          .Select(statement => statement.Expression)
          .OfType<ExpressionSyntax>()],
      _ => [],
    };
  }

  private static bool IsCeilingValue(ISymbol? symbol)
  {
    return symbol is IPropertySymbol { Name: "Value" } property
        && property.ContainingType?.ToDisplayString() == CeilingType;
  }

  private static bool IsClockSymbol(ISymbol? symbol)
  {
    return symbol switch
    {
      IPropertySymbol property => IsClockRead(property.GetMethod),
      IFieldSymbol field => IsClockRead(field.AssociatedSymbol as IMethodSymbol),
      _ => false,
    };
  }

  private static bool IsClockRead(IMethodSymbol? method)
  {
    return method?.ContainingType?.ToDisplayString() switch
    {
      "System.DateTime" or "System.DateTimeOffset" => method.Name is "get_UtcNow" or "get_Now",
      "System.Diagnostics.Stopwatch" => method.Name.StartsWith("get_Elapsed", StringComparison.Ordinal),
      "System.Environment" => method.Name is "get_TickCount" or "get_TickCount64",
      _ => false,
    };
  }

  /// <summary>A call that only converts units or offsets a clock: the value it returns is the value
  ///     it was given, in another representation.</summary>
  private static bool IsUnitConversion(IMethodSymbol method)
  {
    return method.ContainingType?.ToDisplayString() switch
    {
      "System.TimeSpan" => method.Name.StartsWith("From", StringComparison.Ordinal),
      "System.DateTime" or "System.DateTimeOffset" =>
          method.Name.StartsWith("Add", StringComparison.Ordinal)
              || method.Name.StartsWith("Subtract", StringComparison.Ordinal),
      _ => false,
    };
  }

  private static ExpressionSyntax Strip(ExpressionSyntax expression)
  {
    ExpressionSyntax current = expression;
    ExpressionSyntax? next = Unwrap(current);
    while (next is not null)
    {
      current = next;
      next = Unwrap(current);
    }

    return current;
  }

  private static ExpressionSyntax? Unwrap(ExpressionSyntax expression)
  {
    return expression switch
    {
      ParenthesizedExpressionSyntax parenthesised => parenthesised.Expression,
      CastExpressionSyntax cast => cast.Expression,
      CheckedExpressionSyntax checkedExpression => checkedExpression.Expression,
      _ => null,
    };
  }
}

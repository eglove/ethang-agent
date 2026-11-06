namespace eThangAgent.ModelDomain;

/// <summary>Static, always-present guidance that a number or a date is obtained by running
///     a computation, never by reasoning about one. Rides every request, root and children
///     alike.</summary>
public sealed class ComputedValuesPromptProvider : ISystemPromptProvider
{
  /// <summary>The guidance text. It forbids mental arithmetic outright, names the
  ///     operations that count as computations, and requires an unrun value to be
  ///     reported as unverified rather than stated.</summary>
  public const string Guidance =
      "Never compute a number or a date by reasoning. Every arithmetic operation, unit " +
      "conversion, date or duration calculation (offsets, day-of-week, timezone shifts), " +
      "percentage, count, and comparison of quantities must be produced by running " +
      "something that actually performs it - an exec program, a tool, or a command - and " +
      "the value you state must be that output, copied. Mental arithmetic is not a " +
      "shortcut, it is a guess wearing the costume of a fact: do not estimate, do not " +
      "round from memory, and do not carry a computed value forward from an earlier turn " +
      "without re-running it. If you cannot run a calculation, say the value is " +
      "unverified - never state it.";

  public string Build() => Guidance;
}

using System.Globalization;

namespace eThangAgent.Composition;

/// <summary>Strict composition-root binding of the main agent's tool-iteration budget.
///     Key "Agent:MaxToolIterations" — optional; absent defaults to 100. Present must be a
///     positive integer; zero, negative, or non-integer values are startup validation
///     errors. Nothing is silently coerced or clamped.</summary>
public static class MaxToolIterationsConfiguration
{
    public const int Default = 100;

    public static int Bind(string? value)
    {
        if (value is null)
            return Default;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            || max < 1)
            throw new InvalidOperationException(
                $"Agent:MaxToolIterations must be a positive integer, got '{value}'.");

        return max;
    }
}

namespace eThangAgent.SharedKernel;

/// <summary>A rule violation carrying a human-readable, field-naming message.</summary>
public sealed record Violation(string Message);

/// <summary>Base type for composable domain rules evaluated before mutations.</summary>
public abstract class Specification<T>
{
    public abstract bool IsSatisfiedBy(T candidate);

    /// <returns>A <see cref="Violation"/> naming the violated field, or null when satisfied.</returns>
    public Violation? ViolationFor(T candidate)
        => IsSatisfiedBy(candidate) ? null : new Violation(FailureMessageFor(candidate));

    protected abstract string FailureMessageFor(T candidate);
}

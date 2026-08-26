namespace eThangAgent.SharedKernel;

/// <summary>An expected domain failure: a stable code plus a human-readable message.
/// Flows through <see cref="Result{T}"/> as data — never thrown for expected failures.</summary>
public sealed record DomainError(string Code, string Message);

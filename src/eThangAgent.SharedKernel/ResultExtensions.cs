namespace eThangAgent.SharedKernel;

/// <summary>Bridges a completed <see cref="Result{T}"/> into the Task-returning seams, so a
///     refusal discovered before any await reads the same as one discovered after it.</summary>
public static class ResultExtensions
{
  /// <summary>The same result, already completed as a task.</summary>
  /// <param name="result">The result to wrap.</param>
  /// <typeparam name="T">The result's value type.</typeparam>
  public static Task<Result<T>> AsTask<T>(this Result<T> result)
  {
    ArgumentNullException.ThrowIfNull(result);
    return Task.FromResult(result);
  }
}

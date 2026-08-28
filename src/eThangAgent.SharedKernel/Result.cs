using System.Diagnostics.CodeAnalysis;

namespace eThangAgent.SharedKernel;

/// <summary>The outcome of an operation that can fail: a value on success, a
/// <see cref="DomainError"/> on failure. Expected failures flow as data — never exceptions.
/// <para>Null-state contract (enforced via MemberNotNullWhen): when <see cref="IsSuccess"/>
/// is true, <see cref="Value"/> is non-null; when false, <see cref="Error"/> is non-null.
/// The one caveat: when T itself is instantiated nullable (Result&lt;string?&gt;), Value may
/// legitimately be null on success — read such Results through <see cref="ValueOrNull"/>
/// and null-check that, because the compiler trusts the contract for <see cref="Value"/>.</para></summary>
public class Result<T>
{
  public T? Value { get; }
  public DomainError? Error { get; }

  [MemberNotNullWhen(true, nameof(Value))]
  [MemberNotNullWhen(false, nameof(Error))]
  public bool IsSuccess { get; }

  /// <summary>Unannotated view of <see cref="Value"/> for nullable-type-argument Results
  /// (Result&lt;T?&gt;): null checks against this member stay live, because the
  /// MemberNotNullWhen claim on <see cref="IsSuccess"/> does not carry over to it.</summary>
  public T? ValueOrNull => Value;

  private Result(T value)
  {
    Value = value;
    IsSuccess = true;
  }

  private Result(DomainError error)
  {
    Value = default;
    Error = error;
    IsSuccess = false;
  }

  /// <summary>Wraps <paramref name="value"/> as a success.</summary>
  internal static Result<T> Success(T value) => new(value);

  /// <summary>Wraps <paramref name="error"/> as a failure.</summary>
  internal static Result<T> Failure(DomainError error) => new(error);

  public TResult Match<TResult>(Func<T, TResult> success, Func<DomainError, TResult> failure)
  {
    ArgumentNullException.ThrowIfNull(success);
    ArgumentNullException.ThrowIfNull(failure);
    return IsSuccess ? success(Value) : failure(Error);
  }

  public Result<TResult> Map<TResult>(Func<T, TResult> f)
  {
    ArgumentNullException.ThrowIfNull(f);
    return IsSuccess ? Result.Success(f(Value)) : Result.Failure<TResult>(Error);
  }

  public Result<TResult> Bind<TResult>(Func<T, Result<TResult>> f)
  {
    ArgumentNullException.ThrowIfNull(f);
    return IsSuccess ? f(Value) : Result.Failure<TResult>(Error);
  }
}

/// <summary>Construction entry point for <see cref="Result{T}"/>: a non-generic facade so the
/// factories do not sit on the generic type itself (CA1000). Call sites read
/// <c>Result.Success(value)</c> / <c>Result.Failure&lt;T&gt;(error)</c>.</summary>
public static class Result
{
  /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
  public static Result<T> Success<T>(T value) => Result<T>.Success(value);

  /// <summary>Creates a failed result carrying <paramref name="error"/>.</summary>
  public static Result<T> Failure<T>(DomainError error) => Result<T>.Failure(error);
}

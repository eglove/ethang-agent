namespace eThangAgent.SharedKernel;

public class Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess { get; }

    private Result(T value)
    {
        Value = value;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        Value = default;
        Error = error;
        IsSuccess = false;
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    public TResult Match<TResult>(Func<T, TResult> success, Func<Error, TResult> failure)
        => IsSuccess ? success(Value!) : failure(Error!);

    public Result<TResult> Map<TResult>(Func<T, TResult> f)
        => IsSuccess ? Result<TResult>.Success(f(Value!)) : Result<TResult>.Failure(Error!);

    public Result<TResult> Bind<TResult>(Func<T, Result<TResult>> f)
        => IsSuccess ? f(Value!) : Result<TResult>.Failure(Error!);
}

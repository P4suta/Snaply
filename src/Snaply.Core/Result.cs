namespace Snaply.Core;

/// <summary>A structured, non-exceptional failure: a stable code plus a human message.</summary>
/// <param name="Code">A stable, machine-readable failure code (see <see cref="ErrorCodes"/>).</param>
/// <param name="Message">A human-readable description of the failure.</param>
/// <param name="Cause">
/// The underlying exception, when this failure was produced by catching one. Preserved so the
/// observability layer can log the type, stack trace, and inner exceptions that would otherwise
/// be lost when only <paramref name="Message"/> is carried. Null for validation-style failures.
/// </param>
public readonly record struct Error(string Code, string Message, Exception? Cause = null)
{
    /// <summary>Renders the error as <c>[Code] Message</c>.</summary>
    /// <returns>The formatted error string.</returns>
    public override string ToString() => $"[{Code}] {Message}";
}

/// <summary>
/// Outcome of an operation that can fail in an expected way (consent declined,
/// capture target vanished, disk write refused). Reserving exceptions for true
/// bugs keeps the control flow of the port implementations explicit and testable.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly record struct Result<T>
{
    private readonly T _value;

    private Result(bool ok, T value, Error error)
    {
        IsSuccess = ok;
        _value = value;
        Error = error;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The success value. Throws if accessed on a failure — check <see cref="IsSuccess"/> first.</summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException($"Result is a failure: {Error}");

    /// <summary>Gets the failure detail; meaningful only when <see cref="IsFailure"/> is <c>true</c>.</summary>
    public Error Error { get; }

    /// <summary>Creates a success result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The success value.</param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Ok(T value) => new(true, value, default);

    /// <summary>Creates a failure result from an existing <see cref="Error"/>.</summary>
    /// <param name="error">The failure detail.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static Result<T> Fail(Error error) => new(false, default!, error);

    /// <summary>Creates a failure result from a code and message.</summary>
    /// <param name="code">A stable, machine-readable failure code.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static Result<T> Fail(string code, string message) => Fail(new Error(code, message));

    /// <summary>Creates a failure result from a code, message, and the exception that caused it.</summary>
    /// <param name="code">A stable, machine-readable failure code.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="cause">The exception that produced this failure (preserved for logging).</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static Result<T> Fail(string code, string message, Exception cause) => Fail(new Error(code, message, cause));

    /// <summary>Transform the success value, propagating failure untouched.</summary>
    /// <typeparam name="TOut">The mapped success type.</typeparam>
    /// <param name="map">The projection applied to the success value.</param>
    /// <returns>A mapped success, or the original failure.</returns>
    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Result<TOut>.Ok(map(_value)) : Result<TOut>.Fail(Error);
}

/// <summary>A <see cref="Result{T}"/> for operations that succeed without a value.</summary>
public readonly record struct Result
{
    private Result(bool ok, Error error)
    {
        IsSuccess = ok;
        Error = error;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Gets the failure detail; meaningful only when <see cref="IsFailure"/> is <c>true</c>.</summary>
    public Error Error { get; }

    /// <summary>Creates a success result.</summary>
    /// <returns>A successful <see cref="Result"/>.</returns>
    public static Result Ok() => new(true, default);

    /// <summary>Creates a failure result from an existing <see cref="Error"/>.</summary>
    /// <param name="error">The failure detail.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Fail(Error error) => new(false, error);

    /// <summary>Creates a failure result from a code and message.</summary>
    /// <param name="code">A stable, machine-readable failure code.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Fail(string code, string message) => Fail(new Error(code, message));

    /// <summary>Creates a failure result from a code, message, and the exception that caused it.</summary>
    /// <param name="code">A stable, machine-readable failure code.</param>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="cause">The exception that produced this failure (preserved for logging).</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Fail(string code, string message, Exception cause) => Fail(new Error(code, message, cause));
}

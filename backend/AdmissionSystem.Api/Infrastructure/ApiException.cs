namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Thrown by handlers to produce a response with the uniform <c>{ error, message }</c> body.</summary>
public class ApiException(int statusCode, string error, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;

    public static ApiException BadRequest(string error, string message) => new(StatusCodes.Status400BadRequest, error, message);
    public static ApiException Forbidden(string error, string message) => new(StatusCodes.Status403Forbidden, error, message);
    public static ApiException NotFound(string error, string message) => new(StatusCodes.Status404NotFound, error, message);
    public static ApiException Conflict(string error, string message) => new(StatusCodes.Status409Conflict, error, message);
}

public sealed record ErrorBody(string Error, string Message);

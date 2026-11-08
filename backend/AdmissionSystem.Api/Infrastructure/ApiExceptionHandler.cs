using Microsoft.AspNetCore.Diagnostics;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Maps every exception to the uniform <c>{ error, message }</c> JSON body.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, body) = exception switch
        {
            ApiException api => (api.StatusCode, new ErrorBody(api.Error, api.Message)),
            // Malformed JSON, wrong field types, missing required body.
            BadHttpRequestException => (StatusCodes.Status400BadRequest,
                new ErrorBody(ErrorCodes.InvalidRequest, "The request is malformed or has the wrong format.")),
            _ => (StatusCodes.Status500InternalServerError,
                new ErrorBody(ErrorCodes.InternalError, "Something went wrong on the server. Please try again.")),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken);
        return true;
    }
}

public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string ValidationFailed = "validation_failed";
    public const string ClassNotFound = "class_not_found";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string InternalError = "internal_error";
}

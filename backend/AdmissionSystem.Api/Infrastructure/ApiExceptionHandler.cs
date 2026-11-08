using Microsoft.AspNetCore.Diagnostics;

namespace AdmissionSystem.Api.Infrastructure;

/// <summary>Maps every exception to the uniform <c>{ error, message }</c> JSON body.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // The client went away (closed the connection, timed out): nobody is listening, and it is not a server fault.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var (status, body) = exception switch
        {
            ApiException api => (api.StatusCode, new ErrorBody(api.Error, api.Message)),
            // Malformed JSON, wrong field types, missing required body.
            BadHttpRequestException => (StatusCodes.Status400BadRequest,
                new ErrorBody(ErrorCodes.InvalidRequest, "The request is malformed or has the wrong format.")),
            // No database connection or lock became free in time: the system is overloaded, not broken.
            _ when PostgresErrors.IsOverload(exception) => (StatusCodes.Status503ServiceUnavailable,
                new ErrorBody(ErrorCodes.ServiceBusy, "The system is very busy right now. Please try again in a few seconds.")),
            _ => (StatusCodes.Status500InternalServerError,
                new ErrorBody(ErrorCodes.InternalError, "Something went wrong on the server. Please try again.")),
        };

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "2";
            logger.LogWarning(exception, "Overloaded for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        if (status == StatusCodes.Status500InternalServerError)
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
    public const string HoldNotFound = "hold_not_found";
    public const string StudentIdRequired = "student_id_required";
    public const string InvalidStudentId = "invalid_student_id";
    public const string AlreadyApplied = "already_applied";
    public const string NoSeatsAvailable = "no_seats_available";
    public const string NotYourHold = "not_your_hold";
    public const string HoldExpired = "hold_expired";
    public const string HoldCancelled = "hold_cancelled";
    public const string HoldConfirmed = "hold_confirmed";
    public const string HoldStateChanged = "hold_state_changed";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string ServiceBusy = "service_busy";
    public const string InternalError = "internal_error";
}

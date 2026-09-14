using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SlotLock.Application.Common;
using SlotLock.Domain.Common;

namespace SlotLock.Api.Errors;

/// <summary>
/// Turns the exceptions the domain and application layers raise into RFC 9457 problem
/// responses.
/// </summary>
/// <remarks>
/// <para>
/// Mapping happens once, here, rather than in try/catch blocks inside controllers. Two
/// endpoints that both hold a seat should not be able to disagree about what a full slot
/// looks like on the wire.
/// </para>
/// <para>
/// Every response carries a stable <c>code</c> extension. Clients branch on that; the
/// human-readable title is free to change without breaking them.
/// </para>
/// <para>
/// Nothing unrecognised is described to the caller. An unmapped exception returns a bare 500
/// with a trace id: the message may name a table, a column, or a connection string, and a
/// stack trace tells an attacker what libraries to look up. The detail goes to the log, where
/// the trace id finds it.
/// </para>
/// </remarks>
public sealed class ProblemDetailsExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ProblemDetailsExceptionHandler> _logger;

    public ProblemDetailsExceptionHandler(ILogger<ProblemDetailsExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var problem = Map(exception, httpContext);

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            // Expected outcomes - a full slot, a replayed key. Logged at Information so they
            // can be counted without being mistaken for faults.
            _logger.LogInformation(
                "{Method} {Path} answered {Status}: {Code}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                problem.Status,
                problem.Extensions.TryGetValue("code", out var code) ? code : "unknown");
        }

        problem.Instance = httpContext.Request.Path;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response
            .WriteAsJsonAsync(problem, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static ProblemDetails Map(Exception exception, HttpContext context) => exception switch
    {
        NotFoundException notFound => Problem(
            StatusCodes.Status404NotFound,
            "Not found",
            notFound.Message,
            "not_found"),

        // 409, not 400. The request was valid and would have succeeded a moment earlier;
        // what changed is the state of the slot, and 409 is the status that says so.
        SlotFullException full => Problem(
            StatusCodes.Status409Conflict,
            "Slot is full",
            full.Message,
            full.Code),

        HoldExpiredException expired => Problem(
            StatusCodes.Status409Conflict,
            "Hold expired",
            expired.Message,
            expired.Code),

        InvalidBookingTransitionException transition => Problem(
            StatusCodes.Status409Conflict,
            "Invalid state transition",
            transition.Message,
            transition.Code),

        // The retry policy already tried and kept losing. Telling the client to try again is
        // honest; looping here would hold their connection open while the server fights.
        ConcurrencyExhaustedException exhausted => WithRetryAfter(
            Problem(
                StatusCodes.Status409Conflict,
                "Too much contention",
                "The slot is being booked by several requests at once. Try again.",
                "concurrency_exhausted"),
            context,
            seconds: 1),

        IdempotencyKeyReuseException reuse => Problem(
            StatusCodes.Status422UnprocessableEntity,
            "Idempotency key reused",
            reuse.Message,
            "idempotency_key_reuse"),

        IdempotentRequestInFlightException inFlight => WithRetryAfter(
            Problem(
                StatusCodes.Status409Conflict,
                "Request in progress",
                inFlight.Message,
                "idempotent_request_in_flight"),
            context,
            seconds: 1),

        // Anything else the domain refused: well-formed, but not allowed right now.
        DomainException domain => Problem(
            StatusCodes.Status422UnprocessableEntity,
            "Request refused",
            domain.Message,
            domain.Code),

        ArgumentException argument => Problem(
            StatusCodes.Status400BadRequest,
            "Invalid request",
            argument.Message,
            "invalid_argument"),

        _ => Problem(
            StatusCodes.Status500InternalServerError,
            "Unexpected error",
            "The request could not be completed. Quote the trace id when reporting this.",
            "internal_error"),
    };

    private static ProblemDetails Problem(int status, string title, string detail, string code) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
        Type = $"https://slotlock.dev/problems/{code}",
        Extensions = { ["code"] = code },
    };

    /// <summary>
    /// Adds a Retry-After header for the statuses where trying again is the right move, so a
    /// well-behaved client backs off on the server's advice instead of guessing.
    /// </summary>
    private static ProblemDetails WithRetryAfter(ProblemDetails problem, HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return problem;
    }
}

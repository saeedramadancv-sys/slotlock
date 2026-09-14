using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SlotLock.Application.Abstractions;

namespace SlotLock.Api.Idempotency;

/// <summary>
/// Honours the <c>Idempotency-Key</c> header on the actions it is applied to.
/// </summary>
/// <remarks>
/// <para>
/// A client that retries after a timeout must not end up with two bookings. The key lets the
/// server recognise the second call as the same request and replay the first answer.
/// </para>
/// <para>
/// <b>The key is optional, and that is deliberate.</b> Rejecting requests without one would
/// break every caller the day it shipped. Sending one is how a client opts into the
/// guarantee; without it they get ordinary at-most-once-per-call behaviour, which is what
/// they have today.
/// </para>
/// <para>
/// <b>The fingerprint is taken from the bound arguments, not the raw stream.</b> Reading the
/// body here would mean buffering it and rewinding for the model binder, and the hash would
/// then be sensitive to whitespace and property order - two byte-different spellings of the
/// same request would be treated as a conflicting reuse.
/// </para>
/// </remarks>
public sealed class IdempotencyFilter : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// Shared because constructing these is expensive: each instance builds and caches its
    /// own reflection metadata for every type it sees, so creating one per request throws
    /// that work away on every call. The instance is immutable once used, and thread-safe.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IIdempotencyStore _store;
    private readonly ILogger<IdempotencyFilter> _logger;

    public IdempotencyFilter(IIdempotencyStore store, ILogger<IdempotencyFilter> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;

        if (!request.Headers.TryGetValue(HeaderName, out var header) ||
            string.IsNullOrWhiteSpace(header.ToString()))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var key = header.ToString().Trim();
        if (key.Length > 200)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Idempotency key too long",
                Detail = "The Idempotency-Key header must be 200 characters or fewer.",
                Extensions = { ["code"] = "idempotency_key_too_long" },
            })
            { StatusCode = StatusCodes.Status400BadRequest };
            return;
        }

        var endpoint = $"{request.Method} {request.Path}";
        var fingerprint = Fingerprint(context.ActionArguments);

        // Throws on reuse with a different body, or while the original is still running.
        // Both are mapped to a problem response by the exception handler.
        var claim = await _store
            .ClaimAsync(key, endpoint, fingerprint, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!claim.IsFirstCaller)
        {
            _logger.LogInformation("Replaying stored response for idempotency key {Key}.", key);

            context.HttpContext.Response.Headers["Idempotency-Replayed"] = "true";
            context.Result = new ContentResult
            {
                StatusCode = claim.StoredStatusCode ?? StatusCodes.Status200OK,
                Content = claim.StoredBody,
                ContentType = "application/json",
            };
            return;
        }

        var executed = await next().ConfigureAwait(false);

        var statusCode = StatusCode(executed);

        // Only successful outcomes are recorded. Storing a failure would freeze it: the
        // client would keep being handed the same 409 even after the condition that caused
        // it cleared, and their retry - the correct move - would become useless.
        if (executed.Exception is not null || statusCode is < 200 or >= 300)
        {
            await _store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await _store
            .CompleteAsync(key, statusCode, SerialiseBody(executed), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static int StatusCode(ActionExecutedContext executed) => executed.Result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult statusResult => statusResult.StatusCode,
        _ => executed.HttpContext.Response.StatusCode,
    };

    private static string? SerialiseBody(ActionExecutedContext executed) => executed.Result switch
    {
        ObjectResult { Value: not null } objectResult =>
            JsonSerializer.Serialize(objectResult.Value, SerializerOptions),
        _ => null,
    };

    /// <summary>
    /// SHA-256 over the bound arguments, so the same key sent with different content is
    /// detectable.
    /// </summary>
    private static string Fingerprint(IDictionary<string, object?> arguments)
    {
        var builder = new StringBuilder();

        // Ordered by name: the binder does not promise a stable order, and an unstable hash
        // would report a legitimate retry as a key conflict.
        foreach (var (name, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            // Action arguments are not all request data. The framework injects a
            // CancellationToken, which is per-request state rather than something the caller
            // sent - and serialising it throws, because it exposes a native handle.
            if (value is CancellationToken)
            {
                continue;
            }

            builder.Append(name).Append('=');
            builder.Append(value is null
                ? "null"
                : JsonSerializer.Serialize(value, SerializerOptions));
            builder.Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }
}

/// <summary>Applies <see cref="IdempotencyFilter"/> to an action.</summary>
public sealed class IdempotentAttribute : ServiceFilterAttribute
{
    public IdempotentAttribute() : base(typeof(IdempotencyFilter))
    {
    }
}

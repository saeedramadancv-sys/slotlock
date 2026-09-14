using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using Serilog;
using SlotLock.Api.Errors;
using SlotLock.Api.Idempotency;
using SlotLock.Infrastructure;
using SlotLock.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Serilog from the start, so failures during startup are logged in the same shape as
// everything else rather than disappearing into console output nobody collects.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddControllers().AddJsonOptions(options =>
{
    // Enums travel as names, not ordinals, in both directions.
    //
    // Without this, "days":["Sunday"] is rejected as unconvertible and a booking's status
    // comes back as 0 or 1. Numbers are worse than unfriendly: they are positional, so
    // inserting a member into an enum silently changes what every stored and transmitted
    // value means, and no client sees an error while it happens.
    //
    // It also keeps one vocabulary end to end - the database stores these as strings too,
    // so a row, a log line and a JSON body all say "Confirmed".
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "SlotLock",
        Version = "v1",
        Description =
            "A booking API built around what happens when two people want the same seat at the "
            + "same moment: optimistic concurrency with bounded retries, holds that expire, "
            + "idempotent writes, and a transactional outbox.",
    });

    var xml = Path.Combine(AppContext.BaseDirectory, "SlotLock.Api.xml");
    if (File.Exists(xml))
    {
        options.IncludeXmlComments(xml);
    }
});

builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddSlotLockInfrastructure(builder.Configuration);
builder.Services.AddScoped<IdempotencyFilter>();

builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddProblemDetails();

// Liveness answers "is the process up"; readiness answers "can it serve traffic". They are
// separate because a pod that cannot reach the database should be taken out of the load
// balancer, not killed and restarted into the same failure.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SlotLockDbContext>("database", tags: ["ready"]);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per-IP rather than global: one noisy client should not consume the budget everyone
    // else is sharing.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "SlotLock API");
}

// Counts and latencies per endpoint, for Prometheus to scrape at /metrics. Booking is a
// domain where the interesting signal is the ratio of 201s to 409s under load, and that is
// not visible in logs alone.
app.UseHttpMetrics();

app.MapControllers();
app.MapMetrics();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();

/// <summary>
/// Named so the integration tests can reference the entry point assembly through
/// <c>WebApplicationFactory&lt;Program&gt;</c>. Top-level statements generate an internal
/// class, which a test project cannot see without this.
/// </summary>
public partial class Program
{
    protected Program()
    {
    }
}

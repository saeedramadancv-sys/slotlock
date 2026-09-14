using FluentValidation;

namespace SlotLock.Api.Contracts;

/// <summary>Create a bookable resource.</summary>
/// <param name="TimeZoneId">
/// IANA id, e.g. "Asia/Amman". Windows ids are accepted too: .NET 8 and later resolve both.
/// </param>
public sealed record CreateResourceRequest(string Name, string TimeZoneId, int DefaultCapacity = 1);

/// <summary>Lay out slots over a date range using the resource's local opening hours.</summary>
public sealed record GenerateSlotsRequest(
    DateOnly FromDate,
    DateOnly ToDate,
    TimeOnly DailyStart,
    TimeOnly DailyEnd,
    int SlotMinutes,
    int? Capacity = null,
    IReadOnlyCollection<DayOfWeek>? Days = null);

/// <summary>Take a seat in a slot.</summary>
public sealed record HoldBookingRequest(Guid SlotId, string CustomerReference);

public sealed class CreateResourceRequestValidator : AbstractValidator<CreateResourceRequest>
{
    public CreateResourceRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(120);
        RuleFor(r => r.TimeZoneId).NotEmpty().MaximumLength(64);
        RuleFor(r => r.DefaultCapacity).GreaterThan(0).LessThanOrEqualTo(100_000);
    }
}

public sealed class GenerateSlotsRequestValidator : AbstractValidator<GenerateSlotsRequest>
{
    public GenerateSlotsRequestValidator()
    {
        RuleFor(r => r.ToDate)
            .GreaterThanOrEqualTo(r => r.FromDate)
            .WithMessage("The end date must not precede the start date.");

        RuleFor(r => r.DailyEnd)
            .GreaterThan(r => r.DailyStart)
            .WithMessage("The closing time must be after the opening time.");

        // The ceiling is not arbitrary: a day divided into one-minute slots is 1,440 rows,
        // and a year of that is half a million. Generation is an admin action, but it should
        // not be a way to fill the table by accident.
        RuleFor(r => r.SlotMinutes)
            .InclusiveBetween(1, 24 * 60)
            .WithMessage("Slot length must be between 1 minute and 24 hours.");

        RuleFor(r => r.Capacity!.Value)
            .GreaterThan(0)
            .When(r => r.Capacity.HasValue);
    }
}

public sealed class HoldBookingRequestValidator : AbstractValidator<HoldBookingRequest>
{
    public HoldBookingRequestValidator()
    {
        RuleFor(r => r.SlotId).NotEmpty();
        RuleFor(r => r.CustomerReference).NotEmpty().MaximumLength(200);
    }
}

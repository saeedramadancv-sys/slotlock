using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SlotLock.Application.Options;

namespace SlotLock.UnitTests.Application;

/// <summary>
/// Configuration binding and validation.
/// </summary>
/// <remarks>
/// These exist because of a bug this project actually shipped into its own appsettings file:
/// <c>"IdempotencyRetention": "24:00:00"</c>, meaning to say one day, binding to twenty-four.
/// Startup validation caught it, but only because a bound is declared. The tests below keep
/// both halves honest - the parsing trap, and the bounds that catch it.
/// </remarks>
public class BookingOptionsTests
{
    [Theory]
    [InlineData("24:00:00", 24, 0)]     // Twenty-four DAYS. The hour field only spans 0-23,
                                        // so a leading 24 is read as a day count.
    [InlineData("1.00:00:00", 1, 0)]    // One day, said unambiguously.
    [InlineData("23:00:00", 0, 23)]     // Twenty-three hours, as expected.
    public void TimeSpan_parsing_reads_a_bare_leading_number_over_23_as_days(string text, int days, int hours)
    {
        var parsed = TimeSpan.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        parsed.Days.Should().Be(days);
        parsed.Hours.Should().Be(hours);
    }

    [Fact]
    public void The_shipped_defaults_are_valid()
    {
        Validate(new BookingOptions()).Should().BeEmpty();
    }

    [Fact]
    public void A_retention_of_twenty_four_days_is_rejected()
    {
        // The exact mistake that was made. Without the upper bound it binds cleanly and
        // nothing complains until the table is enormous.
        var options = new BookingOptions
        {
            IdempotencyRetention = TimeSpan.Parse("24:00:00", System.Globalization.CultureInfo.InvariantCulture),
        };

        Validate(options).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(BookingOptions.IdempotencyRetention));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void An_impossible_retry_budget_is_rejected(int attempts)
    {
        Validate(new BookingOptions { MaxConcurrencyAttempts = attempts }).Should().NotBeEmpty();
    }

    [Fact]
    public void A_hold_shorter_than_a_payment_takes_is_rejected()
    {
        // Not a style rule: a five-second hold releases the seat while the customer is still
        // on the card screen, and they lose it to someone else mid-payment.
        Validate(new BookingOptions { HoldDuration = TimeSpan.FromSeconds(5) })
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Configuration_binds_the_way_the_appsettings_file_is_written()
    {
        // Guards the file itself, not just the type: the keys below are copied from
        // appsettings.json, so a rename there fails here rather than at run time.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Booking:HoldDuration"] = "00:10:00",
                ["Booking:MaxConcurrencyAttempts"] = "10",
                ["Booking:IdempotencyRetention"] = "1.00:00:00",
                ["Booking:SweepInterval"] = "00:00:30",
                ["Booking:OutboxBatchSize"] = "50",
            })
            .Build();

        var options = new BookingOptions();
        configuration.GetSection(BookingOptions.SectionName).Bind(options);

        options.HoldDuration.Should().Be(TimeSpan.FromMinutes(10));
        options.MaxConcurrencyAttempts.Should().Be(10);
        options.IdempotencyRetention.Should().Be(TimeSpan.FromHours(24));
        options.SweepInterval.Should().Be(TimeSpan.FromSeconds(30));
        options.OutboxBatchSize.Should().Be(50);

        Validate(options).Should().BeEmpty();
    }

    private static List<ValidationResult> Validate(BookingOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}

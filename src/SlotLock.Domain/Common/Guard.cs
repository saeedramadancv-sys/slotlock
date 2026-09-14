namespace SlotLock.Domain.Common;

/// <summary>
/// Argument checks that throw <see cref="ArgumentException"/> with the offending
/// parameter name. Entities call these in their constructors so an invalid object
/// can never exist, rather than being validated somewhere later and hoped for.
/// </summary>
public static class Guard
{
    public static string AgainstNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty or whitespace.", paramName);
        }

        return value.Trim();
    }

    public static string AgainstTooLong(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value must be {maxLength} characters or fewer.", paramName);
        }

        return value;
    }

    public static int AgainstNonPositive(int value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be greater than zero.");
        }

        return value;
    }

    /// <summary>
    /// Rejects a window that ends at or before it starts. A zero-length slot would be
    /// bookable and occupy no time, which breaks every availability calculation downstream.
    /// </summary>
    public static void AgainstInvalidWindow(DateTimeOffset start, DateTimeOffset end, string paramName)
    {
        if (end <= start)
        {
            throw new ArgumentException("The window must end after it starts.", paramName);
        }
    }

    public static string AgainstInvalidTimeZone(string timeZoneId, string paramName)
    {
        timeZoneId = AgainstNullOrWhiteSpace(timeZoneId, paramName);

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"'{timeZoneId}' is not a known time zone id.", paramName, ex);
        }

        return timeZoneId;
    }
}

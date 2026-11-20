namespace Workplace.Api.Services;

public static class SiteClock
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    /// <summary>Today's date in South Africa (not the server's UTC date).</summary>
    public static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));
}

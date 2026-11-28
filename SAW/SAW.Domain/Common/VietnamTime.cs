namespace SAW.Domain.Common;

public static class VietnamTime
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    public static DateOnly Today => DateOf(DateTime.UtcNow);
    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Zone);
    public static DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));
    public static DateTime StartOfDayUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Zone);
    // SQL datetime2 timestamps have no Kind; the API/storage contract is UTC.
    public static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    public static DateOnly FilterDay(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateOnly.FromDateTime(value) : DateOf(value);
    public static bool IsCalendarQuery(DateTime value) => value.Kind == DateTimeKind.Unspecified && value.TimeOfDay == TimeSpan.Zero;
    public static DateTime QueryUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? TimeZoneInfo.ConvertTimeToUtc(value, Zone) : AsUtc(value);
}

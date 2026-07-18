namespace TimeTracker.App.Models;

/// <summary>An immutable, app-generated identity for an activity. IDs are never reused.</summary>
public sealed record ActivityDefinition(int Id, string Name, TimeSpan? DailyQuota, TimeSpan? WeeklyQuota, TimeSpan? MonthlyQuota, bool IsActive)
{
    public static ActivityDefinition Unknown { get; } = new(0, "Unknown task", null, null, null, true);
}

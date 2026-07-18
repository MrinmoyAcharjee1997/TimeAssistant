namespace TimeTracker.App.Models;

public sealed record LogValidationResult(
    IReadOnlyList<ActivityDefinition> Activities,
    int NextActivityId,
    IReadOnlyList<LoggedTrackerEvent> ValidEvents,
    IReadOnlyList<LoggedTrackerEvent> RecoverableEvents,
    IReadOnlyList<ActivitySession> Sessions,
    IReadOnlyList<LogIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

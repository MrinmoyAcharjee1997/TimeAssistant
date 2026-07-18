namespace TimeTracker.App.Models;

public sealed record LoggedTrackerEvent(DateTime Timestamp, TrackerEventKind Kind, int? ActivityId, string ActivityName);

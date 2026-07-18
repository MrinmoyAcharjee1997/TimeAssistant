namespace TimeTracker.App.Models;

public enum TrackerEventKind
{
    Start,
    Pause,
    Resume,
    Stop
}

public sealed record TrackingState(int? ActivityId, string? ActivityName, TimeSpan CompletedDuration, DateTime? RunningSince, bool IsPaused)
{
    public static TrackingState Idle { get; } = new(null, null, TimeSpan.Zero, null, false);

    public bool IsActive => ActivityId is not null;

    public TimeSpan GetElapsed(DateTime now) => RunningSince is { } started
        ? CompletedDuration + (now - started)
        : CompletedDuration;
}

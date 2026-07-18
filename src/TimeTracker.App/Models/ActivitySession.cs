namespace TimeTracker.App.Models;

public sealed record ActivitySession(int ActivityId, string ActivityName, DateTime StartedAt, DateTime EndedAt, TimeSpan Duration);

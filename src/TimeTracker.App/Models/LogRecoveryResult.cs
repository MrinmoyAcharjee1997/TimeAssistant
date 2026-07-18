namespace TimeTracker.App.Models;

public sealed record LogRecoveryResult(string BackupPath, int RetainedEventCount, int DiscardedIssueCount);

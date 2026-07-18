namespace TimeTracker.App.Models;

public sealed record LogIssue(int LineNumber, string Message, string LineText);

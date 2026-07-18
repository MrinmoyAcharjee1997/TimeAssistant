namespace TimeTracker.App.Models;

public sealed record StorageStatus(string FilePath, bool FileWasCreated, bool HasExpectedHeader)
{
    public string StateText => FileWasCreated
        ? "Created a new local time log."
        : HasExpectedHeader
            ? "Your local time log is connected."
            : "Your log is connected, but its header was changed manually.";
}

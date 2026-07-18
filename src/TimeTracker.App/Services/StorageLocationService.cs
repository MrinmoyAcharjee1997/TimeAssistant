namespace TimeTracker.App.Services;

/// <summary>
/// Resolves the development data location. A later Settings feature will replace this
/// default with a user-selected location without requiring other services to change.
/// </summary>
public sealed class StorageLocationService
{
    public string GetTimeTrackerFilePath()
    {
        foreach (var directory in WalkParents(AppContext.BaseDirectory))
        {
            var databaseDirectory = Path.Combine(directory, "database");
            if (Directory.Exists(databaseDirectory))
            {
                return Path.Combine(databaseDirectory, "TimeTracker.md");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "database", "TimeTracker.md");
    }

    private static IEnumerable<string> WalkParents(string startDirectory)
    {
        for (var current = new DirectoryInfo(startDirectory); current is not null; current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}

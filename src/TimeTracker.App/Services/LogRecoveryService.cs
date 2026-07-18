using TimeTracker.App.Models;

namespace TimeTracker.App.Services;

public sealed class LogRecoveryService(StorageLocationService storageLocation, LogValidationService validationService, SessionParser sessionParser)
{
    public async Task<LogRecoveryResult?> RepairAsync()
    {
        var result = await validationService.ValidateAsync();
        if (result.IsValid) return null;

        var sourcePath = storageLocation.GetTimeTrackerFilePath();
        var backupDirectory = Path.Combine(Path.GetDirectoryName(sourcePath)!, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"TimeTracker.backup-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.md");
        File.Copy(sourcePath, backupPath, false);

        var (activities, mappedEvents, nextId) = NormalizeActivitiesAndEvents(result.Activities, result.RecoverableEvents, result.NextActivityId);
        var provisional = MarkdownLogService.CreateCanonicalDocument(activities, nextId, mappedEvents, 0);
        var reparsed = sessionParser.Parse(provisional, activities);
        var discardedCount = result.Issues.Count + reparsed.Issues.Count;
        var rebuilt = MarkdownLogService.CreateCanonicalDocument(activities, nextId, reparsed.ValidEvents, discardedCount);
        await File.WriteAllTextAsync(sourcePath, rebuilt);
        return new LogRecoveryResult(backupPath, reparsed.ValidEvents.Count, discardedCount);
    }

    private static (IReadOnlyList<ActivityDefinition> Activities, IReadOnlyList<LoggedTrackerEvent> Events, int NextId) NormalizeActivitiesAndEvents(
        IReadOnlyList<ActivityDefinition> sourceActivities,
        IReadOnlyList<LoggedTrackerEvent> sourceEvents,
        int sourceNextId)
    {
        var activities = sourceActivities.OrderBy(activity => activity.Id).ToList();
        var aliases = new Dictionary<int, int>();
        foreach (var duplicateName in activities.GroupBy(activity => activity.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var canonicalId = duplicateName.Min(activity => activity.Id);
            foreach (var duplicate in duplicateName.Where(activity => activity.Id != canonicalId)) aliases[duplicate.Id] = canonicalId;
        }
        activities = activities.Where(activity => !aliases.ContainsKey(activity.Id)).ToList();

        var nextId = Math.Max(sourceNextId, activities.Select(activity => activity.Id).DefaultIfEmpty(0).Max() + 1);
        ActivityDefinition ResolveByName(string name)
        {
            var existing = activities.FirstOrDefault(activity => string.Equals(activity.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            var created = new ActivityDefinition(nextId++, name, null, null, null, true);
            activities.Add(created);
            return created;
        }

        var normalizedEvents = new List<LoggedTrackerEvent>();
        foreach (var entry in sourceEvents)
        {
            if (entry.ActivityId == 0 || entry.ActivityName.Equals("Unknown task", StringComparison.OrdinalIgnoreCase))
            {
                normalizedEvents.Add(entry with { ActivityId = 0, ActivityName = "Unknown task" });
                continue;
            }

            ActivityDefinition target;
            if (entry.ActivityId is { } originalId && aliases.TryGetValue(originalId, out var canonicalId))
            {
                target = activities.Single(activity => activity.Id == canonicalId);
            }
            else if (entry.ActivityId is { } knownId && activities.FirstOrDefault(activity => activity.Id == knownId) is { } knownActivity
                     && string.Equals(knownActivity.Name, entry.ActivityName, StringComparison.OrdinalIgnoreCase))
            {
                target = knownActivity;
            }
            else
            {
                // A missing/conflicting ID treats the visible name as the user's intent and assigns a non-competing ID.
                target = ResolveByName(entry.ActivityName);
            }
            normalizedEvents.Add(entry with { ActivityId = target.Id, ActivityName = target.Name });
        }
        return (activities.OrderBy(activity => activity.Id).ToList(), normalizedEvents, nextId);
    }
}

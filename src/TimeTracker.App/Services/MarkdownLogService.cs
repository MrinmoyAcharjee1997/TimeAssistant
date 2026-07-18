using System.Text;
using System.Text.RegularExpressions;
using TimeTracker.App.Models;

namespace TimeTracker.App.Services;

/// <summary>Owns the single human-readable source-of-truth file. No database or sidecar state is created.</summary>
public sealed class MarkdownLogService(StorageLocationService storageLocation)
{
    private const string RequiredHeader = "# Time Tracker";
    private static readonly Regex DateHeader = new("^## (?<date>\\d{4}-\\d{2}-\\d{2})$", RegexOptions.Compiled);
    private static readonly Regex V2TrackerEvent = new("^- (?<time>\\d{2}:\\d{2}(?::\\d{2})?) \\| (?<kind>START|PAUSE|RESUME|STOP) \\| (?<id>\\d+) \\| (?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex V1TrackerEvent = new("^- (?<time>\\d{2}:\\d{2}(?::\\d{2})?) \\| (?<kind>START|PAUSE|RESUME|STOP) \\| (?<name>.+)$", RegexOptions.Compiled);

    public async Task<StorageStatus> EnsureAndInspectAsync()
    {
        var path = storageLocation.GetTimeTrackerFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var created = !File.Exists(path);
        if (created)
        {
            await File.WriteAllTextAsync(path, CreateStarterDocument(), Encoding.UTF8);
        }

        var firstLine = File.ReadLines(path).FirstOrDefault()?.Trim();
        return new StorageStatus(path, created, string.Equals(firstLine, RequiredHeader, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<ActivityDefinition>> GetActivitiesAsync()
    {
        return (await GetActivityCatalogAsync()).Where(activity => activity.IsActive).ToList();
    }

    public async Task<IReadOnlyList<ActivityDefinition>> GetActivityCatalogAsync()
    {
        var path = storageLocation.GetTimeTrackerFilePath();
        var text = await File.ReadAllTextAsync(path);
        var activities = ParseActivities(text);
        return activities.Count > 0 ? activities : DefaultActivities;
    }

    public async Task<string> ReadDocumentAsync() => await File.ReadAllTextAsync(storageLocation.GetTimeTrackerFilePath());

    public async Task AppendEventAsync(TrackerEventKind kind, ActivityDefinition activity, DateTime timestamp)
    {
        var path = storageLocation.GetTimeTrackerFilePath();
        var dateHeading = $"## {timestamp:yyyy-MM-dd}";
        var existingText = await File.ReadAllTextAsync(path);
        var dateSection = existingText.Replace("\r", string.Empty).Split('\n').Any(line => string.Equals(line.Trim(), dateHeading, StringComparison.Ordinal));
        var prefix = dateSection
            ? string.Empty
            : $"{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}{dateHeading}{Environment.NewLine}{Environment.NewLine}### Activity log{Environment.NewLine}";
        var entry = $"- {timestamp:HH:mm:ss} | {kind.ToString().ToUpperInvariant()} | {activity.Id} | {activity.Name}{Environment.NewLine}";
        await File.AppendAllTextAsync(path, prefix + entry, Encoding.UTF8);
    }

    public async Task<TrackingState> GetLatestTrackingStateAsync()
    {
        var path = storageLocation.GetTimeTrackerFilePath();
        var text = await File.ReadAllTextAsync(path);
        DateTime? currentDate = null;
        var state = TrackingState.Idle;

        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var dateMatch = DateHeader.Match(rawLine.Trim());
            if (dateMatch.Success)
            {
                currentDate = DateTime.ParseExact(dateMatch.Groups["date"].Value, "yyyy-MM-dd", null);
                continue;
            }

            if (currentDate is null) continue;
            var eventMatch = V2TrackerEvent.Match(rawLine.Trim());
            var isV2 = eventMatch.Success;
            if (!isV2) eventMatch = V1TrackerEvent.Match(rawLine.Trim());
            if (!eventMatch.Success || !TryParseEventTime(eventMatch.Groups["time"].Value, out var time)) continue;
            var timestamp = currentDate.Value.Add(time.ToTimeSpan());
            var activityId = isV2 ? int.Parse(eventMatch.Groups["id"].Value) : (int?)null;
            var activityName = eventMatch.Groups["name"].Value.Trim();
            var kind = eventMatch.Groups["kind"].Value;

            state = ApplyEvent(state, kind, activityId, activityName, timestamp);
        }

        return state;
    }

    private static TrackingState ApplyEvent(TrackingState state, string kind, int? activityId, string activityName, DateTime timestamp) => kind switch
    {
        "START" => new TrackingState(activityId, activityName, TimeSpan.Zero, timestamp, false),
        "PAUSE" when IsSameActivity(state, activityId, activityName) && state.RunningSince is { } started
            => state with { CompletedDuration = state.CompletedDuration + (timestamp - started), RunningSince = null, IsPaused = true },
        "RESUME" when IsSameActivity(state, activityId, activityName) && state.IsPaused
            => state with { RunningSince = timestamp, IsPaused = false },
        "STOP" => TrackingState.Idle,
        _ => state
    };

    private static bool IsSameActivity(TrackingState state, int? activityId, string activityName) => state.IsActive
        && (activityId is not null ? state.ActivityId == activityId : string.Equals(state.ActivityName, activityName, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseEventTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out time)
        || TimeOnly.TryParseExact(value, "HH:mm", null, System.Globalization.DateTimeStyles.None, out time);

    private static List<ActivityDefinition> ParseActivities(string text)
    {
        var result = new List<ActivityDefinition>();
        var inActivities = false;
        foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
        {
            if (line.Trim().Equals("## Activities", StringComparison.OrdinalIgnoreCase))
            {
                inActivities = true;
                continue;
            }
            if (inActivities && line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!inActivities || !line.TrimStart().StartsWith('|')) continue;

            var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 6 || !int.TryParse(cells[0], out var id)) continue;
            if (cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase) || cells[0].All(character => character is '-' or ':')) continue;
            result.Add(new ActivityDefinition(id, cells[1], ParseQuota(cells[2]), ParseQuota(cells[3]), ParseQuota(cells[4]), cells[5].Equals("Active", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static TimeSpan? ParseQuota(string value) => value.EndsWith('h') && double.TryParse(value[..^1], out var hours)
        ? TimeSpan.FromHours(hours) : null;

    private static IReadOnlyList<ActivityDefinition> DefaultActivities =>
    [
        new(1, "xAI", null, TimeSpan.FromHours(20), null, true),
        new(2, "Craqit", null, null, null, true),
        new(3, "Streaming", null, null, null, true),
        new(4, "Gaming", null, null, null, true),
        new(5, "Personal", null, null, null, true),
        new(6, "Rest", null, null, null, true)
    ];

    public static string CreateCanonicalDocument(IReadOnlyList<ActivityDefinition> activities, int nextActivityId, IReadOnlyList<LoggedTrackerEvent> events, int discardedIssueCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Time Tracker");
        builder.AppendLine("<!-- format: 2 -->");
        builder.AppendLine();
        builder.AppendLine("## Metadata");
        builder.AppendLine();
        builder.AppendLine($"- Next activity ID: {nextActivityId}");
        builder.AppendLine("- Storage mode: project folder (development default)");
        builder.AppendLine();
        builder.AppendLine("## Activities");
        builder.AppendLine();
        builder.AppendLine("| ID | Name | Daily quota | Weekly quota | Monthly quota | Status |");
        builder.AppendLine("|---:|---|---:|---:|---:|---|");
        foreach (var activity in activities)
        {
            builder.AppendLine($"| {activity.Id} | {activity.Name} | {FormatQuota(activity.DailyQuota)} | {FormatQuota(activity.WeeklyQuota)} | {FormatQuota(activity.MonthlyQuota)} | {(activity.IsActive ? "Active" : "Archived")} |");
        }
        if (discardedIssueCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Recovery note");
            builder.AppendLine();
            builder.AppendLine($"> {discardedIssueCount} invalid tracking event(s) were excluded during repair. The untouched original is in backups/.");
        }
        foreach (var day in events.GroupBy(entry => entry.Timestamp.Date).OrderBy(group => group.Key))
        {
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine();
            builder.AppendLine($"## {day.Key:yyyy-MM-dd}");
            builder.AppendLine();
            builder.AppendLine("### Activity log");
            foreach (var entry in day)
                builder.AppendLine($"- {entry.Timestamp:HH:mm:ss} | {entry.Kind.ToString().ToUpperInvariant()} | {entry.ActivityId} | {entry.ActivityName}");
        }
        return builder.ToString();
    }

    private static string FormatQuota(TimeSpan? quota) => quota is { } value ? $"{value.TotalHours:0.##}h" : string.Empty;

    private static string CreateStarterDocument() => """
        # Time Tracker
        <!-- format: 1 -->

        ## Configuration

        - Weekly goal: xAI — 20h
        - Active categories: Craqit, xAI, Streaming, Gaming, Personal, Rest
        - Storage mode: project folder (development default)

        ---

        <!-- New dated activity, check-in, and decision entries are appended below this line. -->
        """;
}

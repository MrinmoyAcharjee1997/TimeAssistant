using System.Globalization;
using System.Text.RegularExpressions;
using TimeTracker.App.Models;

namespace TimeTracker.App.Services;

/// <summary>Validates app-managed tracking lines and derives completed sessions only from valid V2 events.</summary>
public sealed class SessionParser
{
    private static readonly Regex DateHeader = new("^## (?<date>\\d{4}-\\d{2}-\\d{2})$", RegexOptions.Compiled);
    private static readonly Regex V2Event = new("^- (?<time>\\d{2}:\\d{2}(?::\\d{2})?) \\| (?<kind>START|PAUSE|RESUME|STOP) \\| (?<id>\\d+) \\| (?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex V1Event = new("^- (?<time>\\d{2}:\\d{2}(?::\\d{2})?) \\| (?<kind>START|PAUSE|RESUME|STOP) \\| (?<name>[^|]+)$", RegexOptions.Compiled);

    public SessionParseResult Parse(string document, IReadOnlyCollection<ActivityDefinition> activities)
    {
        var activityIds = activities.Select(activity => activity.Id).ToHashSet();
        var activityNames = activities.ToDictionary(activity => activity.Id, activity => activity.Name);
        var validEvents = new List<LoggedTrackerEvent>();
        var recoverableEvents = new List<LoggedTrackerEvent>();
        var sessions = new List<ActivitySession>();
        var issues = new List<LogIssue>();
        DateTime? date = null;
        var inActivityLog = false;
        PendingSession? pending = null;

        var lines = document.Replace("\r", string.Empty).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            var dateMatch = DateHeader.Match(trimmed);
            if (dateMatch.Success)
            {
                date = DateTime.ParseExact(dateMatch.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                inActivityLog = false;
                continue;
            }
            if (trimmed.Equals("### Activity log", StringComparison.OrdinalIgnoreCase)) { inActivityLog = true; continue; }
            if (trimmed.StartsWith("### ", StringComparison.Ordinal)) { inActivityLog = false; continue; }
            if (!inActivityLog || !trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;

            var match = V2Event.Match(trimmed);
            var isV2 = match.Success;
            if (!isV2) match = V1Event.Match(trimmed);
            if (date is null || !match.Success || !TryParseEventTime(match.Groups["time"].Value, out var time))
            {
                issues.Add(new LogIssue(index + 1, "This is not a valid tracking event.", raw));
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            var kind = Enum.Parse<TrackerEventKind>(match.Groups["kind"].Value, true);
            var timestamp = date.Value.Add(time.ToTimeSpan());
            var id = isV2 ? int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture) : (int?)null;
            var entry = new LoggedTrackerEvent(timestamp, kind, id, name);
            recoverableEvents.Add(entry);

            if (!isV2)
            {
                issues.Add(new LogIssue(index + 1, "Legacy V1 tracking events require migration to the V2 numeric-ID format.", raw));
                continue;
            }
            if (id != 0 && !activityIds.Contains(id!.Value))
            {
                issues.Add(new LogIssue(index + 1, $"Activity ID {id} does not exist in the activity catalog.", raw));
                continue;
            }
            if (id != 0 && !string.Equals(activityNames[id!.Value], name, StringComparison.Ordinal))
            {
                issues.Add(new LogIssue(index + 1, "The event activity name conflicts with its activity ID in the catalog.", raw));
                continue;
            }
            if (!TryApply(entry, ref pending, sessions, out var reason))
            {
                issues.Add(new LogIssue(index + 1, reason!, raw));
                continue;
            }
            validEvents.Add(entry);
        }
        return new SessionParseResult(validEvents, recoverableEvents, sessions, issues);
    }

    private static bool TryApply(LoggedTrackerEvent entry, ref PendingSession? pending, ICollection<ActivitySession> sessions, out string? reason)
    {
        reason = null;
        if (entry.Kind == TrackerEventKind.Start)
        {
            if (pending is not null) { reason = "A START event was found while another activity is already open."; return false; }
            pending = new PendingSession(entry.ActivityId!.Value, entry.ActivityName, entry.Timestamp, entry.Timestamp, TimeSpan.Zero, false);
            return true;
        }
        if (pending is null) { reason = $"A {entry.Kind.ToString().ToUpperInvariant()} event has no matching START event."; return false; }
        if (pending.ActivityId != entry.ActivityId!.Value || entry.Timestamp < pending.LastTimestamp)
        {
            reason = "This event does not match the currently open activity or occurs before the prior event.";
            return false;
        }
        switch (entry.Kind)
        {
            case TrackerEventKind.Pause when !pending.IsPaused:
                pending = pending with { Accumulated = pending.Accumulated + (entry.Timestamp - pending.RunningSince), LastTimestamp = entry.Timestamp, IsPaused = true };
                return true;
            case TrackerEventKind.Resume when pending.IsPaused:
                pending = pending with { RunningSince = entry.Timestamp, LastTimestamp = entry.Timestamp, IsPaused = false };
                return true;
            case TrackerEventKind.Stop:
                var duration = pending.IsPaused ? pending.Accumulated : pending.Accumulated + (entry.Timestamp - pending.RunningSince);
                sessions.Add(new ActivitySession(pending.ActivityId, pending.ActivityName, pending.StartedAt, entry.Timestamp, duration));
                pending = null;
                return true;
            default:
                reason = $"A {entry.Kind.ToString().ToUpperInvariant()} event is not valid in the current timer state.";
                return false;
        }
    }

    private static bool TryParseEventTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
        || TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private sealed record PendingSession(int ActivityId, string ActivityName, DateTime StartedAt, DateTime RunningSince, TimeSpan Accumulated, bool IsPaused)
    {
        public DateTime LastTimestamp { get; init; } = RunningSince;
    }
}

public sealed record SessionParseResult(IReadOnlyList<LoggedTrackerEvent> ValidEvents, IReadOnlyList<LoggedTrackerEvent> RecoverableEvents, IReadOnlyList<ActivitySession> Sessions, IReadOnlyList<LogIssue> Issues);

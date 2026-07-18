using System.Text.RegularExpressions;
using TimeTracker.App.Models;

namespace TimeTracker.App.Services;

public sealed class LogValidationService(MarkdownLogService logService, SessionParser sessionParser)
{
    private static readonly Regex NextId = new("^- Next activity ID: (?<id>\\d+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task<LogValidationResult> ValidateAsync()
    {
        await logService.EnsureAndInspectAsync();
        var document = await logService.ReadDocumentAsync();
        var activities = await logService.GetActivityCatalogAsync();
        var parsed = sessionParser.Parse(document, activities);
        var issues = parsed.Issues.ToList();
        var nextIdMatch = NextId.Match(document);
        var nextId = nextIdMatch.Success ? int.Parse(nextIdMatch.Groups["id"].Value) : activities.DefaultIfEmpty().Max(activity => activity?.Id ?? 0) + 1;

        if (nextId <= activities.Select(activity => activity.Id).DefaultIfEmpty(0).Max())
            issues.Add(new LogIssue(0, "Next activity ID must be greater than every existing activity ID.", ""));

        return new LogValidationResult(activities, nextId, parsed.ValidEvents, parsed.RecoverableEvents, parsed.Sessions, issues);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TimeTracker.App.Models;
using TimeTracker.App.Services;

namespace TimeTracker.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly MarkdownLogService _logService = new(new StorageLocationService());
    private readonly LogValidationService _validationService;
    private readonly LogRecoveryService _recoveryService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _ticker;
    private TrackingState _trackingState = TrackingState.Idle;

    public ObservableCollection<ActivityDefinition> Activities { get; } = [];

    [ObservableProperty]
    public partial string StoragePath { get; set; } = "Connecting to local data…";

    [ObservableProperty]
    public partial string StorageState { get; set; } = "Preparing your single Markdown log.";

    [ObservableProperty]
    public partial string StorageDetail { get; set; } = "No cloud account or background service is involved.";

    [ObservableProperty]
    public partial ActivityDefinition? SelectedActivity { get; set; }

    [ObservableProperty]
    public partial string CurrentActivity { get; set; } = "Nothing is being tracked";

    [ObservableProperty]
    public partial string Elapsed { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial string TrackingStatus { get; set; } = "Ready when you are.";

    [ObservableProperty]
    public partial bool IsTracking { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool HasLogIssues { get; set; }

    [ObservableProperty]
    public partial string LogIssueMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsValidating { get; set; }

    public DashboardViewModel()
    {
        var storageLocation = new StorageLocationService();
        _validationService = new LogValidationService(new MarkdownLogService(storageLocation), new SessionParser());
        _recoveryService = new LogRecoveryService(storageLocation, _validationService, new SessionParser());
        _ticker = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _ticker.Interval = TimeSpan.FromSeconds(1);
        _ticker.Tick += (_, _) => RefreshTimerDisplay();
    }

    public async Task InitializeAsync()
    {
        IsValidating = true;
        try
        {
        var status = await _logService.EnsureAndInspectAsync();
        StoragePath = status.FilePath;
        StorageState = status.StateText;
        StorageDetail = status.HasExpectedHeader
            ? "This file remains the source of truth for every future session, check-in, and decision."
            : "The app preserves your edits. We will add recovery guidance when session parsing is built.";

        Activities.Clear();
        foreach (var activity in await _logService.GetActivitiesAsync()) Activities.Add(activity);
        _trackingState = await _logService.GetLatestTrackingStateAsync();
        ApplyTrackingState("Restored the latest state from your Markdown log.");
        var validation = await _validationService.ValidateAsync();
        HasLogIssues = !validation.IsValid;
        LogIssueMessage = HasLogIssues
            ? $"{validation.Issues.Count} entry or setting issue(s) need review. Repair creates a backup and preserves valid data."
            : "";
        }
        finally
        {
            IsValidating = false;
        }
    }

    [RelayCommand]
    private async Task RepairLogAsync()
    {
        var result = await _recoveryService.RepairAsync();
        if (result is null) return;
        HasLogIssues = false;
        LogIssueMessage = "";
        TrackingStatus = $"Log repaired. Backup saved as {Path.GetFileName(result.BackupPath)}.";
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task StartOrSwitchAsync()
    {
        var activityToTrack = SelectedActivity ?? ActivityDefinition.Unknown;
        var now = DateTime.Now;

        if (_trackingState.IsActive && _trackingState.ActivityId != activityToTrack.Id)
        {
            await _logService.AppendEventAsync(TrackerEventKind.Stop, new ActivityDefinition(_trackingState.ActivityId!.Value, _trackingState.ActivityName!, null, null, null, true), now);
            _trackingState = TrackingState.Idle;
        }

        if (!_trackingState.IsActive)
        {
            await _logService.AppendEventAsync(TrackerEventKind.Start, activityToTrack, now);
            _trackingState = new TrackingState(activityToTrack.Id, activityToTrack.Name, TimeSpan.Zero, now, false);
            ApplyTrackingState($"Started {activityToTrack.Name} and recorded it in TimeTracker.md.");
        }
        else if (_trackingState.IsPaused)
        {
            await _logService.AppendEventAsync(TrackerEventKind.Resume, new ActivityDefinition(_trackingState.ActivityId!.Value, _trackingState.ActivityName!, null, null, null, true), now);
            _trackingState = _trackingState with { RunningSince = now, IsPaused = false };
            ApplyTrackingState($"Resumed {_trackingState.ActivityName}.");
        }
        else
        {
            TrackingStatus = $"Already tracking {_trackingState.ActivityName}. Choose another activity to switch.";
        }
    }

    [RelayCommand]
    private async Task PauseOrResumeAsync()
    {
        if (!_trackingState.IsActive) return;
        var now = DateTime.Now;
        if (_trackingState.IsPaused)
        {
            await _logService.AppendEventAsync(TrackerEventKind.Resume, new ActivityDefinition(_trackingState.ActivityId!.Value, _trackingState.ActivityName!, null, null, null, true), now);
            _trackingState = _trackingState with { RunningSince = now, IsPaused = false };
            ApplyTrackingState($"Resumed {_trackingState.ActivityName}.");
            return;
        }

        await _logService.AppendEventAsync(TrackerEventKind.Pause, new ActivityDefinition(_trackingState.ActivityId!.Value, _trackingState.ActivityName!, null, null, null, true), now);
        _trackingState = _trackingState with
        {
            CompletedDuration = _trackingState.GetElapsed(now),
            RunningSince = null,
            IsPaused = true
        };
        ApplyTrackingState($"Paused {_trackingState.ActivityName}.");
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!_trackingState.IsActive) return;
        var now = DateTime.Now;
        var stoppedActivity = new ActivityDefinition(_trackingState.ActivityId!.Value, _trackingState.ActivityName!, null, null, null, true);
        await _logService.AppendEventAsync(TrackerEventKind.Stop, stoppedActivity, now);
        _trackingState = TrackingState.Idle;
        ApplyTrackingState($"Stopped {stoppedActivity.Name}. The completed session is in TimeTracker.md.");
    }

    private void ApplyTrackingState(string status)
    {
        IsTracking = _trackingState.IsActive;
        IsPaused = _trackingState.IsPaused;
        CurrentActivity = _trackingState.ActivityName ?? "Nothing is being tracked";
        TrackingStatus = status;
        RefreshTimerDisplay();
        if (_trackingState.IsActive && !_trackingState.IsPaused) _ticker.Start(); else _ticker.Stop();
    }

    private void RefreshTimerDisplay()
    {
        Elapsed = _trackingState.IsActive ? _trackingState.GetElapsed(DateTime.Now).ToString(@"hh\:mm\:ss") : "00:00:00";
    }
}

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPAStudio.App.Services;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.App.ViewModels;

public sealed partial class StatisticsViewModel : ObservableObject, IPageAware
{
    private readonly StatisticsService _stats;
    private INavigator? _navigator;

    [ObservableProperty]
    private StatisticsPeriod _selectedPeriod = StatisticsPeriod.Month;

    [ObservableProperty]
    private string _periodTitle = "";

    [ObservableProperty]
    private string _periodSubtitle = "";

    [ObservableProperty]
    private int _totalInstalls;

    [ObservableProperty]
    private int _todayInstalls;

    [ObservableProperty]
    private int _uniqueDevices;

    [ObservableProperty]
    private int _totalIpaDownloads;

    [ObservableProperty]
    private string _ipaVolumeText = "0 GB";

    [ObservableProperty]
    private int _totalFirmwareDownloads;

    [ObservableProperty]
    private string _firmwareVolumeText = "0 GB";

    [ObservableProperty]
    private int _totalConnections;

    [ObservableProperty]
    private ObservableCollection<TopItemStat> _topApps = new();

    [ObservableProperty]
    private ObservableCollection<TopItemStat> _topDevices = new();

    [ObservableProperty]
    private ObservableCollection<MethodBreakdownStat> _methods = new();

    [ObservableProperty]
    private ObservableCollection<TimelinePointStat> _timeline = new();

    [ObservableProperty]
    private ObservableCollection<RecentActivityStat> _recentActivity = new();

    public StatisticsViewModel(StatisticsService stats)
    {
        _stats = stats;
        _stats.StatisticsChanged += (_, _) => RefreshData();
        RefreshData();
    }

    public void OnNavigatedTo(INavigator navigator)
    {
        _navigator = navigator;
        RefreshData();
    }

    [RelayCommand]
    private void GoBack() => _navigator?.GoBack();

    [RelayCommand]
    private void GoHome() => _navigator?.GoHome();

    [RelayCommand]
    private void SetPeriod(string periodStr)
    {
        if (Enum.TryParse<StatisticsPeriod>(periodStr, true, out var period))
        {
            SelectedPeriod = period;
            RefreshData();
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshData();
    }

    [RelayCommand]
    private void ClearStats()
    {
        var confirmed = ModernDialog.Confirm(
            Loc.Get("L.Stats.ClearConfirmText"),
            Loc.Get("L.Stats.ClearConfirmTitle"),
            ModernDialogType.Danger,
            confirmText: Loc.Get("L.Stats.Clear"),
            cancelText: Loc.Get("L.Cancel"),
            isDestructive: true);

        if (confirmed)
        {
            _stats.ClearAll();
            RefreshData();
        }
    }

    public void RefreshData()
    {
        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            var summary = _stats.GetSummary(SelectedPeriod);

            TotalInstalls = summary.TotalInstalls;
            TodayInstalls = summary.TodayInstalls;
            UniqueDevices = summary.UniqueDevices;
            TotalIpaDownloads = summary.TotalIpaDownloads;
            IpaVolumeText = summary.IpaVolumeText;
            TotalFirmwareDownloads = summary.TotalFirmwareDownloads;
            FirmwareVolumeText = summary.FirmwareVolumeText;
            TotalConnections = summary.TotalConnections;

            PeriodTitle = SelectedPeriod switch
            {
                StatisticsPeriod.Today => Loc.Get("L.Stats.Period.Today"),
                StatisticsPeriod.Week => Loc.Get("L.Stats.Period.Week"),
                StatisticsPeriod.Month => Loc.Get("L.Stats.Period.Month"),
                StatisticsPeriod.Year => Loc.Get("L.Stats.Period.Year"),
                _ => Loc.Get("L.Stats.Period.AllTime")
            };

            TopApps.Clear();
            foreach (var item in summary.TopApps) TopApps.Add(item);

            TopDevices.Clear();
            foreach (var item in summary.TopDevices) TopDevices.Add(item);

            Methods.Clear();
            foreach (var item in summary.Methods) Methods.Add(item);

            Timeline.Clear();
            foreach (var item in summary.Timeline) Timeline.Add(item);

            RecentActivity.Clear();
            foreach (var item in summary.RecentActivity) RecentActivity.Add(item);
        });
    }
}

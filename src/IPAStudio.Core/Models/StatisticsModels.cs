using System;
using System.Collections.Generic;

namespace IPAStudio.Core.Models;

public enum StatisticsPeriod
{
    Today,
    Week,
    Month,
    Year,
    AllTime
}

public sealed class AppInstallRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string AppName { get; set; } = "";
    public string BundleId { get; set; } = "";
    public string Version { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Method { get; set; } = "Catalog"; // "Catalog", "File", "Transfer", "DirectUrl"
    public bool Succeeded { get; set; } = true;
}

public sealed class DownloadRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "Ipa"; // "Ipa", "Firmware"
    public string Name { get; set; } = "";
    public string Identifier { get; set; } = "";
    public long FileSize { get; set; }
}

public sealed class DeviceConnectionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string DeviceModel { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ConnectionType { get; set; } = "USB";
}

public sealed class StatisticsStore
{
    public List<AppInstallRecord> Installs { get; set; } = new();
    public List<DownloadRecord> Downloads { get; set; } = new();
    public List<DeviceConnectionRecord> Connections { get; set; } = new();
}

public sealed class TopItemStat
{
    public int Rank { get; set; }
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? BundleId { get; set; }
    public string? IconPath { get; set; }
    public int Count { get; set; }
    public double Percentage { get; set; }
    public string CountText => $"{Count}";
    public string PercentageText => $"{Percentage:F1}%";
}

public sealed class MethodBreakdownStat
{
    public string Method { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
    public double Percentage { get; set; }
    public string ColorHex { get; set; } = "#0A84FF";
    public string PercentageText => $"{Percentage:F1}%";
}

public sealed class TimelinePointStat
{
    public string Label { get; set; } = "";
    public int Installs { get; set; }
    public int Downloads { get; set; }
    public double BarHeight { get; set; }
}

public sealed class RecentActivityStat
{
    public DateTime Timestamp { get; set; }
    public string TimeText => Timestamp.ToString("dd.MM HH:mm");
    public string? IconPath { get; set; }
    public string Icon { get; set; } = "📲";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? BundleId { get; set; }
    public string StatusText { get; set; } = "Успешно";
    public bool IsSuccess { get; set; } = true;
}

public sealed class StatisticsSummary
{
    public StatisticsPeriod Period { get; set; }
    public int TotalInstalls { get; set; }
    public int TodayInstalls { get; set; }
    public int UniqueDevices { get; set; }
    public int TotalIpaDownloads { get; set; }
    public long TotalIpaBytes { get; set; }
    public int TotalFirmwareDownloads { get; set; }
    public long TotalFirmwareBytes { get; set; }
    public int TotalConnections { get; set; }

    public string IpaVolumeText => $"{TotalIpaBytes / 1024d / 1024d / 1024d:F2} GB";
    public string FirmwareVolumeText => $"{TotalFirmwareBytes / 1024d / 1024d / 1024d:F2} GB";

    public List<TopItemStat> TopApps { get; set; } = new();
    public List<TopItemStat> TopDevices { get; set; } = new();
    public List<MethodBreakdownStat> Methods { get; set; } = new();
    public List<TimelinePointStat> Timeline { get; set; } = new();
    public List<RecentActivityStat> RecentActivity { get; set; } = new();
}

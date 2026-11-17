using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

public sealed class StatisticsService
{
    private readonly string _storagePath;
    private readonly ToolLocator _tools;
    private readonly object _lock = new();
    private StatisticsStore _store = new();

    public event EventHandler? StatisticsChanged;

    public StatisticsService(ToolLocator? tools = null)
    {
        _tools = tools ?? new ToolLocator();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "IPAStudio");
        Directory.CreateDirectory(dir);
        _storagePath = Path.Combine(dir, "statistics.json");

        if (!File.Exists(_storagePath))
        {
            SeedInitialData();
            Save();
        }
        else
        {
            Load();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    var json = File.ReadAllText(_storagePath);
                    var loaded = JsonSerializer.Deserialize<StatisticsStore>(json);
                    if (loaded is not null)
                    {
                        _store = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
                _store = new StatisticsStore();
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }
        StatisticsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RecordAppInstall(string appName, string bundleId, string version, string deviceModel, string deviceId, string method, bool succeeded = true)
    {
        lock (_lock)
        {
            _store.Installs.Insert(0, new AppInstallRecord
            {
                AppName = !string.IsNullOrWhiteSpace(appName) ? appName : "Application",
                BundleId = bundleId,
                Version = version,
                DeviceModel = !string.IsNullOrWhiteSpace(deviceModel) ? deviceModel : "iPhone",
                DeviceId = deviceId,
                Method = !string.IsNullOrWhiteSpace(method) ? method : "Catalog",
                Succeeded = succeeded,
                Timestamp = DateTime.Now
            });

            // Keep up to 2000 records
            if (_store.Installs.Count > 2000)
            {
                _store.Installs.RemoveRange(2000, _store.Installs.Count - 2000);
            }
        }
        Save();
    }

    public void RecordIpaDownload(string name, string identifier, long fileSize)
    {
        lock (_lock)
        {
            _store.Downloads.Insert(0, new DownloadRecord
            {
                Kind = "Ipa",
                Name = name,
                Identifier = identifier,
                FileSize = fileSize,
                Timestamp = DateTime.Now
            });

            if (_store.Downloads.Count > 2000)
            {
                _store.Downloads.RemoveRange(2000, _store.Downloads.Count - 2000);
            }
        }
        Save();
    }

    public void RecordFirmwareDownload(string name, string identifier, long fileSize)
    {
        lock (_lock)
        {
            _store.Downloads.Insert(0, new DownloadRecord
            {
                Kind = "Firmware",
                Name = name,
                Identifier = identifier,
                FileSize = fileSize,
                Timestamp = DateTime.Now
            });

            if (_store.Downloads.Count > 2000)
            {
                _store.Downloads.RemoveRange(2000, _store.Downloads.Count - 2000);
            }
        }
        Save();
    }

    public void RecordDeviceConnection(string deviceModel, string deviceId, string connectionType = "USB")
    {
        lock (_lock)
        {
            _store.Connections.Insert(0, new DeviceConnectionRecord
            {
                DeviceModel = !string.IsNullOrWhiteSpace(deviceModel) ? deviceModel : "iPhone",
                DeviceId = deviceId,
                ConnectionType = connectionType,
                Timestamp = DateTime.Now
            });

            if (_store.Connections.Count > 1000)
            {
                _store.Connections.RemoveRange(1000, _store.Connections.Count - 1000);
            }
        }
        Save();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _store.Installs.Clear();
            _store.Downloads.Clear();
            _store.Connections.Clear();
        }
        Save();
    }

    public StatisticsSummary GetSummary(StatisticsPeriod period)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            DateTime fromDate = period switch
            {
                StatisticsPeriod.Today => now.Date,
                StatisticsPeriod.Week => now.Date.AddDays(-6),
                StatisticsPeriod.Month => now.Date.AddDays(-29),
                StatisticsPeriod.Year => now.Date.AddDays(-364),
                _ => DateTime.MinValue
            };

            var filteredInstalls = _store.Installs.Where(i => i.Timestamp >= fromDate).ToList();
            var filteredDownloads = _store.Downloads.Where(d => d.Timestamp >= fromDate).ToList();
            var filteredConnections = _store.Connections.Where(c => c.Timestamp >= fromDate).ToList();

            var todayInstalls = _store.Installs.Count(i => i.Timestamp >= now.Date);
            var uniqueDevices = filteredInstalls.Select(i => i.DeviceId).Where(id => !string.IsNullOrEmpty(id))
                .Union(filteredConnections.Select(c => c.DeviceId).Where(id => !string.IsNullOrEmpty(id)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();

            if (uniqueDevices == 0 && filteredInstalls.Count > 0)
            {
                uniqueDevices = filteredInstalls.Select(i => i.DeviceModel).Distinct().Count();
            }

            var ipaDownloads = filteredDownloads.Where(d => d.Kind == "Ipa").ToList();
            var fwDownloads = filteredDownloads.Where(d => d.Kind == "Firmware").ToList();

            var summary = new StatisticsSummary
            {
                Period = period,
                TotalInstalls = filteredInstalls.Count,
                TodayInstalls = todayInstalls,
                UniqueDevices = (filteredInstalls.Count > 0 || filteredConnections.Count > 0) ? Math.Max(uniqueDevices, 1) : 0,
                TotalIpaDownloads = ipaDownloads.Count,
                TotalIpaBytes = ipaDownloads.Sum(d => d.FileSize),
                TotalFirmwareDownloads = fwDownloads.Count,
                TotalFirmwareBytes = fwDownloads.Sum(d => d.FileSize),
                TotalConnections = filteredConnections.Count
            };

            // Top Apps
            var appGroups = filteredInstalls
                .GroupBy(i => !string.IsNullOrEmpty(i.AppName) ? i.AppName : i.BundleId)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToList();

            var totalTopApps = Math.Max(1, filteredInstalls.Count);
            var rank = 1;
            foreach (var g in appGroups)
            {
                var first = g.First();
                var iconPath = AppIconResolver.ResolveIconPath(first.BundleId, g.Key, _tools);
                summary.TopApps.Add(new TopItemStat
                {
                    Rank = rank++,
                    Name = g.Key,
                    Subtitle = first.BundleId,
                    BundleId = first.BundleId,
                    IconPath = iconPath,
                    Count = g.Count(),
                    Percentage = Math.Round((double)g.Count() * 100 / totalTopApps, 1)
                });
            }

            // Top Devices
            var deviceGroups = filteredInstalls.Select(i => i.DeviceModel)
                .Concat(filteredConnections.Select(c => c.DeviceModel))
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .GroupBy(m => m)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToList();

            var totalDevCount = Math.Max(1, deviceGroups.Sum(g => g.Count()));
            rank = 1;
            foreach (var g in deviceGroups)
            {
                summary.TopDevices.Add(new TopItemStat
                {
                    Rank = rank++,
                    Name = g.Key,
                    Subtitle = "Устройство Apple",
                    Count = g.Count(),
                    Percentage = Math.Round((double)g.Count() * 100 / totalDevCount, 1)
                });
            }

            // Methods Breakdown
            var methodGroups = filteredInstalls
                .GroupBy(i => i.Method)
                .ToList();

            var methodNames = new Dictionary<string, (string Display, string Color)>
            {
                ["Catalog"] = ("Каталог приложений", "#0A84FF"),
                ["File"] = ("Прямая установка IPA", "#30D158"),
                ["Transfer"] = ("Перенос с устройства", "#BF5AF2"),
                ["DirectUrl"] = ("Скачивание IPA", "#FF9F0A")
            };

            foreach (var mg in methodGroups)
            {
                var (display, color) = methodNames.TryGetValue(mg.Key, out var info) ? info : (mg.Key, "#0A84FF");
                summary.Methods.Add(new MethodBreakdownStat
                {
                    Method = mg.Key,
                    DisplayName = display,
                    Count = mg.Count(),
                    Percentage = Math.Round((double)mg.Count() * 100 / Math.Max(1, filteredInstalls.Count), 1),
                    ColorHex = color
                });
            }

            // Activity Timeline (Chart points)
            var pointsCount = period switch
            {
                StatisticsPeriod.Today => 24, // by hour
                StatisticsPeriod.Week => 7,   // by day
                StatisticsPeriod.Month => 30, // by day
                StatisticsPeriod.Year => 12,  // by month
                _ => 12
            };

            var timelineList = new List<TimelinePointStat>();
            if (period == StatisticsPeriod.Today)
            {
                for (var h = 0; h < 24; h += 2)
                {
                    var count = filteredInstalls.Count(i => i.Timestamp.Hour >= h && i.Timestamp.Hour < h + 2);
                    var dlCount = filteredDownloads.Count(d => d.Timestamp.Hour >= h && d.Timestamp.Hour < h + 2);
                    timelineList.Add(new TimelinePointStat
                    {
                        Label = $"{h:D2}:00",
                        Installs = count,
                        Downloads = dlCount,
                        BarHeight = Math.Max(4, Math.Min(120, count * 15))
                    });
                }
            }
            else if (period is StatisticsPeriod.Week or StatisticsPeriod.Month)
            {
                var days = period == StatisticsPeriod.Week ? 7 : 30;
                var step = period == StatisticsPeriod.Week ? 1 : 3;
                for (var d = days - 1; d >= 0; d -= step)
                {
                    var dayDate = now.Date.AddDays(-d);
                    var count = filteredInstalls.Count(i => i.Timestamp.Date == dayDate);
                    var dlCount = filteredDownloads.Count(d => d.Timestamp.Date == dayDate);
                    timelineList.Add(new TimelinePointStat
                    {
                        Label = dayDate.ToString("dd.MM"),
                        Installs = count,
                        Downloads = dlCount,
                        BarHeight = Math.Max(4, Math.Min(120, count * 12))
                    });
                }
            }
            else // Year or AllTime (by month)
            {
                for (var m = 11; m >= 0; m--)
                {
                    var monthDate = now.AddMonths(-m);
                    var count = filteredInstalls.Count(i => i.Timestamp.Year == monthDate.Year && i.Timestamp.Month == monthDate.Month);
                    var dlCount = filteredDownloads.Count(d => d.Timestamp.Year == monthDate.Year && d.Timestamp.Month == monthDate.Month);
                    timelineList.Add(new TimelinePointStat
                    {
                        Label = monthDate.ToString("MMM"),
                        Installs = count,
                        Downloads = dlCount,
                        BarHeight = Math.Max(4, Math.Min(120, count * 8))
                    });
                }
            }

            var maxInstalls = timelineList.Max(t => Math.Max(t.Installs, t.Downloads));
            if (maxInstalls > 0)
            {
                foreach (var t in timelineList)
                {
                    var val = Math.Max(t.Installs, t.Downloads);
                    t.BarHeight = Math.Max(8, (double)val / maxInstalls * 110);
                }
            }
            summary.Timeline = timelineList;

            // Recent activity
            summary.RecentActivity = filteredInstalls.Take(15).Select(i => new RecentActivityStat
            {
                Timestamp = i.Timestamp,
                BundleId = i.BundleId,
                IconPath = AppIconResolver.ResolveIconPath(i.BundleId, i.AppName, _tools),
                Icon = i.Method == "Transfer" ? "🔄" : (i.Method == "File" ? "📁" : "📲"),
                Title = i.AppName,
                Subtitle = $"{i.DeviceModel} · {i.Method}",
                StatusText = i.Succeeded ? "Успешно" : "Ошибка",
                IsSuccess = i.Succeeded
            }).ToList();

            return summary;
        }
    }

    private void SeedInitialData()
    {
        var now = DateTime.Now;
        var sampleApps = new[]
        {
            ("2ГИС: карты, навигатор, друзья", "ru.doublegis.grymmobile", "7.31.1", "Catalog"),
            ("Telegram Messenger", "ph.telegra.Telegraph", "10.14.1", "Catalog"),
            ("СберБанк Онлайн", "ru.sberbank.sberbankonline", "15.8.0", "File"),
            ("T-Bank (Тинькофф)", "com.tcsbank.mobile", "6.24.1", "Transfer"),
            ("ВКонтакте: соцсеть, музыка", "com.vk.vkclient", "8.72.0", "Catalog"),
            ("Яндекс Карты и Навигатор", "ru.yandex.yandexmaps", "17.4.0", "Catalog"),
            ("Альфа-Банк", "ru.alfabank.mobilephone", "12.80.0", "Transfer")
        };

        var sampleDevices = new[] { "iPhone 14 Pro", "iPhone 17 Pro Max", "iPhone 13", "iPad Pro 11\"" };

        for (var i = 0; i < 28; i++)
        {
            var app = sampleApps[i % sampleApps.Length];
            var dev = sampleDevices[i % sampleDevices.Length];
            var dt = now.AddDays(-((i * 3) % 25)).AddHours(-(i * 2 % 20));

            _store.Installs.Add(new AppInstallRecord
            {
                AppName = app.Item1,
                BundleId = app.Item2,
                Version = app.Item3,
                DeviceModel = dev,
                DeviceId = $"DEV-{i:X4}",
                Method = app.Item4,
                Succeeded = true,
                Timestamp = dt
            });
        }

        // Add some downloads
        _store.Downloads.Add(new DownloadRecord
        {
            Kind = "Ipa",
            Name = "2ГИС: карты, навигатор, друзья",
            Identifier = "ru.doublegis.grymmobile",
            FileSize = 285_000_000,
            Timestamp = now.AddHours(-2)
        });
        _store.Downloads.Add(new DownloadRecord
        {
            Kind = "Firmware",
            Name = "iPhone 17 Pro Max (iOS 27.2.1)",
            Identifier = "iPhone18,2",
            FileSize = 13_150_000_000,
            Timestamp = now.AddDays(-1)
        });

        // Add some connections
        foreach (var dev in sampleDevices)
        {
            _store.Connections.Add(new DeviceConnectionRecord
            {
                DeviceModel = dev,
                DeviceId = Guid.NewGuid().ToString("N")[..16],
                ConnectionType = "USB",
                Timestamp = now.AddDays(-1)
            });
        }
    }
}

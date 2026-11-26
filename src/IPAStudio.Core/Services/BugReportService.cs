using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Result of submitting a bug report directly to the developer's website backend.
/// </summary>
public sealed record BugReportSubmitResult(
    bool Success,
    int StatusCode,
    string? ReportId = null,
    string? ErrorMessage = null);

/// <summary>
/// Prepares sanitized diagnostic bug reports and submits them directly to the
/// developer's website backend or via GitHub Issues.
/// </summary>
public sealed class BugReportService
{
    public const string GitHubRepoIssuesUrl = "https://github.com/afteryester-sys/ipa-downloader-ui/issues/new";

    private readonly HttpClient? _http;
    private readonly SettingsService? _settings;

    public BugReportService(HttpClient? http = null, SettingsService? settings = null)
    {
        _http = http;
        _settings = settings;
    }

    public static string GetAppVersion()
    {
        var entryAsm = Assembly.GetEntryAssembly() ?? typeof(BugReportService).Assembly;
        var infoVer = entryAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var plusIdx = infoVer.IndexOf('+');
            return plusIdx > 0 ? infoVer[..plusIdx] : infoVer;
        }

        var ver = entryAsm.GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.8.89";
    }

    public static string GetOsSummary() =>
        $"{Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";

    /// <summary>
    /// Formats connected iOS device models and OS versions without including serials,
    /// phone numbers, Apple IDs, or UDIDs.
    /// </summary>
    public static string FormatDeviceSummary(IEnumerable<Device>? devices)
    {
        if (devices is null) return string.Empty;

        var parts = devices
            .Where(d => d is not null)
            .Select(d =>
            {
                var model = !string.IsNullOrWhiteSpace(d.Model) ? d.Model : (!string.IsNullOrWhiteSpace(d.ProductType) ? d.ProductType : d.DeviceClass);
                var os = string.IsNullOrWhiteSpace(d.OsVersion) ? "iOS" : $"iOS {d.OsVersion}";
                return $"{model} ({os})";
            })
            .ToList();

        return parts.Count > 0 ? string.Join(", ", parts) : string.Empty;
    }

    public string BuildMarkdownReport(
        string subject,
        string description,
        bool includeDiagnostics,
        string? deviceSummary = null,
        int maxLogLines = 50)
    {
        var sb = new StringBuilder();
        var cleanDesc = string.IsNullOrWhiteSpace(description)
            ? "Описание не заполнено."
            : description.Trim();

        sb.AppendLine("### Описание проблемы");
        sb.AppendLine(cleanDesc);
        sb.AppendLine();

        if (includeDiagnostics)
        {
            sb.AppendLine("### Системная информация");
            sb.AppendLine($"- **Приложение:** IPA Studio v{GetAppVersion()}");
            sb.AppendLine($"- **ОС:** {GetOsSummary()}");
            sb.AppendLine($"- **Среда:** .NET {Environment.Version}");
            if (!string.IsNullOrWhiteSpace(deviceSummary))
            {
                sb.AppendLine($"- **Устройства:** {deviceSummary}");
            }
            sb.AppendLine($"- **Время (UTC):** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            if (maxLogLines > 0)
            {
                var logs = AppLog.GetRecentLines(maxLogLines);
                if (!string.IsNullOrWhiteSpace(logs))
                {
                    sb.AppendLine("<details>");
                    sb.AppendLine("<summary>Журнал работы (последние записи лога)</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```log");
                    sb.AppendLine(logs);
                    sb.AppendLine("```");
                    sb.AppendLine("</details>");
                }
            }
        }

        return sb.ToString();
    }

    public string BuildGitHubIssueUrl(
        string subject,
        string description,
        bool includeDiagnostics,
        string? deviceSummary = null)
    {
        var cleanTitle = string.IsNullOrWhiteSpace(subject)
            ? "Bug report: error in IPA Studio"
            : subject.Trim();

        var compactBody = BuildMarkdownReport(cleanTitle, description, includeDiagnostics, deviceSummary, maxLogLines: 15);
        const int maxBodyChars = 1500;
        if (compactBody.Length > maxBodyChars)
        {
            compactBody = compactBody[..maxBodyChars] + "\n\n*(Полный отчёт и лог скопированы в буфер обмена — вставьте через Ctrl+V)*";
        }
        else if (includeDiagnostics)
        {
            compactBody += "\n\n*(Полный отчёт и журнал также скопированы в буфер обмена)*";
        }

        var escapedTitle = Uri.EscapeDataString(cleanTitle);
        var escapedBody = Uri.EscapeDataString(compactBody);
        return $"{GitHubRepoIssuesUrl}?title={escapedTitle}&body={escapedBody}&labels=bug";
    }

    /// <summary>
    /// Submits a structured JSON bug report directly to the developer's website API.
    /// </summary>
    public async Task<BugReportSubmitResult> SubmitToWebsiteAsync(
        string subject,
        string description,
        bool includeDiagnostics,
        string? deviceSummary = null,
        CancellationToken ct = default)
    {
        if (_http is null || _settings is null || string.IsNullOrWhiteSpace(_settings.Current.SupportServerUrl))
        {
            return new BugReportSubmitResult(false, 0, ErrorMessage: "Website URL is not configured.");
        }

        var baseUrl = _settings.Current.SupportServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return new BugReportSubmitResult(false, 0, ErrorMessage: "Invalid website URL.");
        }

        var payload = new
        {
            title = string.IsNullOrWhiteSpace(subject) ? "Bug report" : subject.Trim(),
            description = description?.Trim() ?? string.Empty,
            appVersion = GetAppVersion(),
            osVersion = Environment.OSVersion.VersionString,
            architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            dotnetVersion = Environment.Version.ToString(),
            deviceInfo = includeDiagnostics ? deviceSummary : null,
            logs = includeDiagnostics ? AppLog.GetRecentLines(80) : null,
            createdAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };

        string[] candidateEndpoints = ["/api/bug-report", "/api/support/bug-report"];
        int lastStatus = 0;
        string? lastError = null;

        foreach (var path in candidateEndpoints)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                var endpoint = new Uri(baseUri, path);
                using var response = await _http.PostAsJsonAsync(endpoint, payload, timeoutCts.Token).ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    string? reportId = null;
                    try
                    {
                        var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            using var doc = JsonDocument.Parse(text);
                            if (doc.RootElement.TryGetProperty("id", out var idProp))
                                reportId = idProp.ToString();
                        }
                    }
                    catch { /* response id is optional */ }

                    AppLog.Info($"BugReport: submitted to website ({endpoint}, status={lastStatus})");
                    return new BugReportSubmitResult(true, lastStatus, reportId);
                }

                lastError = $"HTTP {lastStatus}";
                if (lastStatus != 404 && lastStatus != 405)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = "Timeout";
                AppLog.Warn($"BugReport: website request timed out for {path}");
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                AppLog.Warn($"BugReport: website submission failed on {path}: {ex.Message}");
                break;
            }
        }

        return new BugReportSubmitResult(false, lastStatus, ErrorMessage: lastError ?? "Unavailable");
    }
}

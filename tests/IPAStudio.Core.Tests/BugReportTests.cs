namespace IPAStudio.Core.Tests;

using System;
using System.Collections.Generic;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

/// <summary>
/// Unit tests for BugReportService formatting, device sanitization, GitHub URL construction,
/// and website submission validation.
/// </summary>
public static class BugReportTests
{
    public static void RunAll()
    {
        TestAppAndOsSummary();
        TestDeviceSummarySanitization();
        TestMarkdownReportAndGitHubUrl();
        TestWebsiteSubmissionGuards();
    }

    private static void TestAppAndOsSummary()
    {
        var ver = BugReportService.GetAppVersion();
        AssertEqual(!string.IsNullOrWhiteSpace(ver), true, "App version non-empty");

        var os = BugReportService.GetOsSummary();
        AssertEqual(os.Contains("Windows", StringComparison.OrdinalIgnoreCase) || os.Length > 0, true, "OS summary non-empty");
    }

    private static void TestDeviceSummarySanitization()
    {
        var devices = new List<Device>
        {
            new()
            {
                Udid = "SECRET-UDID-12345",
                Name = "My Private Phone",
                Model = "iPhone 15 Pro",
                ProductType = "iPhone16,1",
                OsVersion = "18.2",
                SerialNumber = "SERIAL-SECRET",
                AppleId = "private@example.com",
            }
        };

        var summary = BugReportService.FormatDeviceSummary(devices);
        AssertEqual(summary.Contains("iPhone 15 Pro", StringComparison.Ordinal), true, "Model included in summary");
        AssertEqual(summary.Contains("18.2", StringComparison.Ordinal), true, "iOS version included in summary");
        AssertEqual(summary.Contains("SECRET-UDID", StringComparison.Ordinal), false, "UDID excluded from summary");
        AssertEqual(summary.Contains("SERIAL-SECRET", StringComparison.Ordinal), false, "Serial excluded from summary");
        AssertEqual(summary.Contains("private@example.com", StringComparison.Ordinal), false, "Apple ID excluded from summary");
    }

    private static void TestMarkdownReportAndGitHubUrl()
    {
        AppLog.Info("BugReportTests diagnostic marker");
        var service = new BugReportService();

        var md = service.BuildMarkdownReport("Test Crash", "App crashed on import", includeDiagnostics: true, "iPhone 15 Pro (iOS 18.2)");
        AssertEqual(md.Contains("App crashed on import", StringComparison.Ordinal), true, "Markdown contains description");
        AssertEqual(md.Contains("iPhone 15 Pro (iOS 18.2)", StringComparison.Ordinal), true, "Markdown contains device summary");
        AssertEqual(md.Contains("BugReportTests diagnostic marker", StringComparison.Ordinal), true, "Markdown contains recent log lines");

        var url = service.BuildGitHubIssueUrl("Photo crash", "Steps to reproduce", includeDiagnostics: true, "iPhone 15 Pro (iOS 18.2)");
        AssertEqual(url.StartsWith(BugReportService.GitHubRepoIssuesUrl, StringComparison.Ordinal), true, "GitHub URL uses correct repo issues endpoint");
        AssertEqual(url.Contains("title=Photo%20crash", StringComparison.Ordinal), true, "GitHub URL encodes title");
        AssertEqual(url.Length < 4096, true, "GitHub URL stays within browser length limits");
    }

    private static void TestWebsiteSubmissionGuards()
    {
        var serviceWithoutHttp = new BugReportService();
        var res = serviceWithoutHttp.SubmitToWebsiteAsync("Subj", "Desc", true).GetAwaiter().GetResult();
        AssertEqual(res.Success, false, "Website submit without HTTP client returns structured failure");
        AssertEqual(!string.IsNullOrWhiteSpace(res.ErrorMessage), true, "Failure provides error detail");
    }

    private static void AssertEqual<T>(T actual, T expected, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"FAILED: {testName}. Expected '{expected}', got '{actual}'.");
        }
        Console.WriteLine($"  [PASS] {testName}");
    }
}

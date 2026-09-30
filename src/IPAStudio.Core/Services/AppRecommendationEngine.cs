namespace IPAStudio.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;

/// <summary>
/// Evaluates and ranks installed iOS apps according to their compatibility with dropped files,
/// mimicking the intelligent Quick Transfer behavior found in iMazing.
/// </summary>
public static class AppRecommendationEngine
{
    // Known bundle IDs for specialized app categories
    private static readonly Dictionary<string, FileCategory[]> KnownAppProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video players
        ["org.videolan.vlc-ios"] = new[] { FileCategory.Video, FileCategory.Audio },
        ["com.firecore.infuse"] = new[] { FileCategory.Video },
        ["com.pinger.playerxtreme"] = new[] { FileCategory.Video, FileCategory.Audio },
        ["com.kmplayer.kmplayer-ios"] = new[] { FileCategory.Video, FileCategory.Audio },
        ["com.olimsoft.omplayer"] = new[] { FileCategory.Video, FileCategory.Audio },
        ["tv.nplayer.nplayer"] = new[] { FileCategory.Video, FileCategory.Audio },

        // Audio & Music players
        ["com.foobar2000.foobar2000"] = new[] { FileCategory.Audio },
        ["com.everappz.flacplayer"] = new[] { FileCategory.Audio },
        ["com.neoplayer.neoplayer"] = new[] { FileCategory.Audio },
        ["com.neutronamp.neutron"] = new[] { FileCategory.Audio },

        // Books & Readers
        ["com.apple.iBooks"] = new[] { FileCategory.Book },
        ["com.adobe.Adobe-Reader"] = new[] { FileCategory.Book, FileCategory.Document },
        ["com.amazon.Lollipop"] = new[] { FileCategory.Book }, // Kindle
        ["com.kobo.KoboBooks"] = new[] { FileCategory.Book },
        ["com.goodiware.goodreader"] = new[] { FileCategory.Book, FileCategory.Document },
        ["com.goodiware.goodreader4"] = new[] { FileCategory.Book, FileCategory.Document },
        ["kolyvan.kybook"] = new[] { FileCategory.Book },
        ["kolyvan.kybook2"] = new[] { FileCategory.Book },
        ["kolyvan.kybook3"] = new[] { FileCategory.Book },
        ["com.chp.yomu"] = new[] { FileCategory.Book },

        // Office & Productivity
        ["com.microsoft.Office.Word"] = new[] { FileCategory.Document },
        ["com.microsoft.Office.Excel"] = new[] { FileCategory.Document },
        ["com.microsoft.Office.Powerpoint"] = new[] { FileCategory.Document },
        ["com.microsoft.Office.Outlook"] = new[] { FileCategory.Contact },
        ["com.apple.Pages"] = new[] { FileCategory.Document },
        ["com.apple.Numbers"] = new[] { FileCategory.Document },
        ["com.apple.Keynote"] = new[] { FileCategory.Document },

        // Multi-purpose File Managers (support everything)
        ["com.readdle.ReaddleDocsIPad"] = new[]
        {
            FileCategory.Video, FileCategory.Audio, FileCategory.Book,
            FileCategory.Document, FileCategory.Photo, FileCategory.Archive
        },
        ["com.skyjos.fileexplorer"] = new[]
        {
            FileCategory.Video, FileCategory.Audio, FileCategory.Book,
            FileCategory.Document, FileCategory.Archive
        },
        ["com.unzip.app"] = new[] { FileCategory.Archive, FileCategory.Document },
    };

    /// <summary>
    /// Ranks installed FileSharingApp instances according to their match score for the given files.
    /// Returns ordered results (highest match first).
    /// </summary>
    public static IReadOnlyList<AppMatchScore> RankApps(
        IEnumerable<TransferPayload> files,
        IEnumerable<FileSharingApp> availableApps)
    {
        if (availableApps is null) return Array.Empty<AppMatchScore>();
        var appsList = availableApps.ToList();
        if (appsList.Count == 0) return Array.Empty<AppMatchScore>();

        var payloads = (files ?? Enumerable.Empty<TransferPayload>()).ToList();

        // Count distribution of categories in the dropped set
        var nonAppPayloads = payloads.Where(p => p.Category != FileCategory.App).ToList();
        var categoryCounts = nonAppPayloads
            .GroupBy(p => p.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        var scores = new List<AppMatchScore>();

        foreach (var app in appsList)
        {
            var (score, reason) = EvaluateAppMatch(app, categoryCounts);
            scores.Add(new AppMatchScore(app, score, false, reason));
        }

        // Sort descending by score, then ascending by name
        var ranked = scores
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Flag the highest-scoring apps as recommended
        if (ranked.Count > 0)
        {
            var topScore = ranked[0].Score;
            if (topScore >= 50)
            {
                for (var i = 0; i < ranked.Count; i++)
                {
                    if (ranked[i].Score == topScore)
                    {
                        ranked[i] = ranked[i] with { IsRecommended = true };
                    }
                }
            }
        }

        return ranked;
    }

    /// <summary>
    /// Returns the single best app recommendation for the files, or null if no apps exist.
    /// </summary>
    public static FileSharingApp? GetBestRecommendation(
        IEnumerable<TransferPayload> files,
        IEnumerable<FileSharingApp> availableApps)
    {
        var ranked = RankApps(files, availableApps);
        return ranked.Count > 0 ? ranked[0].App : null;
    }

    private static (int Score, string Reason) EvaluateAppMatch(
        FileSharingApp app,
        IReadOnlyDictionary<FileCategory, int> categoryCounts)
    {
        if (categoryCounts.Count == 0)
        {
            // No specific file category dropped: neutral container score
            return (10, Loc.Get("L.QuickTransfer.FileSharingHint"));
        }

        var totalScore = 0;
        var primaryCategory = categoryCounts.OrderByDescending(kv => kv.Value).First().Key;
        var hasExactCategorySupport = false;

        // Check known app profiles
        if (KnownAppProfiles.TryGetValue(app.BundleId, out var supportedCategories))
        {
            if (supportedCategories.Contains(primaryCategory))
            {
                hasExactCategorySupport = true;
                totalScore += supportedCategories.Length <= 2 ? 120 : 80;
            }
        }

        // Keyword analysis in bundleId or DisplayName
        var nameLower = (app.Name + " " + app.BundleId).ToLowerInvariant();
        var keywordMatch = primaryCategory switch
        {
            FileCategory.Video => nameLower.Contains("vlc") || nameLower.Contains("player") || nameLower.Contains("video") || nameLower.Contains("movie") || nameLower.Contains("media"),
            FileCategory.Audio => nameLower.Contains("music") || nameLower.Contains("audio") || nameLower.Contains("player") || nameLower.Contains("sound") || nameLower.Contains("flac") || nameLower.Contains("vlc"),
            FileCategory.Book => nameLower.Contains("book") || nameLower.Contains("reader") || nameLower.Contains("pdf") || nameLower.Contains("epub") || nameLower.Contains("mobi"),
            FileCategory.Document => nameLower.Contains("office") || nameLower.Contains("doc") || nameLower.Contains("word") || nameLower.Contains("excel") || nameLower.Contains("sheet") || nameLower.Contains("text"),
            FileCategory.Archive => nameLower.Contains("zip") || nameLower.Contains("rar") || nameLower.Contains("archive") || nameLower.Contains("file"),
            FileCategory.Photo => nameLower.Contains("photo") || nameLower.Contains("image") || nameLower.Contains("pic"),
            FileCategory.Contact => nameLower.Contains("contact") || nameLower.Contains("vcf") || nameLower.Contains("vcard") || nameLower.Contains("address") || nameLower.Contains("outlook") || nameLower.Contains("mail"),
            _ => false
        };

        if (keywordMatch && !hasExactCategorySupport)
        {
            totalScore += 50;
        }
        else if (keywordMatch && hasExactCategorySupport)
        {
            totalScore += 20;
        }

        // General file managers always offer good baseline compatibility (e.g. Documents by Readdle)
        if (nameLower.Contains("document") || nameLower.Contains("files") || nameLower.Contains("readdle") || nameLower.Contains("explorer"))
        {
            totalScore += 10;
        }

        // Base container score for having file sharing enabled
        totalScore += 5;

        string reason;
        if (hasExactCategorySupport || totalScore >= 100)
        {
            reason = string.Format(Loc.Get("L.QuickTransfer.RecommendedFor"), FileClassifier.GetCategoryDisplayName(primaryCategory), app.Name);
        }
        else if (keywordMatch || totalScore >= 60)
        {
            reason = string.Format(Loc.Get("L.QuickTransfer.RecommendedFor"), FileClassifier.GetCategoryDisplayName(primaryCategory), app.Name);
        }
        else
        {
            reason = Loc.Get("L.QuickTransfer.FileSharingHint");
        }

        return (totalScore, reason);
    }
}

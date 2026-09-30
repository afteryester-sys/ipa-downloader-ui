using System.Text.Json;
using IPAStudio.Core.Diagnostics;

namespace IPAStudio.Core.Tools;

/// <summary>
/// Keeps ipatool's on-disk profile (&lt;HOME&gt;/.ipatool) in a state the tool can actually
/// start from.
///
/// ipatool v2 loads a persistent cookie jar at startup for EVERY subcommand, through
/// util.Must(cookiejar.New(...)) - a hard panic when the file is not valid JSON:
///
///   panic: cannot load cookies: invalid character '#' looking for beginning of value
///
/// The process then exits with code 2 before it ever reaches "auth login", "purchase" or
/// "download", so the app sees an unexplained tool failure and the user sees "login is
/// broken" and "some apps refuse to download". Because the damaged file lives in the user
/// profile and not in the install folder, reinstalling or rolling the app back to an older
/// version does not clear it - which is exactly the "I downgraded and authentication is
/// still dead" case this guards against.
///
/// The jar is pure cache: Apple credentials live in ipatool's keychain file, not here.
/// Quarantining it therefore costs nothing but a fresh handshake.
/// </summary>
public static class IpatoolProfile
{
    /// <summary>The profile folder ipatool resolves from HOME for the active backend.</summary>
    public static string ConfigFolder(ToolLocator tools)
    {
        var home = tools.IpatoolEnvironment is { } env && env.TryGetValue("HOME", out var beta)
            ? beta
            : Environment.GetEnvironmentVariable("HOME")
              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(home, ".ipatool");
    }

    public static string CookieJarPath(ToolLocator tools) => Path.Combine(ConfigFolder(tools), "cookies");
    public static string RustCookieJarPath(ToolLocator tools) => Path.Combine(ConfigFolder(tools), "cookies.json");

    /// <summary>
    /// True when tool output is the cookie-jar startup panic or deserialization failure
    /// rather than a real authentication or download error.
    /// </summary>
    public static bool IsCookieJarFailure(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("cannot load cookies")
            || (lower.Contains("cookiejar") && lower.Contains("panic"))
            || (lower.Contains("cookies") && lower.Contains("looking for beginning of value"))
            || (lower.Contains("cookie") && lower.Contains("serde"))
            || lower.Contains("cannot deserialize cookies");
    }

    /// <summary>
    /// True when Apple's gateway, WAF or network connection dropped / timed out or returned
    /// an HTML error page (504, 502, 503) instead of a plist dictionary.
    /// These failures are transient and warrant an immediate retry with purged cookies.
    /// </summary>
    public static bool IsTransientAuthFailure(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("operation timed out")
            || lower.Contains("timed out")
            || lower.Contains("timeout")
            || lower.Contains("invalid type: string \"<html>\"")
            || lower.Contains("invalid type: string '<html>'")
            || lower.Contains("<html>")
            || lower.Contains("504 gateway")
            || lower.Contains("502 bad gateway")
            || lower.Contains("503 service unavailable")
            || lower.Contains("connection reset")
            || lower.Contains("connection refused")
            || lower.Contains("tls handshake")
            || lower.Contains("broken pipe");
    }

    /// <summary>
    /// Discards cookie jars (both Go 'cookies' and Rust 'cookies.json') when invalid or forced.
    /// Returns true when something was reset.
    /// </summary>
    public static bool RepairCookieJar(ToolLocator tools, bool force = false)
    {
        var cleaned = false;
        try
        {
            var folder = ConfigFolder(tools);
            Directory.CreateDirectory(folder);

            var targets = new[]
            {
                Path.Combine(folder, "cookies"),
                Path.Combine(folder, "cookies.json")
            };

            foreach (var path in targets)
            {
                if (!File.Exists(path)) continue;

                if (!force && IsLoadableJson(path)) continue;

                var quarantine = path + ".corrupt";
                if (File.Exists(quarantine))
                {
                    try { File.Delete(quarantine); } catch { /* ignore */ }
                }

                try
                {
                    File.Move(path, quarantine);
                    cleaned = true;
                    AppLog.Warn($"ipatool cookie jar was reset: {Path.GetFileName(path)}");
                }
                catch
                {
                    try { File.Delete(path); cleaned = true; } catch { /* ignore */ }
                }
            }

            return cleaned;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reset ipatool cookie jars: {ex.Message}");
            return cleaned;
        }
    }

    private static bool IsLoadableJson(string path)
    {
        try
        {
            var text = File.ReadAllText(path);

            // An empty jar is valid: persistent-cookiejar treats a zero-length file as
            // "no cookies yet" and writes a fresh one.
            if (string.IsNullOrWhiteSpace(text)) return true;

            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

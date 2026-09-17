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

    /// <summary>
    /// True when tool output is the cookie-jar startup panic rather than a real
    /// authentication or download error.
    /// </summary>
    public static bool IsCookieJarFailure(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("cannot load cookies")
            || (lower.Contains("cookiejar") && lower.Contains("panic"))
            || (lower.Contains("cookies") && lower.Contains("looking for beginning of value"));
    }

    /// <summary>
    /// Discards the cookie jar when it is not loadable JSON. Returns true when something was
    /// removed, so callers can retry the command they were about to run.
    ///
    /// Run before every ipatool invocation: the check is a few hundred bytes of JSON parsing,
    /// and skipping it means the panic surfaces as a misleading login error.
    /// </summary>
    public static bool RepairCookieJar(ToolLocator tools, bool force = false)
    {
        try
        {
            var folder = ConfigFolder(tools);
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, "cookies");
            if (!File.Exists(path)) return false;

            if (!force && IsLoadableJson(path)) return false;

            // Keep one copy for diagnostics instead of deleting outright; a stale quarantine
            // file is harmless, and it is the only evidence of what corrupted the jar.
            var quarantine = path + ".corrupt";
            if (File.Exists(quarantine)) File.Delete(quarantine);
            File.Move(path, quarantine);

            AppLog.Warn($"ipatool cookie jar was unusable and has been reset ({path}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reset the ipatool cookie jar: {ex.Message}");
            return false;
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

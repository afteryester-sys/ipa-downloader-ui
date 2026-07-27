using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services.ICloud;

/// <summary>
/// Persists just enough to skip the password and the 2FA prompt next time.
///
/// What is stored: the account name, Apple's session and trust tokens, and the session
/// cookies. What is never stored: the password. It is used to compute one SRP proof and
/// then dropped, so this file cannot leak it.
///
/// The file is encrypted with DPAPI scoped to the current Windows user, so another
/// account on the same machine cannot read the tokens even with the file in hand.
/// </summary>
internal sealed class ICloudSessionStore
{
    private readonly ToolLocator _tools;

    /// <summary>Extra entropy, so the blob is only decryptable in this app's context.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPAStudio.iCloud.v1");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ICloudSessionStore(ToolLocator tools) => _tools = tools;

    private string FilePath => Path.Combine(_tools.DataFolder, "icloud-session.dat");

    /// <summary>The persisted shape. Tokens only — no password, by design.</summary>
    internal sealed class SessionData
    {
        public string? AccountName { get; set; }
        public string? SessionToken { get; set; }
        public string? TrustToken { get; set; }
        public string? AccountCountry { get; set; }
        public string? Dsid { get; set; }
        public List<CookieData> Cookies { get; set; } = new();
    }

    internal sealed class CookieData
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "/";
    }

    /// <summary>Writes the session, replacing any previous one. Failures are non-fatal.</summary>
    public void Save(SessionData data, CookieContainer cookies)
    {
        try
        {
            _tools.EnsureFolders();

            data.Cookies = Collect(cookies);
            var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
            var blob = Protect(json);

            // Write then move, so an interrupted save cannot leave a truncated file that
            // would force a fresh password prompt.
            var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, blob);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud: could not save session: {ex.Message}");
        }
    }

    /// <summary>Reads the stored session, or null when absent or unreadable.</summary>
    public SessionData? Load(CookieContainer cookies)
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            var json = Unprotect(File.ReadAllBytes(FilePath));
            var data = JsonSerializer.Deserialize<SessionData>(json, JsonOptions);
            if (data is null) return null;

            foreach (var c in data.Cookies)
            {
                if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Domain)) continue;
                try
                {
                    cookies.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain));
                }
                catch (CookieException)
                {
                    // A single malformed cookie must not sink the whole session.
                }
            }
            return data;
        }
        catch (Exception ex)
        {
            // Wrong Windows user, moved machine, or a corrupt file: start clean.
            AppLog.Warn($"iCloud: stored session unusable ({ex.Message}); signing in fresh");
            return null;
        }
    }

    /// <summary>Removes the stored session. Used on sign-out.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud: could not clear session: {ex.Message}");
        }
    }

    private static List<CookieData> Collect(CookieContainer container)
    {
        var result = new List<CookieData>();

        // Only the domains that carry the iCloud session are worth keeping.
        foreach (var url in new[] { "https://idmsa.apple.com", "https://setup.icloud.com", "https://www.icloud.com" })
        {
            foreach (Cookie cookie in container.GetCookies(new Uri(url)))
            {
                if (cookie.Expired) continue;
                if (result.Any(c => c.Name == cookie.Name && c.Domain == cookie.Domain)) continue;

                result.Add(new CookieData
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain,
                    Path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                });
            }
        }
        return result;
    }

    private static byte[] Protect(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return data;
        return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return data;
        return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }
}

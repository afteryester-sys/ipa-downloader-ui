using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Owns the installation-specific passphrase used by the opt-in SAP BETA keychain.
/// The value is random and DPAPI-protected for the current Windows user; Apple credentials
/// themselves are still owned by ipatool and are never persisted by IPA Studio.
/// </summary>
public sealed class AuthSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPAStudio.Auth.SapBeta.v1");
    private readonly ToolLocator _tools;
    private readonly object _gate = new();
    private string? _cached;

    public AuthSecretStore(ToolLocator tools) => _tools = tools;

    public string GetBetaKeychainPassphrase()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached;

            var path = Path.Combine(_tools.DataFolder, "sap-beta-keychain.dpapi");
            try
            {
                if (File.Exists(path))
                {
                    var protectedBytes = File.ReadAllBytes(path);
                    var clear = Unprotect(protectedBytes);
                    _cached = Convert.ToBase64String(clear);
                    CryptographicOperations.ZeroMemory(clear);
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not read the SAP BETA installation secret; replacing it: {ex.GetType().Name}");
            }

            Directory.CreateDirectory(_tools.DataFolder);
            var secret = RandomNumberGenerator.GetBytes(32);
            var protectedSecret = Protect(secret);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedSecret);
            File.Move(temp, path, overwrite: true);
            _cached = Convert.ToBase64String(secret);
            CryptographicOperations.ZeroMemory(secret);
            return _cached;
        }
    }

    private static byte[] Protect(byte[] data) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser)
        : data.ToArray();

    private static byte[] Unprotect(byte[] data) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)
        : data.ToArray();
}

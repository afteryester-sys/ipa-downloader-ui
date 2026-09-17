using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace IPAStudio.Core.Services;

public sealed record SupportComputer(string Id, string DisplayName, string WindowsVersion, string AppVersion,
    string? RustdeskId, string? RustdeskVersion, string? EncryptedSessionSecret, DateTimeOffset? LastSeenAt,
    bool ActiveSession, bool Online, DateTimeOffset? RevokedAt);

[SupportedOSPlatform("windows")]
public sealed class RemoteSupportService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPAStudio", "support-device.json");
    private readonly string _adminKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPAStudio", "support-admin-key.bin");

    public RemoteSupportService(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public bool IsEnabled => _settings.Current.RemoteSupportEnabled && File.Exists(_statePath);

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        var rustDesk = FindRustDesk() ?? throw new FileNotFoundException("Сначала установите официальный RustDesk.");
        var sessionPassword = Base64Url(RandomNumberGenerator.GetBytes(18));
        await ConfigureRustDeskPasswordAsync(rustDesk.Path, sessionPassword, cancellationToken);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var fingerprint = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var consentedAt = DateTimeOffset.UtcNow.ToString("O");
        var displayName = Environment.MachineName;
        var message = $"IPA-STUDIO-CONSENT\n1\n{fingerprint}\n{displayName}\n{consentedAt}";
        var signature = Base64Url(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
        var response = await PostAsync("/api/support/devices/enroll", new
        {
            publicKey, displayName,
            windowsVersion = Environment.OSVersion.VersionString,
            appVersion = AppVersion(), consentedAt, consentVersion = "1", signature,
        }, null, cancellationToken);
        var enrollment = await ReadAsync<Enrollment>(response, cancellationToken);
        var protectedPrivateKey = ProtectedData.Protect(key.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser);
        var protectedSessionPassword = ProtectedData.Protect(Encoding.UTF8.GetBytes(sessionPassword), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await File.WriteAllTextAsync(_statePath, JsonSerializer.Serialize(new DeviceState(
            enrollment.DeviceId, enrollment.Token, Convert.ToBase64String(protectedPrivateKey),
            Convert.ToBase64String(protectedSessionPassword))), cancellationToken);
        _settings.Current.RemoteSupportEnabled = true;
        _settings.Save();
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        if (state is not null)
        {
            try { await PostAsync("/api/support/devices/revoke-self", new { }, state.Token, cancellationToken); }
            catch { /* Local opt-out must still win if the service cannot be reached. */ }
        }
        // Rotate the unattended password before deleting local state. This makes both
        // local and server-side revocation effective even if an old password leaked.
        var rustDesk = FindRustDesk();
        if (rustDesk is not null)
        {
            try { await ConfigureRustDeskPasswordAsync(rustDesk.Value.Path, Base64Url(RandomNumberGenerator.GetBytes(24)), cancellationToken); }
            catch { /* The local consent flag still disables future heartbeats. */ }
        }
        if (File.Exists(_statePath)) File.Delete(_statePath);
        _settings.Current.RemoteSupportEnabled = false;
        _settings.Save();
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        if (!_settings.Current.RemoteSupportEnabled || state is null) return;
        var rustDesk = FindRustDesk();
        var rustDeskId = rustDesk is null ? null : await ReadRustDeskIdAsync(rustDesk.Value.Path, cancellationToken);
        var sessionSecret = string.IsNullOrWhiteSpace(state.ProtectedSessionPassword) ? null : Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(state.ProtectedSessionPassword), null, DataProtectionScope.CurrentUser));
        var response = await PostAsync("/api/support/devices/heartbeat", new
        {
            appVersion = AppVersion(), windowsVersion = Environment.OSVersion.VersionString,
            rustdeskId = rustDeskId, rustdeskVersion = rustDesk?.Version,
            sessionSecret, activeSession = false,
        }, state.Token, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            await DisableAsync(cancellationToken);
        else
            response.EnsureSuccessStatusCode();
    }

    public async Task CreateAdministratorKeyAsync(string outputPath, string bootstrapSecret, string password,
        CancellationToken cancellationToken = default)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var keyFingerprint = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey)));
        using var response = await PostAsync("/api/support/admin/bootstrap", new
        {
            bootstrapSecret, publicKey, password, label = $"{Environment.UserName}@{Environment.MachineName}",
        }, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var protectedKey = EncryptAdminKey(key.ExportPkcs8PrivateKey(), password, keyFingerprint);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(protectedKey, JsonOptions), cancellationToken);
    }

    public bool HasImportedAdministratorKey => File.Exists(_adminKeyPath);

    public async Task ImportAdministratorKeyAsync(string keyFile, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(keyFile, cancellationToken);
        _ = JsonSerializer.Deserialize<AdminKeyFile>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Неверный файл ключа.");
        Directory.CreateDirectory(Path.GetDirectoryName(_adminKeyPath)!);
        var protectedBytes = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("IPAStudio.AdminKey.v1"), DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_adminKeyPath, protectedBytes, cancellationToken);
    }

    public async Task<string> AuthenticateAdministratorAsync(string password,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_adminKeyPath)) throw new InvalidOperationException("Сначала импортируйте файл ключа администратора.");
        var protectedBytes = await File.ReadAllBytesAsync(_adminKeyPath, cancellationToken);
        var bytes = ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes("IPAStudio.AdminKey.v1"), DataProtectionScope.CurrentUser);
        var key = JsonSerializer.Deserialize<AdminKeyFile>(bytes, JsonOptions)
                  ?? throw new InvalidDataException("Неверный импортированный ключ.");
        var challengeResponse = await PostAsync("/api/support/admin/challenge", new { key.Fingerprint }, null, cancellationToken);
        var challenge = await ReadAsync<Challenge>(challengeResponse, cancellationToken);
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(DecryptAdminKey(key, password), out _);
        var signature = Base64Url(signer.SignData(Encoding.UTF8.GetBytes($"IPA-STUDIO-ADMIN\n{challenge.Nonce}"), HashAlgorithmName.SHA256));
        var verifyResponse = await PostAsync("/api/support/admin/verify", new { key.Fingerprint, challenge.Nonce, signature, password }, null, cancellationToken);
        return (await ReadAsync<TokenResponse>(verifyResponse, cancellationToken)).Token;
    }

    public async Task<IReadOnlyList<SupportComputer>> GetComputersAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("/api/support/admin/devices"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        return (await ReadAsync<DeviceList>(response, cancellationToken)).Devices;
    }

    public async Task RevokeAsync(string token, string deviceId, CancellationToken cancellationToken = default) =>
        (await PostAsync($"/api/support/admin/devices/{deviceId}/revoke", new { }, token, cancellationToken)).EnsureSuccessStatusCode();

    public void Connect(SupportComputer computer)
    {
        if (string.IsNullOrWhiteSpace(computer.RustdeskId)) throw new InvalidOperationException("RustDesk ID пока не получен.");
        var rustDesk = FindRustDesk()?.Path ?? throw new FileNotFoundException("RustDesk не установлен.");
        Process.Start(new ProcessStartInfo(rustDesk, $"--connect {computer.RustdeskId}") { UseShellExecute = true });
    }

    private Uri Endpoint(string path) => new(new Uri(_settings.Current.SupportServerUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

    private async Task<HttpResponseMessage> PostAsync(string path, object body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(path)) { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Сервер поддержки: {(int)response.StatusCode} {detail}");
        }
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
               ?? throw new InvalidDataException("Сервер поддержки вернул пустой ответ.");
    }

    private async Task<DeviceState?> LoadStateAsync(CancellationToken ct) => File.Exists(_statePath)
        ? JsonSerializer.Deserialize<DeviceState>(await File.ReadAllTextAsync(_statePath, ct), JsonOptions) : null;

    private static async Task ConfigureRustDeskPasswordAsync(string path, string password, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(path, $"--password {password}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Не удалось запустить RustDesk.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"RustDesk не принял постоянный пароль: {error}".Trim());
    }

    private static async Task<string?> ReadRustDeskIdAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "--get-id")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var id = output.Trim();
            return id.Length is >= 6 and <= 32 && id.All(c => char.IsDigit(c) || c == ' ') ? id : null;
        }
        catch { return null; }
    }

    private static (string Path, string? Version)? FindRustDesk()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RustDesk", "rustdesk.exe"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        return path is null ? null : (path, FileVersionInfo.GetVersionInfo(path).FileVersion);
    }

    private static AdminKeyFile EncryptAdminKey(byte[] privateKey, string password, string keyFingerprint)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[privateKey.Length];
        var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Encrypt(nonce, privateKey, cipher, tag, Encoding.UTF8.GetBytes(keyFingerprint));
        CryptographicOperations.ZeroMemory(encryptionKey);
        return new AdminKeyFile(keyFingerprint, Convert.ToBase64String(cipher), Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag));
    }

    private static byte[] DecryptAdminKey(AdminKeyFile key, string password)
    {
        var salt = Convert.FromBase64String(key.Salt);
        var nonce = Convert.FromBase64String(key.Nonce);
        var tag = Convert.FromBase64String(key.Tag);
        var cipher = Convert.FromBase64String(key.EncryptedPrivateKey);
        var plaintext = new byte[cipher.Length];
        var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Decrypt(nonce, cipher, tag, plaintext, Encoding.UTF8.GetBytes(key.Fingerprint));
        CryptographicOperations.ZeroMemory(encryptionKey);
        return plaintext;
    }

    private static string AppVersion() => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private sealed record DeviceState(string DeviceId, string Token, string ProtectedPrivateKey, string ProtectedSessionPassword);
    private sealed record Enrollment(string DeviceId, string Token);
    private sealed record Challenge(string Nonce, DateTimeOffset ExpiresAt);
    private sealed record TokenResponse(string Token);
    private sealed record DeviceList(List<SupportComputer> Devices);
    private sealed record AdminKeyFile(string Fingerprint, string EncryptedPrivateKey, string Salt, string Nonce, string Tag);
}

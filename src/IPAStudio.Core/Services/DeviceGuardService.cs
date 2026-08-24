using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Decides which devices are guarded and holds the password that unlocks them.
///
/// A guarded device is one that must not be touched by accident. Every action aimed at it —
/// listing what is installed, downloading, transferring, installing, uninstalling — asks for the
/// password first, and asks again the next time, because the point of the guard is that no single
/// unlock ever leaves the device open for the rest of the session.
///
/// Nothing is remembered on purpose. There is no "unlocked until" timestamp, no per-device flag,
/// no cache keyed by serial: a guard that stops asking after the first answer is a guard for the
/// first action only, and the actions that matter here are the later ones.
///
/// The password lives in the sources rather than in settings.json. A file on the same disk is
/// editable by whoever is being guarded against, so storing it there would remove the guard
/// rather than configure it. This is not protection against someone who can rebuild the app; it
/// is protection against the wrong phone being on the cable.
/// </summary>
public sealed class DeviceGuardService
{
    /// <summary>
    /// Serial numbers that require the password. Compared case-insensitively and trimmed, since a
    /// serial arrives from the device as free-form text and pasted ones tend to carry whitespace.
    /// </summary>
    private static readonly HashSet<string> GuardedSerials = new(StringComparer.OrdinalIgnoreCase);

    private const string Password = "NAEBANET";

    /// <summary>
    /// True when this device's serial is on the guarded list.
    ///
    /// A device with no serial reported is treated as unguarded: refusing everything unnamed would
    /// lock up ordinary phones during the first seconds after a connection, before the serial has
    /// been read, and the guard is about one known device rather than about anything unfamiliar.
    /// </summary>
    public bool IsGuarded(Device? device) => IsGuarded(device?.SerialNumber);

    public bool IsGuarded(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        return GuardedSerials.Contains(serial.Trim());
    }

    /// <summary>
    /// Checks an attempt. Trimmed because the password is typed and a trailing space from a paste
    /// is not a wrong answer; the comparison itself is exact.
    /// </summary>
    public bool Verify(string? attempt) =>
        !string.IsNullOrEmpty(attempt) && string.Equals(attempt.Trim(), Password, StringComparison.Ordinal);
}

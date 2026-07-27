namespace IPAStudio.Core.Models;

/// <summary>Outcome of an iCloud sign-in attempt.</summary>
public enum ICloudSignInResult
{
    /// <summary>Signed in; data calls can proceed.</summary>
    Success,

    /// <summary>Apple wants the six-digit code from a trusted device.</summary>
    NeedsTwoFactorCode,

    /// <summary>Apple ID or password rejected.</summary>
    InvalidCredentials,

    /// <summary>Apple refused the sign-in for another reason (locked, rate-limited, …).</summary>
    Failed,
}

/// <summary>One contact from the iCloud address book.</summary>
public sealed class ICloudContact
{
    public string? ContactId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Company { get; init; }
    public string? Notes { get; init; }
    public List<ICloudLabelledValue> Phones { get; init; } = new();
    public List<ICloudLabelledValue> Emails { get; init; } = new();

    /// <summary>Name for display; falls back to company, then a phone or email.</summary>
    public string DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { FirstName, LastName }
                .Where(p => !string.IsNullOrWhiteSpace(p))).Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            if (!string.IsNullOrWhiteSpace(Company)) return Company!;
            return Phones.FirstOrDefault()?.Value ?? Emails.FirstOrDefault()?.Value ?? "—";
        }
    }

    /// <summary>Phones and emails on one line, for the list view.</summary>
    public string Summary => string.Join("  ·  ",
        Phones.Select(p => p.Value).Concat(Emails.Select(e => e.Value)).Take(3));
}

/// <summary>A phone number or email with its label ("home", "work", …).</summary>
public sealed class ICloudLabelledValue
{
    public string Value { get; init; } = "";
    public string? Label { get; init; }
}

/// <summary>A photo or video in the iCloud photo library.</summary>
public sealed class ICloudAsset
{
    public string RecordName { get; init; } = "";
    public string FileName { get; init; } = "";

    /// <summary>Signed URL for the full-size original. These expire, so re-query if stale.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>Signed URL for the small preview shown in the grid.</summary>
    public string? ThumbnailUrl { get; init; }

    public long SizeBytes { get; init; }
    public DateTimeOffset? Created { get; init; }
    public bool IsVideo { get; init; }

    public string SizeText => SizeBytes <= 0
        ? ""
        : SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / 1024.0 / 1024.0:0.#} MB"
            : $"{SizeBytes / 1024.0:0} KB";
}

/// <summary>A note from iCloud Notes.</summary>
public sealed class ICloudNote
{
    public string RecordName { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Snippet { get; init; }
    public string? Folder { get; init; }
    public DateTimeOffset? Modified { get; init; }
}

namespace IPAStudio.Core.Services;

/// <summary>
/// Build-time secrets for the in-app updater.
///
/// IMPORTANT: The value below is intentionally EMPTY in source control.
/// During the release build, CI replaces the token with a fine-grained
/// GitHub Personal Access Token that has ONLY "Contents: read" permission
/// on this single private repository (see auto-release.yml). That token is
/// stored as the repository secret UPDATE_TOKEN.
///
/// Because the token is embedded in the shipped .exe it can be extracted by
/// anyone who has the binary — that is why it must be a fine-grained,
/// read-only, single-repo token. It can never be used to write to the repo
/// or to access anything else.
/// </summary>
internal static class UpdateSecrets
{
    /// <summary>
    /// GitHub token used to read releases of the private repo.
    /// Empty when built locally (updates simply won't work in dev builds).
    /// The exact placeholder string is replaced by CI at build time.
    /// </summary>
    public const string GitHubToken = "__UPDATE_TOKEN__";
}

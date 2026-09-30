# Interface Contract: IAppRecommendationEngine

```csharp
namespace IPAStudio.Core.Services;

using System.Collections.Generic;
using IPAStudio.Core.Models;

/// <summary>
/// Evaluates and ranks installed iOS apps according to their compatibility with dropped files.
/// </summary>
public interface IAppRecommendationEngine
{
    /// <summary>
    /// Ranks available file sharing apps for a given collection of payload files.
    /// Returns ranked matches with scores and recommendation flags.
    /// </summary>
    IReadOnlyList<AppMatchScore> RankApps(
        IEnumerable<TransferPayload> files,
        IEnumerable<FileSharingApp> availableApps);

    /// <summary>
    /// Determines the single best recommended app for the given files, or null if none match.
    /// </summary>
    FileSharingApp? GetBestRecommendation(
        IEnumerable<TransferPayload> files,
        IEnumerable<FileSharingApp> availableApps);
}
```

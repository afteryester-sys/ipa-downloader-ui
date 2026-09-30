# Interface Contract: IFileClassifier

```csharp
namespace IPAStudio.Core.Services;

using System.Collections.Generic;
using IPAStudio.Core.Models;

/// <summary>
/// Classifies files into content categories and describes their transfer payload attributes.
/// </summary>
public interface IFileClassifier
{
    /// <summary>
    /// Detects the category of a file by its extension and metadata.
    /// </summary>
    FileCategory Classify(string filePath);

    /// <summary>
    /// Creates a complete transfer payload description for a file.
    /// </summary>
    TransferPayload Describe(string filePath, string? targetAppName = null);

    /// <summary>
    /// Expands dropped paths (files and directories) into individual playable/transferrable files.
    /// </summary>
    IEnumerable<string> ExpandPaths(IEnumerable<string> paths);
}
```

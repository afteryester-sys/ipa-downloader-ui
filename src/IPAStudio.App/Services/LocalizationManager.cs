using System.Collections.Generic;
using System.Windows;
using IPAStudio.Core.Localization;

namespace IPAStudio.App.Services;

/// <summary>
/// Swaps the language ResourceDictionary at runtime. All UI text is referenced
/// via {DynamicResource L.*} so the whole app re-renders instantly on switch.
///
/// It also publishes a thread-safe snapshot of the active dictionary to
/// <see cref="Loc"/>, which is how background services (downloads, installs, the
/// queue) produce status and error text in the selected language without touching
/// WPF objects off the UI thread.
/// </summary>
public sealed class LocalizationManager
{
    public string CurrentLanguage { get; private set; } = "ru";

    public void Apply(string language)
    {
        language = language is "en" ? "en" : "ru";
        CurrentLanguage = language;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative);
        var loaded = new ResourceDictionary { Source = uri };

        // Replace the previous strings dictionary (identified by the L.AppTitle key).
        var replaced = false;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Contains("L.AppTitle"))
            {
                dictionaries[i] = loaded;
                replaced = true;
                break;
            }
        }
        if (!replaced) dictionaries.Add(loaded);

        PublishToCore(loaded);
    }

    /// <summary>
    /// Copies the dictionary into a plain string map and hands it to <see cref="Loc"/>.
    /// A snapshot rather than a live lookup, because Core runs on worker threads and a
    /// <see cref="ResourceDictionary"/> may only be read from the thread that owns it.
    /// </summary>
    private static void PublishToCore(ResourceDictionary dictionary)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is string value)
                snapshot[name] = value;
        }

        Loc.SetResolver(key => snapshot.TryGetValue(key, out var value) ? value : null);
    }

    /// <summary>Resolves a localized string by key for use in code-behind/viewmodels.</summary>
    public string this[string key]
        => Application.Current.TryFindResource(key) as string ?? key;
}

using System.Windows;

namespace IPAStudio.App.Services;

/// <summary>
/// Swaps the language ResourceDictionary at runtime. All UI text is referenced
/// via {DynamicResource L.*} so the whole app re-renders instantly on switch.
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

        // Replace the previous strings dictionary (identified by the L.AppTitle key).
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Contains("L.AppTitle"))
            {
                dictionaries[i] = new ResourceDictionary { Source = uri };
                return;
            }
        }
        dictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>Resolves a localized string by key for use in code-behind/viewmodels.</summary>
    public string this[string key]
        => Application.Current.TryFindResource(key) as string ?? key;
}

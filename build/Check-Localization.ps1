$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $root "src"
$resourceFiles = @{
    ru = Join-Path $sourceRoot "IPAStudio.App/Resources/Strings.ru.xaml"
    en = Join-Path $sourceRoot "IPAStudio.App/Resources/Strings.en.xaml"
}

$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$textExtensions = @(".cs", ".xaml", ".json", ".xml", ".resx", ".props", ".csproj")
$corruptionMarkers = @([char]0xFFFD, "ï¿½", "â€™", "â€œ", "â€")

Get-ChildItem $sourceRoot -Recurse -File | Where-Object { $textExtensions -contains $_.Extension.ToLowerInvariant() } | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    try { $text = $utf8Strict.GetString($bytes) }
    catch { throw "Invalid UTF-8: $($_.FullName)" }

    foreach ($marker in $corruptionMarkers) {
        if ($text.Contains($marker)) { throw "Broken text marker '$marker' in $($_.FullName)" }
    }
}

$resources = @{}
foreach ($language in $resourceFiles.Keys) {
    [xml]$xml = [System.IO.File]::ReadAllText($resourceFiles[$language], $utf8Strict)
    $entries = @{}
    foreach ($node in $xml.ResourceDictionary.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $key = $node.GetAttribute("Key", "http://schemas.microsoft.com/winfx/2006/xaml")
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        if ($entries.ContainsKey($key)) { throw "Duplicate localization key '$key' in $language" }
        if ($node.InnerText -match '^\s*\?+\s*$') { throw "Question-mark placeholder in ${language}:$key" }
        $entries[$key] = $node.InnerText
    }
    $resources[$language] = $entries
}

foreach ($key in $resources.ru.Keys) {
    if (-not $resources.en.ContainsKey($key)) { throw "English localization is missing '$key'" }
}
foreach ($key in $resources.en.Keys) {
    if (-not $resources.ru.ContainsKey($key)) { throw "Russian localization is missing '$key'" }
}

$referencePattern = '(?:Loc\.(?:Get|Format)\(\s*"|(?:Dynamic|Static)Resource\s+)(L\.[A-Za-z0-9_.-]+)'
Get-ChildItem $sourceRoot -Recurse -File | Where-Object { $_.Extension -in @(".cs", ".xaml") } | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName, $utf8Strict)
    foreach ($match in [regex]::Matches($text, $referencePattern)) {
        $key = $match.Groups[1].Value
        foreach ($language in $resourceFiles.Keys) {
            if (-not $resources[$language].ContainsKey($key)) {
                throw "$language localization is missing referenced key '$key' in $($_.FullName)"
            }
        }
    }
}

Write-Host "Localization OK: $($resources.ru.Count) matching RU/EN keys; UTF-8 and references are valid."

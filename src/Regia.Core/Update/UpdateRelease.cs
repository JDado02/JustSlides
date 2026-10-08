using System.Text.Json;
using System.Text.RegularExpressions;

namespace Regia.Core.Update;

/// <summary>La release più recente su GitHub: versione, note, file di installazione e SHA256 atteso. Logica pura (nessuna rete).</summary>
public sealed record UpdateRelease(
    ReleaseVersion Version,
    string Tag,
    string Notes,
    string AssetName,
    string DownloadUrl,
    long Size,
    string? Sha256)
{
    /// <summary>Nome del file di installazione pubblicato nelle release.</summary>
    public const string SetupAssetName = "JustSlides-Setup.exe";

    private static readonly Regex ShaRegex = new(@"SHA-?256\s*[:=]?\s*([0-9A-Fa-f]{64})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>L'SHA256 scritto nelle note della release ("SHA256: ..."), o null se manca.</summary>
    public static string? ParseSha256(string? notes)
    {
        if (string.IsNullOrEmpty(notes))
            return null;

        var match = ShaRegex.Match(notes);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Legge la risposta di <c>releases/latest</c> dell'API di GitHub. Null se la risposta non è una release utilizzabile
    /// (bozza, pre-release, tag illeggibile, nessun file di installazione).
    /// </summary>
    public static UpdateRelease? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (Flag(root, "draft") || Flag(root, "prerelease"))
                return null;

            var tag = Text(root, "tag_name");
            if (!ReleaseVersion.TryParse(tag, out var version))
                return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(Text(asset, "name"), SetupAssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = Text(asset, "browser_download_url");
                if (string.IsNullOrEmpty(url))
                    return null;

                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var notes = Text(root, "body") ?? "";
                return new UpdateRelease(version, tag!, notes, SetupAssetName, url, size, ParseSha256(notes));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

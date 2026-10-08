using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TwoTicks.Core;

/// <summary>A newer release on GitHub and its setup program.</summary>
/// <param name="Sha256">The setup program's hash as GitHub lists it, in lowercase hex; null when GitHub has none.</param>
public sealed record UpdateInfo(Version Version, string SetupUrl, long SetupSize, string? Sha256, string PageUrl);

/// <summary>
/// Finds new versions among the releases on GitHub, which docs/releasing.md
/// describes, and downloads their setup program.
/// </summary>
public static class Updates
{
    public const string Repository = "rathlinus/TwoTicks";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    private static string LatestRelease => $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>A client with the headers GitHub's API asks for.</summary>
    public static HttpClient CreateClient(Version current)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TwoTicks", Normalize(current).ToString()));
        return http;
    }

    /// <summary>The latest release if it is newer than <paramref name="current"/> and has a setup program; otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellation), current);
    }

    /// <summary>Reads GitHub's description of a release.</summary>
    internal static UpdateInfo? Parse(string json, Version current)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement release = document.RootElement;
        if (Flag(release, "draft") || Flag(release, "prerelease"))
        {
            return null;
        }
        string tag = Text(release, "tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version) || Normalize(version) <= Normalize(current))
        {
            return null;
        }
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = Text(asset, "name");
            string? url = Text(asset, "browser_download_url");
            if (name is null || url is null || !name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
            string? digest = Text(asset, "digest");
            string? sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].ToLowerInvariant()
                : null;
            return new UpdateInfo(Normalize(version), url, size, sha256, Text(release, "html_url") ?? ReleasesPage);
        }
        return null;
    }

    /// <summary>
    /// Downloads the setup program into <paramref name="folder"/> and returns its
    /// path. A file of the wrong size or hash is deleted and reported as an error.
    /// </summary>
    /// <param name="progress">Gets how much of the file is there, from 0 to 1.</param>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo update, string folder, IProgress<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"TwoTicks-{update.Version}-Setup.exe");
        string partial = path + ".part";
        try
        {
            using (HttpResponseMessage response = await http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                long total = update.SetupSize > 0 ? update.SetupSize : response.Content.Headers.ContentLength ?? 0;
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellation);
                await using FileStream target = File.Create(partial);
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(1, (double)received / total));
                    }
                }
            }

            long size = new FileInfo(partial).Length;
            if (update.SetupSize > 0 && size != update.SetupSize)
            {
                throw new InvalidDataException(Loc.T("update.wrongSize", ("size", size), ("expected", update.SetupSize)));
            }
            if (update.Sha256 is not null)
            {
                string hash;
                await using (FileStream file = File.OpenRead(partial))
                {
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
                }
                if (hash != update.Sha256)
                {
                    throw new InvalidDataException(Loc.T("update.wrongHash"));
                }
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>1.2.0 and 1.2.0.0 are the same version; <see cref="Version"/> would call the second newer.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

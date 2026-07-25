using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Services;

/// <summary>
/// Thin wrapper around the bundled yt-dlp executable: reads the video metadata and downloads
/// the best audio-only stream to a local cache file.
/// </summary>
public sealed class YoutubeDownloader(ILogger<YoutubeDownloader> logger)
{
    // Queued tracks prefetch their audio as soon as they are enqueued; one download at a time
    // keeps a burst of /play commands from starving the audio encoder on small hosts.
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    /// <summary>Reads title/duration/thumbnail without downloading anything.</summary>
    public async Task<TrackInfo?> GetTrackInfoAsync(string url, string requestedBy, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            ["--dump-single-json", "--no-playlist", "--skip-download", "--no-warnings", url], ct);

        if (exitCode != 0 || stdout.Length == 0)
        {
            logger.LogWarning("yt-dlp metadata lookup failed for {Url}: {Error}", url, stderr);
            return null;
        }

        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;

        // A playlist url with --no-playlist still returns an entries array in some cases.
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            if (entries.GetArrayLength() == 0)
                return null;
            root = entries[0];
        }

        var id = root.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
        var webpage = root.TryGetProperty("webpage_url", out var w) ? w.GetString() ?? url : url;
        var thumbnail = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;

        TimeSpan? duration = null;
        if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
            duration = TimeSpan.FromSeconds(d.GetDouble());

        return new TrackInfo(id, title, webpage, duration, thumbnail, requestedBy);
    }

    /// <summary>
    /// Downloads the audio of a track and returns the local file path. Already downloaded
    /// tracks are served straight from the cache directory.
    /// </summary>
    public async Task<string> DownloadAudioAsync(TrackInfo track, CancellationToken ct)
    {
        Directory.CreateDirectory(BundledTools.CacheDirectory);

        if (FindCached(track.Id) is { } cached)
        {
            logger.LogInformation("Using cached audio for {Title}", track.Title);
            return cached;
        }

        await _downloadGate.WaitAsync(ct);
        try
        {
            // The same track may have been queued twice and downloaded while we waited.
            if (FindCached(track.Id) is { } nowCached)
                return nowCached;

            var template = Path.Combine(BundledTools.CacheDirectory, "%(id)s.%(ext)s");
            logger.LogInformation("Downloading audio for {Title}", track.Title);

            var (exitCode, _, stderr) = await RunAsync(
            [
                "--no-playlist",
                "--no-warnings",
                "--no-part",
                "--format", "bestaudio[ext=m4a]/bestaudio/best",
                "--output", template,
                track.Url
            ], ct);

            if (exitCode != 0)
                throw new InvalidOperationException($"yt-dlp failed to download '{track.Title}': {stderr}");

            return Directory.EnumerateFiles(BundledTools.CacheDirectory, track.Id + ".*").FirstOrDefault()
                   ?? throw new InvalidOperationException($"yt-dlp reported success but no file was written for '{track.Title}'.");
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static string? FindCached(string id) =>
        Directory
            .EnumerateFiles(BundledTools.CacheDirectory, id + ".*")
            .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(BundledTools.YtDlp)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException("Could not start yt-dlp.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process is already gone, nothing to clean up.
        }
    }
}

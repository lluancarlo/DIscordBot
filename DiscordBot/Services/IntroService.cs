using Microsoft.Extensions.Logging;

namespace DiscordBot.Services;

/// <summary>
/// Scans the "intro" folder next to the executable once at startup. When it contains at least one
/// audio file the intro feature is enabled and the bot plays a random one every time it joins a
/// voice channel, before the requested track.
/// </summary>
public sealed class IntroService
{
    private static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".flac", ".webm", ".wma"];

    private readonly string[] _files;

    public IntroService(ILogger<IntroService> logger)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "intro");

        _files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray()
            : [];

        if (Enabled)
            logger.LogInformation("Intro enabled: {Count} audio file(s) found in {Folder}", _files.Length, folder);
        else
            logger.LogInformation("Intro disabled: no audio files found in {Folder}", folder);
    }

    /// <summary>True when the intro folder contained at least one audio file at startup.</summary>
    public bool Enabled => _files.Length > 0;

    /// <summary>A random intro file, or <c>null</c> when the feature is disabled.</summary>
    public string? PickRandomIntro() =>
        Enabled ? _files[Random.Shared.Next(_files.Length)] : null;
}

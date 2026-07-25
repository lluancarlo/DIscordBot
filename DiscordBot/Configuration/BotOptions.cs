namespace DiscordBot.Configuration;

/// <summary>
/// Settings bound from the "Discord" section of appsettings.json.
/// </summary>
public sealed class BotOptions
{
    public const string SectionName = "Discord";

    /// <summary>Bot token. Can also be supplied through the DISCORD_TOKEN environment variable.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Optional guild to register the slash commands in. When 0 the commands are registered in
    /// every guild the bot is a member of, which makes them show up instantly.
    /// </summary>
    public ulong GuildId { get; set; }

    /// <summary>
    /// How long the bot waits in the voice channel for a new /play, in seconds, after the last
    /// track finished and the queue is empty. When nothing is queued in time it leaves. 0 makes
    /// it stay until /quit or the alone timeout kicks in.
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How long the bot may be alone (no non-bot users) in a voice channel, in seconds, before it
    /// disconnects automatically, like /quit. 0 disables the check.
    /// </summary>
    public int AloneTimeoutSeconds { get; set; } = 10;
}

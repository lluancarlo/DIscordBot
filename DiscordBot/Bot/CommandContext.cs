using Discord;
using Discord.WebSocket;

namespace DiscordBot.Bot;

/// <summary>
/// Everything a command needs from an interaction, without handing it the raw Discord.Net API:
/// who ran it, the options they filled in and the ways to reply.
/// </summary>
public sealed class CommandContext(SocketSlashCommand interaction)
{
    /// <summary>The caller as a guild member, or <c>null</c> when the command was run outside a guild.</summary>
    public SocketGuildUser? User => interaction.User as SocketGuildUser;

    /// <summary>
    /// Whether Discord would still accept a reply. Mirrors the window Discord.Net enforces locally
    /// before it sends anything: three seconds for the first response, fifteen minutes for the
    /// follow ups that a successful defer unlocks.
    /// </summary>
    public bool CanRespond => DateTimeOffset.UtcNow - interaction.CreatedAt <
        (interaction.HasResponded ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(3));

    /// <summary>Reads a string option by the name used in the command definition.</summary>
    public string GetString(string name) =>
        interaction.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as string ?? string.Empty;

    /// <summary>
    /// Tells Discord the reply is on its way. Needed by anything slower than the three second
    /// window Discord gives an interaction, such as looking a video up with yt-dlp.
    /// </summary>
    public Task DeferAsync() => interaction.DeferAsync();

    /// <summary>Sends the command's result, whether or not the reply was deferred first.</summary>
    public Task RespondAsync(Embed embed) => interaction.HasResponded
        ? interaction.FollowupAsync(embed: embed)
        : interaction.RespondAsync(embed: embed);

    /// <summary>Sends a failure message only the caller can see.</summary>
    public Task ErrorAsync(string message) => interaction.HasResponded
        ? interaction.FollowupAsync(message, ephemeral: true)
        : interaction.RespondAsync(message, ephemeral: true);
}

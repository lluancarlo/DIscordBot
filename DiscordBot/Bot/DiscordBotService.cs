using Discord;
using Discord.WebSocket;
using DiscordBot.Configuration;
using DiscordBot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Bot;

/// <summary>
/// Owns the connection to Discord: logs in, wires the gateway events to the
/// <see cref="CommandRegistry"/> and shuts everything down again. The commands themselves live
/// in the Commands folder, the playback logic in the Services folder.
/// </summary>
public sealed class DiscordBotService(
    DiscordSocketClient client,
    CommandRegistry registry,
    MusicService music,
    IOptions<BotOptions> options,
    ILogger<DiscordBotService> logger) : IHostedService
{
    private readonly BotOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        BundledTools.EnsureAvailable();

        var token = _options.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "No bot token configured. Set Discord:Token in appsettings.Development.json " +
                "or the DISCORD_TOKEN environment variable.");
        }

        client.Log += OnLogAsync;
        // Command registration talks to the REST API, so keep it off the gateway task as well.
        client.Ready += () => { _ = Task.Run(OnReadyAsync); return Task.CompletedTask; };
        client.JoinedGuild += guild => { _ = Task.Run(() => registry.RegisterAsync(guild)); return Task.CompletedTask; };
        client.SlashCommandExecuted += registry.Dispatch;
        // Detects the bot being left alone in a voice channel; runs off the gateway task because
        // leaving waits for the playback loop to unwind.
        client.UserVoiceStateUpdated += (user, before, after) =>
        {
            _ = Task.Run(() => music.HandleVoiceStateUpdatedAsync(user, before, after));
            return Task.CompletedTask;
        };

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await music.DisposeAsync();
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private async Task OnReadyAsync()
    {
        logger.LogInformation("Logged in as {User}", client.CurrentUser);

        var guilds = _options.GuildId != 0
            ? [client.GetGuild(_options.GuildId)]
            : client.Guilds.ToArray();

        foreach (var guild in guilds.Where(g => g is not null))
            await registry.RegisterAsync(guild);
    }

    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace
        };

        logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}

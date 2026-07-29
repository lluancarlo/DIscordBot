using System.Diagnostics;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Bot;

/// <summary>
/// Knows every <see cref="ISlashCommand"/> the bot has: publishes them to Discord and routes
/// incoming interactions to the right one.
/// </summary>
public sealed class CommandRegistry
{
    /// <summary>
    /// Discord gives an interaction three seconds to be answered. Anything slower than this on the
    /// way to the handler leaves no room for the reply itself, so it gets logged.
    /// </summary>
    private static readonly TimeSpan SlowDispatchThreshold = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, ISlashCommand> _commands;
    private readonly ILogger<CommandRegistry> _logger;

    public CommandRegistry(IEnumerable<ISlashCommand> commands, ILogger<CommandRegistry> logger)
    {
        _logger = logger;
        _commands = [];

        foreach (var command in commands)
        {
            if (!_commands.TryAdd(command.Name, command))
            {
                throw new InvalidOperationException(
                    $"Two commands are called '{command.Name}': {_commands[command.Name].GetType().Name} and {command.GetType().Name}.");
            }
        }

        _logger.LogInformation("Loaded {Count} commands: {Names}", _commands.Count, string.Join(", ", _commands.Keys));
    }

    /// <summary>
    /// Publishes the commands to a single guild. Guild commands show up immediately, while global
    /// ones can take up to an hour, which makes this the better fit during development.
    /// </summary>
    public async Task RegisterAsync(SocketGuild guild)
    {
        var definitions = _commands.Values
            .Select(command => (ApplicationCommandProperties)command.Build())
            .ToArray();

        try
        {
            await guild.BulkOverwriteApplicationCommandAsync(definitions);
            _logger.LogInformation("Registered {Count} commands in guild {Guild}", definitions.Length, guild.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not register commands in guild {Guild}", guild.Name);
        }
    }

    /// <summary>
    /// Hands the interaction off to the thread pool and returns straight away.
    /// Discord.Net raises events on the gateway task, and connecting to voice needs that task free
    /// to receive the voice server handshake: running a command inline stalls it until it times out.
    /// </summary>
    public Task Dispatch(SocketSlashCommand interaction)
    {
        // Discord.Net decides an interaction has expired by comparing this machine's clock against
        // the timestamp baked into the interaction's id, so the age measured here is only as
        // trustworthy as the local clock: a host running ahead of real time reports interactions as
        // seconds old the moment they arrive. Measuring on arrival and again once the handler
        // actually starts separates that (and a slow gateway) from a starved thread pool.
        var ageOnArrival = DateTimeOffset.UtcNow - interaction.CreatedAt;
        var arrivedAt = Stopwatch.GetTimestamp();

        _ = Task.Run(() => ExecuteAsync(interaction, ageOnArrival, arrivedAt));
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(SocketSlashCommand interaction, TimeSpan ageOnArrival, long arrivedAt)
    {
        var context = new CommandContext(interaction);
        var queueDelay = Stopwatch.GetElapsedTime(arrivedAt);

        if (ageOnArrival + queueDelay >= SlowDispatchThreshold)
        {
            _logger.LogWarning(
                "/{Command} reached the handler {Total:0.00}s after Discord created it " +
                "({Arrival:0.00}s before the gateway event, {Queue:0.00}s waiting for the thread pool). " +
                "Discord rejects anything past 3.00s; if the arrival share is the large one, check that " +
                "the host clock is synchronised (a clock running ahead fails every command).",
                interaction.Data.Name, (ageOnArrival + queueDelay).TotalSeconds,
                ageOnArrival.TotalSeconds, queueDelay.TotalSeconds);
        }

        if (!_commands.TryGetValue(interaction.Data.Name, out var command))
        {
            _logger.LogWarning("Received unknown command /{Command}", interaction.Data.Name);
            await TryReplyAsync(context, "Unknown command.", interaction.Data.Name);
            return;
        }

        try
        {
            await command.ExecuteAsync(context);
        }
        catch (TimeoutException ex) when (!context.CanRespond)
        {
            // The three second window closed before the command got a word in. Discord has already
            // shown the user "the application did not respond" and rejects any further reply, so
            // sending one would only add a second, identical stack trace to the log.
            _logger.LogError(ex,
                "/{Command} expired before it could answer ({Age:0.00}s old, Discord allows 3.00s)",
                interaction.Data.Name, (DateTimeOffset.UtcNow - interaction.CreatedAt).TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command /{Command} failed", interaction.Data.Name);
            await TryReplyAsync(context, "Something went wrong while running that command.", interaction.Data.Name);
        }
    }

    /// <summary>
    /// Sends a failure message without ever throwing. Replying can fail for the same reason the
    /// command did - an interaction that already ran out of Discord's three second window rejects
    /// the error message too - and nobody awaits <see cref="ExecuteAsync"/>, so an exception here
    /// would be lost instead of logged.
    /// </summary>
    private async Task TryReplyAsync(CommandContext context, string message, string commandName)
    {
        try
        {
            await context.ErrorAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not reply to /{Command}", commandName);
        }
    }
}

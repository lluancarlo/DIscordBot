# DiscordBot

A Discord music bot written in C# (.NET 10). It plays audio from YouTube links in a voice channel using bundled **yt-dlp** and **ffmpeg** — nothing needs to be installed globally.

## Commands

| Command | Description |
|---|---|
| `/play <link>` | Play a YouTube link, or queue it if something is already playing. |
| `/pause` | Pause the current track; run again to resume. |
| `/stop` | Stop playback, clear the queue and leave the voice channel. |
| `/list` | Show the current track and the queue. |
| `/clear` | Clear the queue but keep the current track playing. |
| `/quit` | Force the bot to leave the voice channel. |

You must be in a voice channel to use a command — the same one as the bot if it's already playing.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Discord bot token ([Discord Developer Portal](https://discord.com/developers/applications)) with the **Guilds** and **Guild Voice States** gateway intents
- Internet connection for the first build (it downloads ffmpeg and yt-dlp automatically)

## Getting started

1. Set your token in `DiscordBot/appsettings.json` (or via the `DISCORD_TOKEN` environment variable):

   ```json
   {
     "Discord": {
       "Token": "YOUR_BOT_TOKEN_HERE",
       "GuildId": 0
     }
   }
   ```

2. Build and run:

   ```
   dotnet run --project DiscordBot
   ```

3. Invite the bot to your server with the `applications.commands` and `bot` scopes (with *Connect* and *Speak* permissions), join a voice channel and run `/play`.

## Running in Docker

The image is available for `linux/amd64` and `linux/arm64` (64-bit ARM build only — the native voice libraries ship no 32-bit ARM build). Inside the container, ffmpeg and yt-dlp are installed as Linux system tools; the `.exe` bundling only happens on Windows.

### Using the prebuilt package

Every push to `master` publishes a multi-arch image to GitHub Container Registry as [`ghcr.io/lluancarlo/discordbot`](https://ghcr.io/lluancarlo/discordbot):

```
docker pull ghcr.io/lluancarlo/discordbot:latest
docker run -d --name discordbot \
  -e DISCORD_TOKEN=your_bot_token \
  --restart unless-stopped \
  ghcr.io/lluancarlo/discordbot:latest
```

Docker picks the right architecture (amd64/arm64) automatically. To use your own intro sounds (played when the bot joins a voice channel) without rebuilding the image, mount a folder with audio files over the bundled one: `-v /path/to/intro:/app/intro`. The folder is scanned once at startup.

### Building the image yourself

```
docker build -t discordbot .
docker run -d --name discordbot -e DISCORD_TOKEN=your_bot_token --restart unless-stopped discordbot
```

Or with compose (reads `DISCORD_TOKEN` from the host environment or an `.env` file):

```
docker compose up -d --build
```

## Troubleshooting

- **`Bundled tool 'ffmpeg.exe' was not found`** — run `dotnet build` once with an internet connection.
- **Close code 4017** — `libdave.dll` must sit next to the executable (handled by the project file).
- **Commands don't appear in Discord** — check the log for `Registered ... commands in guild` and re-invite the bot with the `applications.commands` scope.

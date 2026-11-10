# Olivia Bot

A little pink desktop chat app where you can text a bot that texts like Olivia. It's a bot, not the real her:
the real Olivia can't see what you send it.

## Download and open it (Windows)

1. Go to [Releases](../../releases/latest) and download **OliviaBot.zip**.
2. Unzip it anywhere.
3. Double-click **Olivia Bot.cmd**.

If Windows says "Windows protected your PC", click **More info → Run anyway** (the app isn't code-signed).

## What you need

- **Claude Code**, installed and signed in on your computer. The bot uses your own Claude login to write
  its replies.
- The **.NET 8 runtime** (most dev machines already have it).

## Privacy

The bot only sees your chat with it and `persona.md`. It can't see your files or anything else on your
computer. Chats are saved only on your computer. GIFs come from GIPHY, G-rated only.

## Building from source

It's a Godot 4.7 (.NET) project. Open the folder in Godot 4.7.2 mono and run it, or `dotnet build` and launch
with `--path`. For GIFs, put a GIPHY API key in `giphy-key.txt` next to `persona.md`.

## Layout

| Path | Purpose |
|---|---|
| `Scripts/` | The app: chat UI, bubbles and icons, Claude and GIPHY calls |
| `persona.md` | The bot's personality: voice rules, example texts, boundaries |
| `portfolio.yaml`, `docs/portfolio/` | Portfolio write-up |
| `media/` | Screenshots of the app |

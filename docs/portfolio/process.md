<!-- PROCESS: how it works and why you built it this way. 3–6 short paragraphs.
     - Overview: the main pieces and how they connect (link media/architecture.png)
     - Key decisions: what you chose, what you rejected, and WHY
     - Mobile/web: client ↔ API ↔ data flow, auth, offline, deployment
     - AI: model choice, prompting/RAG/agent design, how you measured quality
     - Embedded/IoT: board choice, sensors, power budget, device ↔ cloud ↔ app flow -->

## How it works

The app is a Godot 4.7 project written in C#. The whole UI is drawn in code: the pink gradient and twinkling sparkles, iMessage-style bubbles with curled tails, tapback badges, a typing indicator and a sidebar of saved chats. There are no image assets besides the app icon.

When a friend sends a message, the app runs Claude Code headless on their computer (`claude -p`) with my persona file as the system prompt and the recent chat as input. The model answers with a JSON array of one to three short texts, and the app shows them one at a time with a typing delay sized to each message. A text can also be a special command instead of a message:

- `REACT: ❤️` puts a tapback on the friend's last message.
- `GIF: happy dance` searches GIPHY (G-rated) and sends the result.
- `NAME: Millie` tells the app who it's talking to, so it can remember them. The friend never sees it.

When a friend sends a photo, it's copied alone into an empty temp folder and the model gets only the Read tool there, so it can react to what's actually in the picture without seeing anything else.

Chats are saved as JSON on the friend's computer and get a short lowercase title once the bot has replied, like Claude's sidebar.

## Key decisions

- **Claude Code instead of an API key:** each friend uses their own Claude login, so there's no key to ship or server to pay for. The tradeoff is that friends need Claude Code installed.
- **No tools, no MCP servers:** the model runs with tools and MCP servers off, so it can only see the chat and the persona. Even if someone asks it to, it can't read files.
- **The personality is one Markdown file:** `persona.md` holds the voice rules, real example texts, GIF etiquette and boundaries, so tuning the bot means editing text, not code.
- **Godot for a desktop app:** it gave me a custom-looking window, smooth animation and a one-folder Windows build without a web stack. Godot has no GIF loader, so GIFs are decoded into frames with System.Drawing.
- **The friend texts first:** new chats start empty, and the bot learns who it's talking to from the conversation instead of a sign-up screen.

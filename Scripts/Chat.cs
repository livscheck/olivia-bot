using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// Gets the Olivia bot's next texts from headless Claude Code ("claude -p") on whoever's computer runs the app.
// The bot has no MCP servers and no tools, so it can't reach files, mail, Teams or the web. The one exception: when
// the friend just sent a photo, that photo alone is copied into an empty temp folder, Claude runs there with only
// the Read tool, and it can look at that picture (reads outside its folder are refused in -p mode).
public static class Chat
{
    public sealed record Line(string Who, string Text)
    {
        /// <summary>A tapback on this message ("❤️", "😂"…), from the other person. Null when none.</summary>
        public string? React { get; set; }
    }

    /// <summary>One saved conversation, like a chat in Claude's sidebar.</summary>
    public sealed class Conversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Title { get; set; } = "new chat";
        public bool Titled { get; set; }
        public long Updated { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public List<Line> Lines { get; set; } = new();
    }

    /// <summary>A short lowercase title for a chat, in Olivia's style. Empty on any failure.</summary>
    public static async Task<string> Title(IReadOnlyList<Line> lines)
    {
        string? exe = FindClaude();
        if (exe == null) return "";
        var sb = new StringBuilder("Give this text conversation a 2 to 5 word title, all lowercase, casual, no quotes, no punctuation at the end. Output only the title.\n\n");
        foreach (var l in lines.Take(8)) sb.AppendLine(Describe(l));
        var (ok, text) = await RunPlain(exe, sb.ToString(), null);
        if (!ok) return "";
        string t = text.Trim().Trim('"', '\'', '.').Split('\n')[0].Trim().ToLowerInvariant();
        return t.Length is > 0 and <= 40 ? t : "";
    }

    static async Task<(bool ok, string text)> RunPlain(string exe, string prompt, string? systemFile, string? photo = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        string? photoDir = null;
        if (photo != null && File.Exists(photo))
        {
            photoDir = Path.Combine(Path.GetTempPath(), "oliviabot-photo-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(photoDir);
            File.Copy(photo, Path.Combine(photoDir, "photo" + Path.GetExtension(photo).ToLowerInvariant()));
            psi.WorkingDirectory = photoDir;
        }
        foreach (var a in new[] { "-p", "--output-format", "json", "--tools", photoDir != null ? "Read" : "", "--strict-mcp-config", "--no-session-persistence" })
            psi.ArgumentList.Add(a);
        if (systemFile != null) { psi.ArgumentList.Add("--system-prompt-file"); psi.ArgumentList.Add(systemFile); }
        try
        {
            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteAsync(prompt);
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAny(Task.Run(() => p.WaitForExit()), Task.Delay(TimeSpan.FromMinutes(2)));
            if (!p.HasExited) { try { p.Kill(true); } catch { } return (false, "took too long, try again"); }
            string stdout = await outTask, stderr = await errTask;
            using var doc = JsonDocument.Parse(stdout);
            var r = doc.RootElement;
            string result = r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "";
            if (r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                return (false, result != "" ? result : stderr.Trim());
            return (true, result);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { if (photoDir != null) try { Directory.Delete(photoDir, true); } catch { } }
    }

    public static string? FindClaude()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>A saved GIF message is stored as "GIF: search words|url"; a photo as "IMG: path".</summary>
    public static bool IsGif(string text) => text.StartsWith("GIF:", StringComparison.Ordinal);
    public static (string query, string url) GifParts(string text)
    {
        string body = text[4..].Trim();
        int bar = body.LastIndexOf('|');
        return bar < 0 ? (body, "") : (body[..bar].Trim(), body[(bar + 1)..].Trim());
    }
    public static bool IsImage(string text) => text.StartsWith("IMG:", StringComparison.Ordinal);
    public static string ImagePath(string text) => text[4..].Trim();

    /// <summary>The bot telling the app who it's talking to: "NAME: Millie". Never shown in the chat.</summary>
    public static bool IsName(string text) => text.StartsWith("NAME:", StringComparison.Ordinal);
    public static string NameValue(string text) => text[5..].Trim();

    /// <summary>The bot reacting to the friend's last message: "REACT: ❤️".</summary>
    public static bool IsReact(string text) => text.StartsWith("REACT:", StringComparison.Ordinal) && ReactEmoji(text) != null;
    public static string? ReactEmoji(string text)
    {
        string e = text[6..].Trim();
        return Main.Tapbacks.FirstOrDefault(t => t == e || t.TrimEnd('️') == e.TrimEnd('️'));
    }

    static string Describe(Line l)
    {
        string s = IsGif(l.Text) ? $"{l.Who}: (sent a gif: {GifParts(l.Text).query})"
            : IsImage(l.Text) ? $"{l.Who}: (sent a photo)"
            : $"{l.Who}: {l.Text}";
        if (!string.IsNullOrEmpty(l.React)) s += l.Who == "Olivia" ? $"   [they reacted {l.React}]" : $"   [Olivia reacted {l.React}]";
        return s;
    }

    /// <summary>The bot's next 1–3 texts, or a single "error:" line.</summary>

    public static async Task<List<string>> Reply(string personaFile, string friendName, IReadOnlyList<Line> history, bool gifs = false)
    {
        string? exe = FindClaude();
        if (exe == null) return new() { "error: Claude Code isn't installed on this computer" };
        if (!File.Exists(personaFile)) return new() { "error: persona.md is missing next to the app" };

        var sb = new StringBuilder();
        sb.AppendLine($"You're texting with {friendName}. Conversation so far, oldest first (keep only the last part in mind if it's long):");
        foreach (var l in history.TakeLast(40)) sb.AppendLine(Describe(l));
        int sinceGif = history.Reverse().TakeWhile(l => !IsGif(l.Text)).Count(l => l.Who == "Olivia");
        sb.AppendLine();
        sb.AppendLine("Write Olivia's next reply. Output ONLY a JSON array of 1 to 3 short strings, one per text message, nothing else. " +
                      "Get it right the first time: no notes, corrections or second versions outside the array.");
        if (friendName.StartsWith("a friend whose name", StringComparison.Ordinal))
            sb.AppendLine("You don't know who this is yet. Answer what they said, and casually find out who it is (\"wait who is this lol\") unless they already said. " +
                          "As soon as you know their first name, add a separate string exactly \"NAME: <first name>\" (it isn't shown to them).");
        sb.AppendLine("Like iMessage tapbacks, you can also react to their last message by making one of the strings exactly \"REACT: <emoji>\" with one of " +
                      string.Join(" ", Main.Tapbacks) + ". Do it now and then when it's natural (❤️ for something sweet, 😂 for something funny, ‼️ for big news); a reaction can come with texts or, for a simple message, instead of them.");
        if (gifs)
        {
            sb.AppendLine("You can send a GIF as one of the strings, written exactly as \"GIF: <2 to 5 search words>\" (or \"GIF: trending\" for whatever's trending).");
            bool recent = history.Any(l => IsGif(l.Text) && l.Who == "Olivia") && sinceGif < 6;
            sb.AppendLine("Follow the GIF rules in your persona. " + (recent
                ? "You sent one recently, so no GIF this time."
                : "Only add one if it really fits this moment; most replies have none."));
        }

        // If their newest message is a photo, let the bot look at it.
        var lastFriend = history.LastOrDefault(l => l.Who != "Olivia");
        string? photo = lastFriend != null && IsImage(lastFriend.Text) && history[^1] == lastFriend ? ImagePath(lastFriend.Text) : null;
        if (photo != null)
            sb.AppendLine("They just sent a photo. Look at it with the Read tool (file \"photo" + Path.GetExtension(photo).ToLowerInvariant() + "\" in the current folder) and react to what's actually in it, like a friend would. Don't read any other file.");

        var (ok, text) = await RunPlain(exe, sb.ToString(), personaFile, photo);
        return ok ? Parse(text) : new() { "error: " + text };
    }

    static List<string> Parse(string text)
    {
        // Use the LAST complete JSON array: if the model second-guessed itself ("wait, let me fix that: [...]"),
        // the final array is the one it meant, and nothing around it is ever shown.
        for (int b = text.LastIndexOf(']'); b > 0; b = text.LastIndexOf(']', b - 1))
            for (int a = text.LastIndexOf('[', b); a >= 0; a = a > 0 ? text.LastIndexOf('[', a - 1) : -1)
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<List<string>>(text[a..(b + 1)]);
                    if (arr != null && arr.Count > 0) return arr.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
                }
                catch { }
            }
        // No array at all: plain lines, minus anything that looks like a note about the reply.
        return text.Split('\n').Select(s => s.Trim())
            .Where(s => s != "" && !s.Contains("persona", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("Correction", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
    }
}

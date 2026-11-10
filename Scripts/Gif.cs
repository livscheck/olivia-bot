using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

// GIFs for the bot, from GIPHY (G-rated only). The bot asks for one with a few search words; this finds one,
// caches the file on this computer, and decodes it into frames (Godot can't play GIFs itself).
//
// Needs a GIPHY API key in giphy-key.txt next to persona.md. Without it the bot just doesn't send GIFs.
public static class Gif
{
    public sealed record Frames(List<ImageTexture> Textures, List<float> Delays, Vector2I Size);

    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Random Rng = new();
    const string CacheDir = "user://gifs";

    public static string KeyFile(string personaFile) => Path.Combine(Path.GetDirectoryName(personaFile) ?? "", "giphy-key.txt");

    public static string? Key(string personaFile)
    {
        try
        {
            var f = KeyFile(personaFile);
            if (!File.Exists(f)) return null;
            string k = File.ReadAllText(f).Trim();
            return k.Length >= 16 && !k.Contains(' ') ? k : null;
        }
        catch { return null; }
    }

    /// <summary>The URL of a G-rated GIF for the search words ("trending" gets a trending one), or null.</summary>
    public static async Task<string?> Find(string key, string query)
    {
        try
        {
            bool trending = query.Trim().Equals("trending", StringComparison.OrdinalIgnoreCase);
            string url = trending
                ? $"https://api.giphy.com/v1/gifs/trending?api_key={key}&limit=25&rating=g"
                : $"https://api.giphy.com/v1/gifs/search?api_key={key}&q={Uri.EscapeDataString(query)}&limit=8&rating=g&lang=en";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
            if (items.Count == 0) return null;
            // A little randomness so the same search doesn't always give the same GIF.
            var pick = items[Rng.Next(Math.Min(items.Count, trending ? 25 : 5))];
            return pick.GetProperty("images").GetProperty("fixed_height").GetProperty("url").GetString();
        }
        catch { return null; }
    }

    /// <summary>Up to n G-rated results for the picker as (small preview url, full url).</summary>
    public static async Task<List<(string preview, string full)>> Search(string key, string query, int n)
    {
        var list = new List<(string, string)>();
        try
        {
            bool trending = query.Trim().Equals("trending", StringComparison.OrdinalIgnoreCase);
            string url = trending
                ? $"https://api.giphy.com/v1/gifs/trending?api_key={key}&limit={n}&rating=g"
                : $"https://api.giphy.com/v1/gifs/search?api_key={key}&q={Uri.EscapeDataString(query)}&limit={n}&rating=g&lang=en";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            foreach (var it in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var imgs = it.GetProperty("images");
                string full = imgs.GetProperty("fixed_height").GetProperty("url").GetString() ?? "";
                string prev = imgs.TryGetProperty("fixed_height_small", out var s) ? s.GetProperty("url").GetString() ?? full : full;
                if (full != "") list.Add((prev, full));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Downloads (or reads from cache) and decodes the GIF into frames, or null.</summary>
    public static async Task<Frames?> Load(string url)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(CacheDir));
            string name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16] + ".gif";
            string path = ProjectSettings.GlobalizePath($"{CacheDir}/{name}");
            byte[] bytes;
            if (File.Exists(path)) bytes = await File.ReadAllBytesAsync(path);
            else
            {
                if (!url.StartsWith("https://", StringComparison.Ordinal) || !new Uri(url).Host.EndsWith("giphy.com", StringComparison.OrdinalIgnoreCase)) return null;
                bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length > 8_000_000) return null;
                await File.WriteAllBytesAsync(path, bytes);
            }
            // Decoding is CPU work; keep it off the UI thread, then make textures back on it.
            var raw = await Task.Run(() => Decode(bytes));
            if (raw == null) return null;
            var tex = raw.Value.pngs.Select(png =>
            {
                var img = new Image();
                img.LoadPngFromBuffer(png);
                return ImageTexture.CreateFromImage(img);
            }).ToList();
            return new Frames(tex, raw.Value.delays, raw.Value.size);
        }
        catch { return null; }
    }

    static (List<byte[]> pngs, List<float> delays, Vector2I size)? Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var gif = System.Drawing.Image.FromStream(ms);
        var dim = new System.Drawing.Imaging.FrameDimension(gif.FrameDimensionsList[0]);
        int count = Math.Min(gif.GetFrameCount(dim), 120);
        byte[]? delayBytes = null;
        try { delayBytes = gif.GetPropertyItem(0x5100)?.Value; } catch { }

        var pngs = new List<byte[]>();
        var delays = new List<float>();
        for (int i = 0; i < count; i++)
        {
            gif.SelectActiveFrame(dim, i);
            using var frame = new System.Drawing.Bitmap(gif);
            using var o = new MemoryStream();
            frame.Save(o, System.Drawing.Imaging.ImageFormat.Png);
            pngs.Add(o.ToArray());
            int cs = delayBytes != null && delayBytes.Length >= (i + 1) * 4 ? BitConverter.ToInt32(delayBytes, i * 4) : 10;
            delays.Add(Math.Max(cs, 2) / 100f); // GIF delays are in 1/100 s; browsers treat tiny ones as 0.1 s
        }
        return pngs.Count == 0 ? null : (pngs, delays, new Vector2I(gif.Width, gif.Height));
    }
}

/// <summary>Plays decoded GIF frames.</summary>
public partial class GifPlayer : TextureRect
{
    Gif.Frames? _frames;
    int _i;
    double _t;

    public void Play(Gif.Frames f)
    {
        _frames = f;
        _i = 0; _t = 0;
        Texture = f.Textures[0];
    }

    public override void _Process(double delta)
    {
        if (_frames == null || _frames.Textures.Count < 2) return;
        _t += delta;
        while (_t >= _frames.Delays[_i])
        {
            _t -= _frames.Delays[_i];
            _i = (_i + 1) % _frames.Textures.Count;
            Texture = _frames.Textures[_i];
        }
    }
}

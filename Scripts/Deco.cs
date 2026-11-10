using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

// Hand-drawn decorations for the pink theme: twinkling sparkles, and small vector icons (heart, paper plane,
// four-point sparkle) so the app needs no image files.
public partial class Sparkles : Control
{
    struct Star { public Vector2 P; public float Size, Phase, Speed; public bool Big; }
    readonly List<Star> _stars = new();
    readonly Random _rng = new(7);
    float _t;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        for (int i = 0; i < 46; i++)
            _stars.Add(new Star
            {
                P = new Vector2((float)_rng.NextDouble(), (float)_rng.NextDouble()),
                Size = 1.5f + (float)_rng.NextDouble() * 2.5f,
                Phase = (float)(_rng.NextDouble() * Math.Tau),
                Speed = 0.6f + (float)_rng.NextDouble() * 1.4f,
                Big = i % 9 == 0,
            });
    }

    public override void _Process(double delta) { _t += (float)delta; QueueRedraw(); }

    public override void _Draw()
    {
        foreach (var s in _stars)
        {
            float a = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(_t * s.Speed + s.Phase));
            var p = new Vector2(s.P.X * Size.X, s.P.Y * Size.Y);
            var white = new Color(1, 1, 1, a);
            if (s.Big)
            {
                DrawCircle(p, s.Size * 5f, new Color(1, 1, 1, a * 0.12f));
                Icon.DrawSparkle(this, p, s.Size * 5.5f, white);
            }
            else
            {
                DrawCircle(p, s.Size * 2.2f, new Color(1, 1, 1, a * 0.15f));
                DrawCircle(p, s.Size, white);
            }
        }
    }
}

/// <summary>
/// An iMessage-style chat bubble: a flat rounded rectangle with a little curled tail on the bottom corner of the
/// last bubble in a run (left side for Olivia, right side for the friend).
/// </summary>
public partial class Bubble : Container
{
    public Color Fill = Colors.Pink;
    public bool Right;
    bool _tail = true;
    public bool Tail { get => _tail; set { _tail = value; QueueRedraw(); } }
    const float Radius = 18, PadX = 13, PadY = 8;

    // A child named "badge" is the tapback reaction. When there is one, the bubble reserves room above itself
    // (Top) so the badge sits on its corner without covering the message above, like iMessage.
    Control? Badge => GetChildren().OfType<Control>().FirstOrDefault(k => k.Name == "badge" && !k.IsQueuedForDeletion());
    float Top => Badge is { } b ? b.GetCombinedMinimumSize().Y * 0.6f + 4 : 0;

    public override Vector2 _GetMinimumSize()
    {
        var m = Vector2.Zero;
        foreach (var c in GetChildren()) if (c is Control k && k.Visible && k.Name != "badge") m = m.Max(k.GetCombinedMinimumSize());
        return m + new Vector2(PadX * 2, PadY * 2 + Top);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationChildOrderChanged) { UpdateMinimumSize(); QueueRedraw(); }
        if (what != NotificationSortChildren) return;
        float top = Top;
        foreach (var c in GetChildren())
        {
            if (c is not Control k) continue;
            if (k.Name == "badge")
            {
                var s = k.GetCombinedMinimumSize();
                FitChildInRect(k, new Rect2(Right ? -s.X * 0.45f : Size.X - s.X * 0.55f, top - s.Y * 0.6f, s.X, s.Y));
            }
            else FitChildInRect(k, new Rect2(PadX, PadY + top, Size.X - PadX * 2, Size.Y - PadY * 2 - top));
        }
    }

    public override void _Draw()
    {
        float top = Top;
        var sb = new StyleBoxFlat { BgColor = Fill, AntiAliasing = true };
        sb.SetCornerRadiusAll((int)Mathf.Min(Radius, (Size.Y - top) / 2));
        DrawStyleBox(sb, new Rect2(0, top, Size.X, Size.Y - top));
        if (!_tail) return;
        // The tail: a small curl hanging off the bottom corner, like iMessage.
        float h = Size.Y, w = Size.X;
        var pts = new List<Vector2>();
        if (!Right)
        {
            pts.Add(new Vector2(4, h - 18));
            for (int i = 0; i <= 8; i++) { float t = i / 8f; pts.Add(new Vector2(Mathf.Lerp(4, -7, t * t), Mathf.Lerp(h - 18, h, t))); }
            for (int i = 0; i <= 8; i++) { float t = i / 8f; pts.Add(new Vector2(Mathf.Lerp(-7, 14, t), Mathf.Lerp(h, h - 4, t * (2 - t)))); }
            pts.Add(new Vector2(16, h - 10));
        }
        else
        {
            pts.Add(new Vector2(w - 4, h - 18));
            for (int i = 0; i <= 8; i++) { float t = i / 8f; pts.Add(new Vector2(Mathf.Lerp(w - 4, w + 7, t * t), Mathf.Lerp(h - 18, h, t))); }
            for (int i = 0; i <= 8; i++) { float t = i / 8f; pts.Add(new Vector2(Mathf.Lerp(w + 7, w - 14, t), Mathf.Lerp(h, h - 4, t * (2 - t)))); }
            pts.Add(new Vector2(w - 16, h - 10));
        }
        DrawColoredPolygon(pts.ToArray(), Fill);
    }
}

public partial class Icon : Control
{
    public enum Kind { Heart, Plane, Sparkle, Plus, Arrow }
    public Kind Shape;
    public Color Tint = Colors.White;
    public Color? Glow;

    public static Icon Make(Kind k, float size, Color tint, Color? glow = null)
    {
        var i = new Icon { Shape = k, Tint = tint, Glow = glow, CustomMinimumSize = new Vector2(size, size), MouseFilter = MouseFilterEnum.Ignore };
        return i;
    }

    public override void _Draw()
    {
        var c = Size / 2;
        float r = Mathf.Min(Size.X, Size.Y) / 2;
        if (Glow.HasValue) DrawCircle(c, r * 1.05f, Glow.Value);
        switch (Shape)
        {
            case Kind.Heart: DrawHeart(this, c, r * 0.82f, Tint); break;
            case Kind.Sparkle: DrawSparkle(this, c, r * 0.95f, Tint); break;
            case Kind.Plane: DrawPlane(c, r * 0.8f); break;
            case Kind.Plus:
                DrawLine(c - new Vector2(r * 0.42f, 0), c + new Vector2(r * 0.42f, 0), Tint, 3f, true);
                DrawLine(c - new Vector2(0, r * 0.42f), c + new Vector2(0, r * 0.42f), Tint, 3f, true);
                break;
            case Kind.Arrow: // iMessage-style up arrow
                DrawLine(c + new Vector2(0, r * 0.45f), c - new Vector2(0, r * 0.45f), Tint, 3f, true);
                DrawPolyline(new[] { c + new Vector2(-r * 0.36f, -r * 0.08f), c - new Vector2(0, r * 0.47f), c + new Vector2(r * 0.36f, -r * 0.08f) }, Tint, 3f, true);
                break;
        }
    }

    public static void DrawHeart(CanvasItem ci, Vector2 c, float r, Color col)
    {
        var pts = new Vector2[48];
        for (int i = 0; i < pts.Length; i++)
        {
            float t = (float)(i * Math.Tau / pts.Length);
            float x = 16 * MathF.Pow(MathF.Sin(t), 3);
            float y = 13 * MathF.Cos(t) - 5 * MathF.Cos(2 * t) - 2 * MathF.Cos(3 * t) - MathF.Cos(4 * t);
            pts[i] = c + new Vector2(x, -y - 2) * (r / 17f);
        }
        ci.DrawColoredPolygon(pts, col);
        // A little shine, like a candy heart.
        ci.DrawCircle(c + new Vector2(-r * 0.38f, -r * 0.32f), r * 0.16f, new Color(1, 1, 1, 0.55f));
    }

    public static void DrawSparkle(CanvasItem ci, Vector2 c, float r, Color col)
    {
        float k = r * 0.22f;
        var pts = new[]
        {
            c + new Vector2(0, -r), c + new Vector2(k, -k), c + new Vector2(r, 0), c + new Vector2(k, k),
            c + new Vector2(0, r), c + new Vector2(-k, k), c + new Vector2(-r, 0), c + new Vector2(-k, -k),
        };
        ci.DrawColoredPolygon(pts, col);
    }

    void DrawPlane(Vector2 c, float r)
    {
        // A paper plane pointing up-right.
        var tip = c + new Vector2(r, -r);
        var left = c + new Vector2(-r, -r * 0.05f);
        var bottom = c + new Vector2(r * 0.05f, r);
        var fold = c + new Vector2(-r * 0.05f, r * 0.05f);
        DrawColoredPolygon(new[] { tip, left, fold }, Tint);
        DrawColoredPolygon(new[] { tip, fold, bottom }, Tint.Darkened(0.18f));
    }
}

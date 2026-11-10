using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;

// Olivia Bot: a text chat where friends talk to a bot that texts like Olivia. The bot's personality lives in
// persona.md next to the app. The chat is kept on the friend's computer (user://) and never sent anywhere except
// to Claude to get the next reply.
//
// Look: a glowing mauve chat panel on a pink sparkly background. Olivia's texts are a pink name with a heart and
// white text under it; the friend's texts are glowing pink-to-lilac pills; events ("millie just joined!") are
// centered lines with a sparkle.
public partial class Main : Control
{
    // ---- theme colors (change these to restyle) ----
    static readonly Color BgTop = C(247, 118, 182);
    static readonly Color BgBottom = C(255, 196, 221);
    static readonly Color Panel = C(98, 58, 72, 0.94f);
    static readonly Color PanelGlow = C(255, 150, 205, 0.45f);
    static readonly Color NamePink = C(255, 172, 216);
    static readonly Color Text = C(255, 244, 250);
    static readonly Color Muted = C(232, 186, 208);
    static readonly Color PillLeft = C(255, 168, 210);
    static readonly Color PillRight = C(222, 168, 255);
    static readonly Color PillGlow = C(255, 120, 190, 0.55f);
    static readonly Color InputBg = C(74, 42, 54);
    static readonly Color Dark = C(56, 32, 42);
    static readonly Color Hot = C(255, 128, 186);
    static readonly Color ErrorPink = C(255, 205, 120);

    static Color C(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    const string HistoryFile = "user://chat.json";
    const string NameFile = "user://name.txt";
    const float PanelMaxWidth = 480;

    Control _panelWrap = null!;
    PanelContainer _panel = null!;
    Label _title = null!;
    ScrollContainer _scroll = null!;
    VBoxContainer _thread = null!;
    LineEdit _input = null!;
    Button _sendBtn = null!;
    Control? _typing;
    readonly List<Label> _pillLabels = new();
    Shader _pillShader = null!;
    SystemFont _font = null!;

    // Saved conversations, newest first, like Claude's sidebar. A new chat isn't saved until the friend sends
    // something, so empty chats never clutter the list.
    const string ChatsDir = "user://chats";
    const float SidebarWidth = 236;
    readonly List<Chat.Conversation> _chats = new();
    Chat.Conversation _cur = new();
    List<Chat.Line> _history => _cur.Lines;
    PanelContainer _sidebar = null!;
    VBoxContainer _chatList = null!;
    string _name = "";
    bool _busy;
    bool _lastWasHer;

    static string PersonaFile()
    {
        // Exported app: persona.md sits next to the .exe. Running from the project: it's in the project folder.
        string nextToExe = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", "persona.md");
        if (!OS.HasFeature("editor") && File.Exists(nextToExe)) return nextToExe;
        return ProjectSettings.GlobalizePath("res://persona.md");
    }

    public override async void _Ready()
    {
        // Test hook: Godot ... -- --findgif "happy dance"  prints the GIF it finds and how many frames it decodes.
        var args = OS.GetCmdlineUserArgs();
        int fg = Array.IndexOf(args, "--findgif");
        if (fg >= 0 && fg + 1 < args.Length)
        {
            string? key = Gif.Key(PersonaFile());
            string? url = key == null ? null : await Gif.Find(key, args[fg + 1]);
            var frames = url == null ? null : await Gif.Load(url);
            GD.Print($"gif: key={(key != null)} url={url} frames={frames?.Textures.Count} size={frames?.Size}");
            GetTree().Quit();
            return;
        }

        // Test hook: Godot ... -- --say "yo whats up" [--name Millie]  prints the bot's texts and quits.
        
        int si = Array.IndexOf(args, "--say");
        if (si >= 0 && si + 1 < args.Length)
        {
            int ni = Array.IndexOf(args, "--name");
            string who = ni >= 0 && ni + 1 < args.Length ? args[ni + 1] : "a friend";
            int pi = Array.IndexOf(args, "--photo"); // also send a photo after the text
            var hist = new List<Chat.Line> { new(who, args[si + 1]) };
            if (pi >= 0 && pi + 1 < args.Length) hist.Add(new(who, "IMG: " + args[pi + 1]));
            var texts = await Chat.Reply(PersonaFile(), who, hist, Gif.Key(PersonaFile()) != null);
            foreach (var t in texts) GD.Print("olivia: " + t);
            var lines = new List<Chat.Line> { new(who, args[si + 1]) };
            lines.AddRange(texts.Select(t => new Chat.Line("Olivia", t)));
            GD.Print("title: " + await Chat.Title(lines));
            GetTree().Quit();
            return;
        }

        GetWindow().Title = "olivia";
        GetWindow().MinSize = new Vector2I(420, 560);
        BuildUi();
        LoadAll();

        // Open the most recent chat, or a fresh one the first time ever on this computer.
        if (_chats.Count > 0) Open(_chats[0]);
        else NewChat();

        if (Chat.FindClaude() == null)
            Event("this needs Claude Code installed and signed in on this computer", ErrorPink);
        _input.GrabFocus();

        // Test hook: -- --testdelete  adds two throwaway chats, clicks the first one's × with a real mouse click,
        // prints what happened, then removes the test chats.
        if (args.Contains("--testdelete"))
        {
            // Only throwaway chats are in the list, so nothing real can be deleted, even if delete misbehaves.
            _chats.Clear();
            var test = Enumerable.Range(1, 4).Select(i => new Chat.Conversation { Title = $"test chat {i}", Lines = { new("Friend", $"t{i}") } }).ToList();
            foreach (var c in test) { SaveConv(c); _chats.Add(c); }
            Open(test[0]);
            for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var del = _chatList.FindChildren("*", nameof(Button), true, false).OfType<Button>().First(x => x.Text == "×");
            var at = del.GetGlobalRect().GetCenter();
            async Task Click(double waitAfter)
            {
                GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
                GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left, Pressed = true, Position = at, GlobalPosition = at });
                GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
                await ToSignal(GetTree().CreateTimer(waitAfter), SceneTreeTimer.SignalName.Timeout);
            }
            string Left() => $"{test.Count(c => !_chats.Contains(c))} deleted, left = {string.Join(", ", _chats.Select(c => c.Title))}, open = {_cur.Title}";
            await Click(0.08); await Click(0.6);
            GD.Print("double-click on the first x: " + Left());
            await Click(0.6);
            GD.Print("a separate click later: " + Left());
            SaveConv(test[0]); // what the background title step does after a reply
            GD.Print("deleted chat re-saved by title step? " + Godot.FileAccess.FileExists($"{ChatsDir}/{test[0].Id}.json"));
            foreach (var c in test) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath($"{ChatsDir}/{c.Id}.json"));
            GetTree().Quit();
            return;
        }

        // Screenshot hook: -- --show menu|emoji  opens that popup on start.
        int show = Array.IndexOf(args, "--show");
        if (show >= 0 && show + 1 < args.Length)
        {
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
            var plusBtn = FindChildren("*", nameof(Button), true, false).OfType<Button>().First(b => b.TooltipText == "options");
            if (args[show + 1] == "emoji") OpenEmojiPicker(plusBtn);
            else if (args[show + 1] == "gif") OpenGifPicker(plusBtn);
            else { var p = plusBtn.GetScreenPosition(); _options.Popup(new Rect2I((int)p.X, (int)(p.Y - 130), 170, 0)); }
        }
    }

    // ---------- layout ----------

    void BuildUi()
    {
        // Rounded, bold font if the computer has one; Segoe UI otherwise.
        _font = new SystemFont
        {
            FontNames = new[] { "Nunito", "Quicksand", "Fredoka", "Varela Round", "Comfortaa", "Segoe UI" },
            // Color emoji come from the system emoji font when the main font doesn't have them.
            Fallbacks = new Godot.Collections.Array<Font> { new SystemFont { FontNames = new[] { "Segoe UI Emoji", "Noto Color Emoji", "Apple Color Emoji" } } },
            FontWeight = 700,
            Antialiasing = TextServer.FontAntialiasing.Gray,
        };
        Theme = new Theme { DefaultFont = _font, DefaultFontSize = 15 };

        _pillShader = new Shader
        {
            Code = """
                shader_type canvas_item;
                uniform vec4 left : source_color;
                uniform vec4 right : source_color;
                uniform float width = 300.0;
                varying vec2 lp;
                void vertex() { lp = VERTEX; }
                void fragment() {
                    float t = clamp(lp.x / max(width, 1.0), 0.0, 1.0);
                    COLOR.rgb = mix(left.rgb, right.rgb, t);
                }
                """,
        };

        // Background: pink gradient + sparkles
        var grad = new Gradient();
        grad.SetColor(0, BgTop);
        grad.SetColor(1, BgBottom);
        var bg = new TextureRect
        {
            Texture = new GradientTexture2D { Gradient = grad, FillFrom = new Vector2(0, 0), FillTo = new Vector2(1, 1), Width = 64, Height = 64 },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var sparkles = new Sparkles();
        sparkles.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(sparkles);

        BuildSidebar();

        // The chat panel, with a soft pink glow
        _panelWrap = new Control();
        AddChild(_panelWrap);
        _panel = new PanelContainer();
        var ps = Box(Panel, 26, 18);
        ps.ShadowColor = PanelGlow;
        ps.ShadowSize = 26;
        _panel.AddThemeStyleboxOverride("panel", ps);
        _panelWrap.AddChild(_panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(col);

        // Panel header: who this is + a heart badge
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        var headText = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        headText.AddThemeConstantOverride("separation", 2);
        headText.AddChild(Spacer(14));
        _chatTitle = L("", 14, Muted);
        _chatTitle.ClipText = true;
        _chatTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        headText.AddChild(_chatTitle);
        head.AddChild(headText);
        col.AddChild(head);

        // Thread
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _scroll.GetVScrollBar().AddThemeStyleboxOverride("scroll", Box(new Color(1, 1, 1, 0.05f), 4, 0));
        _scroll.GetVScrollBar().AddThemeStyleboxOverride("grabber", Box(new Color(1, 0.75f, 0.88f, 0.35f), 4, 0));
        _scroll.GetVScrollBar().AddThemeStyleboxOverride("grabber_highlight", Box(new Color(1, 0.75f, 0.88f, 0.6f), 4, 0));
        col.AddChild(_scroll);
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 6);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        _scroll.AddChild(pad);
        _thread = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _thread.AddThemeConstantOverride("separation", 7); // between bubbles; runs get their own spacer
        pad.AddChild(_thread);
        _scroll.GetVScrollBar().Changed += () => _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue;

        // Input row, iMessage-style: round "+" for options on the left, then a pill text field with the send
        // arrow inside it on the right.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);

        var plus = new Button { CustomMinimumSize = new Vector2(42, 42), FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "options" };
        plus.AddThemeStyleboxOverride("normal", Box(InputBg, 21, 0));
        plus.AddThemeStyleboxOverride("hover", Box(InputBg.Lightened(0.1f), 21, 0));
        plus.AddThemeStyleboxOverride("pressed", Box(InputBg.Darkened(0.15f), 21, 0));
        var plusIcon = Icon.Make(Icon.Kind.Plus, 42, NamePink);
        plusIcon.SetAnchorsPreset(LayoutPreset.FullRect);
        plus.AddChild(plusIcon);
        _options = new PopupMenu();
        _options.AddThemeFontSizeOverride("font_size", 15);
        _options.AddThemeColorOverride("font_color", Text);
        _options.AddThemeColorOverride("font_hover_color", Colors.White);
        var pm = Box(C(86, 50, 64), 14, 8); pm.ShadowColor = new Color(0, 0, 0, 0.3f); pm.ShadowSize = 10;
        _options.AddThemeStyleboxOverride("panel", pm);
        _options.AddThemeStyleboxOverride("hover", Box(C(130, 76, 98), 10, 6));
        _options.AddItem("📷  photo", 0);
        _options.AddItem("✨  gif", 1);
        _options.AddItem("😊  emoji", 2);
        _options.IdPressed += id =>
        {
            if (_busy) return;
            if (id == 0) PickPhoto();
            else if (id == 1) OpenGifPicker(plus);
            else if (id == 2) OpenEmojiPicker(plus);
        };
        AddChild(_options);
        plus.Pressed += () =>
        {
            var p = plus.GetScreenPosition();
            _options.Popup(new Rect2I((int)p.X, (int)(p.Y - 130), 170, 0));
        };
        row.AddChild(plus);

        var field = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _fieldNormal = Box(InputBg, 24, 0);
        _fieldNormal.ContentMarginLeft = 16; _fieldNormal.ContentMarginRight = 5; _fieldNormal.ContentMarginTop = 5; _fieldNormal.ContentMarginBottom = 5;
        _fieldFocus = (StyleBoxFlat)_fieldNormal.Duplicate();
        _fieldFocus.ShadowColor = new Color(Hot, 0.35f); _fieldFocus.ShadowSize = 8;
        _fieldFocus.BorderColor = new Color(Hot, 0.5f); _fieldFocus.SetBorderWidthAll(1);
        field.AddThemeStyleboxOverride("panel", _fieldNormal);
        var frow = new HBoxContainer();
        frow.AddThemeConstantOverride("separation", 6);
        field.AddChild(frow);
        _input = new LineEdit { PlaceholderText = "yap about something...", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 38) };
        _input.AddThemeFontSizeOverride("font_size", 15);
        _input.AddThemeColorOverride("font_color", Text);
        _input.AddThemeColorOverride("font_placeholder_color", new Color(Muted, 0.6f));
        _input.AddThemeColorOverride("caret_color", Hot);
        _input.AddThemeColorOverride("selection_color", new Color(Hot, 0.4f));
        foreach (var s in new[] { "normal", "focus", "read_only" }) _input.AddThemeStyleboxOverride(s, new StyleBoxEmpty());
        _input.TextSubmitted += _ => OnSend();
        _input.FocusEntered += () => field.AddThemeStyleboxOverride("panel", _fieldFocus);
        _input.FocusExited += () => field.AddThemeStyleboxOverride("panel", _fieldNormal);
        _input.TextChanged += _ => RefreshSend();
        frow.AddChild(_input);

        _sendBtn = new Button { CustomMinimumSize = new Vector2(36, 36), FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "send" };
        var sn = Box(Hot, 18, 0); sn.ShadowColor = new Color(Hot, 0.45f); sn.ShadowSize = 6;
        _sendBtn.AddThemeStyleboxOverride("normal", sn);
        _sendBtn.AddThemeStyleboxOverride("hover", Box(Hot.Lightened(0.12f), 18, 0));
        _sendBtn.AddThemeStyleboxOverride("pressed", Box(Hot.Darkened(0.12f), 18, 0));
        _sendBtn.AddThemeStyleboxOverride("disabled", Box(C(120, 80, 96), 18, 0));
        var arrow = Icon.Make(Icon.Kind.Arrow, 36, Colors.White);
        arrow.SetAnchorsPreset(LayoutPreset.FullRect);
        _sendBtn.AddChild(arrow);
        _sendBtn.Pressed += OnSend;
        frow.AddChild(_sendBtn);
        row.AddChild(field);
        col.AddChild(row);
        RefreshSend();

        // Sticker-style title sitting on the panel's top edge
        _title = new Label { Text = "olivia", MouseFilter = MouseFilterEnum.Ignore };
        _title.AddThemeFontSizeOverride("font_size", 40);
        _title.AddThemeColorOverride("font_color", Colors.White);
        _title.AddThemeColorOverride("font_outline_color", C(70, 34, 48));
        _title.AddThemeConstantOverride("outline_size", 12);
        _title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.25f));
        _title.AddThemeConstantOverride("shadow_offset_y", 3);
        _panelWrap.AddChild(_title);

        Resized += Layout;
        Layout();
    }

    Label _chatTitle = null!;
    PopupMenu _options = null!;
    StyleBoxFlat _fieldNormal = null!, _fieldFocus = null!;

    // Like iMessage, the send arrow only lights up when there's something to send.
    void RefreshSend() => _sendBtn.Disabled = _busy || _input.Text.Trim() == "";

    void BuildSidebar()
    {
        _sidebar = new PanelContainer();
        var ss = Box(C(86, 50, 64, 0.9f), 26, 16);
        ss.ShadowColor = new Color(PanelGlow, 0.3f);
        ss.ShadowSize = 18;
        _sidebar.AddThemeStyleboxOverride("panel", ss);
        AddChild(_sidebar);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        _sidebar.AddChild(col);

        // "+ new chat": a glowing gradient pill, like the friend's messages
        // The label is a child so the gradient shader on the button doesn't recolor the text.
        var btn = new Button { CustomMinimumSize = new Vector2(0, 44), FocusMode = FocusModeEnum.None };
        var btnText = L("♡  new chat", 15, Colors.White);
        btnText.HorizontalAlignment = HorizontalAlignment.Center;
        btnText.VerticalAlignment = VerticalAlignment.Center;
        btnText.AddThemeColorOverride("font_shadow_color", new Color(C(200, 60, 130), 0.45f));
        btnText.AddThemeConstantOverride("shadow_offset_y", 1);
        btnText.SetAnchorsPreset(LayoutPreset.FullRect);
        btn.AddChild(btnText);
        foreach (var (state, glow) in new[] { ("normal", 10), ("hover", 16), ("pressed", 6) })
        {
            var st = Box(Colors.White, 22, 10);
            st.ShadowColor = PillGlow; st.ShadowSize = glow;
            btn.AddThemeStyleboxOverride(state, st);
        }
        btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var mat = new ShaderMaterial { Shader = _pillShader };
        mat.SetShaderParameter("left", PillLeft);
        mat.SetShaderParameter("right", PillRight);
        btn.Material = mat;
        btn.Resized += () => mat.SetShaderParameter("width", btn.Size.X);
        btn.Pressed += () => { if (!_busy) NewChat(); };
        col.AddChild(btn);


        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.GetVScrollBar().AddThemeStyleboxOverride("scroll", Box(new Color(1, 1, 1, 0.04f), 4, 0));
        scroll.GetVScrollBar().AddThemeStyleboxOverride("grabber", Box(new Color(1, 0.75f, 0.88f, 0.3f), 4, 0));
        col.AddChild(scroll);
        _chatList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _chatList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_chatList);
    }

    void RefreshSidebar()
    {
        foreach (var c in _chatList.GetChildren()) c.QueueFree();
        var shown = _chats.ToList();
        if (shown.Count == 0) { _chatList.AddChild(L("no chats yet", 13, new Color(Muted, 0.6f))); return; }

        foreach (var conv in shown)
        {
            bool sel = conv == _cur;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 2);

            var item = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left,
                                    ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new Vector2(0, 50) };
            item.Text = conv.Title + "\n" + When(conv.Updated);
            item.AddThemeFontSizeOverride("font_size", 14);
            item.AddThemeColorOverride("font_color", sel ? Colors.White : Text.Darkened(0.12f));
            item.AddThemeColorOverride("font_hover_color", Colors.White);
            item.AddThemeColorOverride("font_pressed_color", Colors.White);
            var normal = Box(sel ? C(130, 76, 98) : new Color(0, 0, 0, 0), 14, 10);
            if (sel) { normal.BorderColor = new Color(NamePink, 0.6f); normal.SetBorderWidthAll(1); normal.ShadowColor = new Color(Hot, 0.25f); normal.ShadowSize = 8; }
            item.AddThemeStyleboxOverride("normal", normal);
            item.AddThemeStyleboxOverride("hover", Box(C(118, 70, 90), 14, 10));
            item.AddThemeStyleboxOverride("pressed", Box(C(130, 76, 98), 14, 10));
            item.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            item.Pressed += () => { if (!_busy && conv != _cur) Open(conv); };
            row.AddChild(item);

            if (_chats.Contains(conv))
            {
                var del = new Button { Text = "×", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(26, 26), SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "delete this chat" };
                del.AddThemeFontSizeOverride("font_size", 16);
                del.AddThemeColorOverride("font_color", new Color(Muted, 0.45f));
                del.AddThemeColorOverride("font_hover_color", Hot);
                foreach (var s in new[] { "normal", "hover", "pressed", "focus" }) del.AddThemeStyleboxOverride(s, new StyleBoxEmpty());
                del.Pressed += () => Delete(conv);
                row.AddChild(del);
            }
            _chatList.AddChild(row);
        }
    }

    static string When(long unix)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
        var now = DateTimeOffset.Now;
        if (t.Date == now.Date) return t.ToString("h:mm tt").ToLowerInvariant();
        if (t.Date == now.Date.AddDays(-1)) return "yesterday";
        if ((now - t).TotalDays < 7) return t.ToString("dddd").ToLowerInvariant();
        return t.ToString("MMM d").ToLowerInvariant();
    }

    void Layout()
    {
        bool side = Size.X >= 760;
        _sidebar.Visible = side;
        float top = 54, bottom = 28, gap = 22;
        float left = side ? 24 + SidebarWidth + gap : 0;
        float avail = Size.X - left - (side ? 24 : 0);
        float w = side ? Math.Min(PanelMaxWidth + 120, avail) : Math.Min(PanelMaxWidth, Size.X - 40);
        if (side)
        {
            _sidebar.Position = new Vector2(24, top);
            _sidebar.Size = new Vector2(SidebarWidth, Math.Max(200, Size.Y - top - bottom));
        }
        _panelWrap.Position = new Vector2(side ? left + (avail - w) / 2 : (Size.X - w) / 2, top);
        _panelWrap.Size = new Vector2(w, Math.Max(200, Size.Y - top - bottom));
        _panel.Position = Vector2.Zero;
        _panel.Size = _panelWrap.Size;
        _title.Position = new Vector2(22, -38);
        foreach (var l in _pillLabels) if (IsInstanceValid(l)) FitPill(l);
    }

    // ---------- conversation ----------

    async void OnSend()
    {
        string text = _input.Text.Trim();
        if (text == "" || _busy) return;
        _input.Text = "";
        await Send(text);
    }

    // Adds the friend's message (text, "GIF: …" or "IMG: …") to the chat and gets the bot's reply.
    async Task Send(string text, Action? afterShown = null)
    {
        if (_busy) return;
        var mine = new Chat.Line(_name == "" ? "Friend" : _name, text);
        You_(mine);
        afterShown?.Invoke();
        _history.Add(mine);
        Save();

        _busy = true;
        _sendBtn.Disabled = true;
        await ShowTyping(0.4);
        string? gifKey = Gif.Key(PersonaFile());
        var texts = await Chat.Reply(PersonaFile(), _name == "" ? "a friend whose name you don't know yet" : _name, _history, gifKey != null);

        foreach (var t in texts)
        {
            if (t.StartsWith("error:", StringComparison.Ordinal))
            {
                HideTyping();
                Event(t.Substring(6).Trim(), ErrorPink);
                break;
            }
            if (Chat.IsGif(t))
            {
                // Look it up; if nothing comes back, just skip it.
                string query = Chat.GifParts(t).query;
                string? url = gifKey == null ? null : await Gif.Find(gifKey, query);
                if (url == null) continue;
                await ShowTyping(0.6);
                HideTyping();
                var gl = new Chat.Line("Olivia", $"GIF: {query}|{url}");
                Her(gl);
                _history.Add(gl);
                continue;
            }
            if (Chat.IsName(t))
            {
                // The bot found out who this is ("NAME: Millie"); remember it on this computer.
                string n = CleanName(Chat.NameValue(t));
                if (n != "" && _name == "") { _name = n; SaveName(); Event($"{_name.ToLower()} just joined!"); }
                continue;
            }
            if (Chat.IsReact(t))
            {
                // A tapback on their message, like iMessage.
                mine.React = Chat.ReactEmoji(t);
                if (_bubbleFor.TryGetValue(mine, out var bub)) SetBadge(bub, mine.React);
                await ShowTyping(0.3);
                HideTyping();
                continue;
            }
            await ShowTyping(Math.Clamp(0.35 + t.Length * 0.025, 0.4, 1.6));
            HideTyping();
            var hl = new Chat.Line("Olivia", t);
            Her(hl);
            _history.Add(hl);
        }
        HideTyping();
        Save();
        _busy = false;
        RefreshSend();
        _input.GrabFocus();

        // Name the chat the way Claude does: a placeholder from the first message right away, then a short
        // title in the background once there's a little conversation.
        var conv = _cur;
        if (!conv.Titled && conv.Lines.Count(l => l.Who != "Olivia") >= 1)
        {
            conv.Titled = true;
            string t = await Chat.Title(conv.Lines);
            conv.Title = t != "" ? t : FirstLine(conv.Lines);
            if (_deleted.Contains(conv.Id)) return;
            SaveConv(conv);
            if (conv == _cur) _chatTitle.Text = conv.Title;
            RefreshSidebar();
        }
    }

    static string CleanName(string text)
    {
        string t = Regex.Replace(text.Trim(), @"^(it'?s|its|this is|i'?m|im|me,?|hey,?|hi,?|lol)\s+", "", RegexOptions.IgnoreCase);
        string first = t.Split(' ', ',', '.', '!', '?').FirstOrDefault(w => w != "") ?? "";
        if (!Regex.IsMatch(first, @"^[A-Za-z][A-Za-z'-]{1,19}$") || NotNames.Contains(first.ToLowerInvariant())) return "";
        return char.ToUpper(first[0]) + first[1..].ToLowerInvariant();
    }

    static readonly HashSet<string> NotNames = new()
    {
        "you", "me", "u", "hi", "hey", "hello", "hiya", "yo", "sup", "lol", "lmao", "idk", "guess", "who", "what",
        "nobody", "someone", "your", "ur", "friend", "olivia", "its", "it's", "the", "a", "omg", "haha", "bestie",
    };

    // ---------- + menu: photo, gif, emoji ----------

    const string PhotosDir = "user://photos";
    FileDialog? _photoDialog;
    PopupPanel? _gifPicker, _emojiPicker;

    void PickPhoto()
    {
        if (_photoDialog == null)
        {
            _photoDialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true, Title = "send a photo",
                Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; Photos" },
            };
            _photoDialog.FileSelected += async path =>
            {
                // Keep a copy with the chat, so it still shows if the original moves.
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > 15_000_000) { Event("that photo's too big (15 MB max)", ErrorPink); return; }
                    DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(PhotosDir));
                    string dest = ProjectSettings.GlobalizePath($"{PhotosDir}/{Guid.NewGuid():N}{info.Extension.ToLowerInvariant()}");
                    File.Copy(path, dest);
                    await Send("IMG: " + dest);
                }
                catch (Exception ex) { Event("couldn't attach that: " + ex.Message, ErrorPink); }
            };
            AddChild(_photoDialog);
        }
        _photoDialog.PopupCentered(new Vector2I(800, 560));
    }

    Control PhotoBubble(string path)
    {
        var frame = new PanelContainer { ClipChildren = ClipChildrenMode.AndDraw };
        var st = Box(C(70, 40, 52), 18, 0);
        st.ShadowColor = PillGlow; st.ShadowSize = 10;
        frame.AddThemeStyleboxOverride("panel", st);
        var img = new Image();
        if (File.Exists(path) && img.Load(path) == Error.Ok)
        {
            float h = 200, w = Math.Clamp(h * img.GetWidth() / Math.Max(1f, img.GetHeight()), 110, 300);
            if (w == 300) h = Math.Max(90, 300f * img.GetHeight() / Math.Max(1, img.GetWidth()));
            frame.CustomMinimumSize = new Vector2(w, h);
            frame.AddChild(new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(img),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            });
        }
        else
        {
            frame.CustomMinimumSize = new Vector2(160, 60);
            var l = L("(photo missing)", 13, Muted);
            l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center;
            frame.AddChild(l);
        }
        return frame;
    }

    PopupPanel Picker(string title, Vector2I size, out VBoxContainer body)
    {
        var pp = new PopupPanel();
        var st = Box(C(86, 50, 64), 18, 14);
        st.ShadowColor = new Color(0, 0, 0, 0.3f); st.ShadowSize = 12;
        st.BorderColor = new Color(NamePink, 0.35f); st.SetBorderWidthAll(1);
        pp.AddThemeStyleboxOverride("panel", st);
        pp.Size = size;
        body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        body.AddChild(L(title, 14, NamePink));
        pp.AddChild(body);
        AddChild(pp);
        return pp;
    }

    void ShowAbove(PopupPanel pp, Control anchor)
    {
        var p = anchor.GetScreenPosition();
        pp.Popup(new Rect2I((int)p.X, (int)(p.Y - pp.Size.Y - 10), pp.Size.X, pp.Size.Y));
    }

    // GIF picker: search box + a grid of GIPHY results (trending when the box is empty). Click one to send it.
    void OpenGifPicker(Control anchor)
    {
        string? key = Gif.Key(PersonaFile());
        if (key == null) { Event("gifs need a GIPHY key in giphy-key.txt", ErrorPink); return; }
        if (_gifPicker == null)
        {
            _gifPicker = Picker("✨ gifs", new Vector2I(440, 420), out var body);
            var search = new LineEdit { PlaceholderText = "search GIPHY", CustomMinimumSize = new Vector2(0, 38) };
            search.AddThemeColorOverride("font_color", Text);
            search.AddThemeColorOverride("caret_color", Hot);
            search.AddThemeStyleboxOverride("normal", Box(InputBg, 19, 12));
            search.AddThemeStyleboxOverride("focus", Box(InputBg, 19, 12));
            body.AddChild(search);
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            body.AddChild(scroll);
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 8);
            scroll.AddChild(grid);
            body.AddChild(L("powered by GIPHY", 10, new Color(Muted, 0.6f)));

            int searchId = 0;
            async void Fill(string q)
            {
                int me = ++searchId;
                foreach (var c in grid.GetChildren()) c.QueueFree();
                var results = await Gif.Search(key, q.Trim() == "" ? "trending" : q.Trim(), 12);
                if (me != searchId) return; // a newer search started
                if (results.Count == 0) { grid.AddChild(L("nothing found", 13, Muted)); return; }
                foreach (var (preview, full) in results)
                {
                    var btn = new Button { CustomMinimumSize = new Vector2(196, 130), FocusMode = FocusModeEnum.None, ClipChildren = ClipChildrenMode.AndDraw };
                    btn.AddThemeStyleboxOverride("normal", Box(InputBg, 12, 0));
                    var hov = Box(InputBg, 12, 0); hov.BorderColor = Hot; hov.SetBorderWidthAll(2);
                    btn.AddThemeStyleboxOverride("hover", hov);
                    btn.AddThemeStyleboxOverride("pressed", hov);
                    var player = new GifPlayer { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
                    player.SetAnchorsPreset(LayoutPreset.FullRect);
                    btn.AddChild(player);
                    string query = q.Trim() == "" ? "trending" : q.Trim();
                    btn.Pressed += async () => { _gifPicker!.Hide(); await Send($"GIF: {query}|{full}"); };
                    grid.AddChild(btn);
                    _ = LoadPreview(player, preview);
                }
            }
            async Task LoadPreview(GifPlayer player, string url)
            {
                var f = await Gif.Load(url);
                if (f != null && IsInstanceValid(player)) player.Play(f);
            }
            var debounce = new Timer { OneShot = true, WaitTime = 0.45 };
            body.AddChild(debounce);
            debounce.Timeout += () => Fill(search.Text);
            search.TextChanged += _ => debounce.Start();
            search.TextSubmitted += _ => { debounce.Stop(); Fill(search.Text); };
            _gifPicker.AboutToPopup += () => { if (grid.GetChildCount() == 0) Fill(search.Text); search.CallDeferred(Control.MethodName.GrabFocus); };
        }
        ShowAbove(_gifPicker, anchor);
    }

    // Emoji picker: tap one to drop it into the text box.
    static readonly string[] Emojis =
    {
        "😂", "🤣", "😭", "🥹", "😍", "🥰", "😊", "🙂", "😅", "😆", "😎", "🤓", "🤔", "🙃", "😬", "😳",
        "😴", "🫠", "🥲", "😤", "😩", "🙄", "😏", "🤭", "💀", "👀", "🫶", "🙏", "👏", "🙌", "💪", "👍",
        "✨", "💖", "💕", "💗", "💜", "🩷", "❤️", "🔥", "🎉", "🥳", "🌸", "🍓", "🧋", "☕", "🍕", "🐶",
        "🐱", "🐸", "🦋", "🌈", "⭐", "☀️", "❄️", "🏂", "💻", "🐛", "✅", "❌", "📦", "🚗",
    };

    void OpenEmojiPicker(Control anchor)
    {
        if (_emojiPicker == null)
        {
            _emojiPicker = Picker("😊 emoji", new Vector2I(372, 330), out var body);
            var grid = new GridContainer { Columns = 8 };
            grid.AddThemeConstantOverride("h_separation", 2);
            grid.AddThemeConstantOverride("v_separation", 2);
            body.AddChild(grid);
            foreach (var e in Emojis)
            {
                var b = new Button { Text = e, CustomMinimumSize = new Vector2(42, 38), FocusMode = FocusModeEnum.None };
                b.AddThemeFontSizeOverride("font_size", 22);
                b.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
                b.AddThemeStyleboxOverride("hover", Box(C(130, 76, 98), 10, 0));
                b.AddThemeStyleboxOverride("pressed", Box(C(150, 86, 110), 10, 0));
                b.Pressed += () =>
                {
                    int caret = _input.CaretColumn;
                    _input.Text = _input.Text.Insert(Math.Min(caret, _input.Text.Length), e);
                    _input.CaretColumn = caret + e.Length;
                    RefreshSend();
                    _input.GrabFocus();
                };
                grid.AddChild(b);
            }
        }
        ShowAbove(_emojiPicker, anchor);
    }

    // ---------- chats ----------

    // A fresh conversation. The bot asks who you are only the first time ever on this computer; after that it
    // remembers your name, like Claude remembers who you are across chats.
    void NewChat()
    {
        _cur = new Chat.Conversation();
        ShowConversation();
        _input.GrabFocus();
    }

    void Open(Chat.Conversation conv)
    {
        _cur = conv;
        ShowConversation();
        _input.GrabFocus();
    }

    void ShowConversation()
    {
        foreach (var c in _thread.GetChildren()) c.QueueFree();
        _pillLabels.Clear();
        _typing = null;
        _lastWasHer = false;
        foreach (var line in _cur.Lines.TakeLast(80))
        {
            if (line.Who == "Olivia") Her(line); else You_(line);
        }
        _chatTitle.Text = _chats.Contains(_cur) ? _cur.Title : "";
        RefreshSidebar();
    }

    // Chats deleted this session. Nothing may write them back (the background title step finishes after a reply
    // and used to re-save a chat that had just been deleted, so it came back next time the app opened).
    static readonly HashSet<string> _deleted = new();
    ulong _lastDeleteMs;

    void Delete(Chat.Conversation conv)
    {
        // After a delete the next chat's × slides under the cursor; ignore a quick second click so a double-click
        // doesn't take out two chats.
        ulong now = Time.GetTicksMsec();
        if (now - _lastDeleteMs < 450 || !_chats.Contains(conv)) return;
        // The open chat can't go while she's still typing into it; any other chat can.
        if (_busy && conv == _cur) { Event("wait for her to finish typing, then delete it", Muted); return; }
        _lastDeleteMs = now;
        _deleted.Add(conv.Id);
        _chats.Remove(conv);
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath($"{ChatsDir}/{conv.Id}.json"));
        if (conv == _cur) { if (_chats.Count > 0) Open(_chats[0]); else NewChat(); }
        else RefreshSidebar();
    }

    // ---------- messages ----------

    // Olivia: pink name with a heart on the first of a run, white text under it.
    void Her(string text) => Her(new Chat.Line("Olivia", text));

    // Olivia, iMessage-style: light pink bubbles on the left with dark text, the pink name + heart over each run,
    // and the tail only on the last bubble of the run.
    void Her(Chat.Line line)
    {
        string text = line.Text;
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);
        if (!_lastWasHer)
        {
            EndRun();
            box.AddChild(Spacer(26));
            var name = new HBoxContainer();
            name.AddThemeConstantOverride("separation", 8);
            name.AddChild(Icon.Make(Icon.Kind.Heart, 22, Hot, new Color(Hot, 0.18f)));
            name.AddChild(L("olivia", 17, NamePink));
            box.AddChild(name);
        }
        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        body.AddChild(new Control { CustomMinimumSize = new Vector2(22, 0) });
        if (Chat.IsGif(text)) { body.AddChild(GifBubble(Chat.GifParts(text).url)); EndRun(); }
        else body.AddChild(TextBubble(line, right: false));
        box.AddChild(body);
        _thread.AddChild(box);
        _lastWasHer = true;
    }

    // A GIF in a rounded, glowing frame with the GIPHY credit under it. It loads in the background.
    Control GifBubble(string url, bool right = false)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 3);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(220, 150), ClipChildren = ClipChildrenMode.AndDraw };
        var st = Box(C(70, 40, 52), 16, 0);
        st.ShadowColor = new Color(Hot, 0.3f); st.ShadowSize = 8;
        frame.AddThemeStyleboxOverride("panel", st);
        var player = new GifPlayer { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered };
        frame.AddChild(player);
        var loading = L("• • •", 14, Muted);
        loading.HorizontalAlignment = HorizontalAlignment.Center;
        loading.VerticalAlignment = VerticalAlignment.Center;
        frame.AddChild(loading);
        col.AddChild(frame);
        var credit = L("via GIPHY", 10, new Color(Muted, 0.6f));
        if (right) credit.HorizontalAlignment = HorizontalAlignment.Right;
        col.AddChild(credit);

        _ = LoadGif();
        return col;

        async Task LoadGif()
        {
            var f = url == "" ? null : await Gif.Load(url);
            if (!IsInstanceValid(frame)) return;
            if (f == null) { loading.Text = "(gif didn't load)"; return; }
            loading.QueueFree();
            // Fixed height like a text thread, width from the GIF's shape, capped.
            float h = 170, w = Math.Clamp(h * f.Size.X / Math.Max(1f, f.Size.Y), 120, 300);
            frame.CustomMinimumSize = new Vector2(w, h);
            player.Play(f);
        }
    }

    // The friend, iMessage-style: hot pink bubbles on the right with white text.
    void You_(Chat.Line line)
    {
        string text = line.Text;
        if (_lastWasHer) { EndRun(); _thread.AddChild(Spacer(26)); }
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        if (Chat.IsGif(text) || Chat.IsImage(text))
        {
            EndRun();
            row.AddChild(Chat.IsGif(text) ? GifBubble(Chat.GifParts(text).url, right: true) : PhotoBubble(Chat.ImagePath(text)));
            _thread.AddChild(row);
            _lastWasHer = false;
            return;
        }
        row.AddChild(TextBubble(line, right: true));
        _thread.AddChild(row);
        _lastWasHer = false;
    }

    static readonly Color HerBubble = C(255, 199, 226);
    static readonly Color HerInk = C(60, 22, 40);
    static readonly Color YouBubble = C(255, 106, 170);
    Bubble? _prevBubble;                    // the newest text bubble, while the run continues; null after a break
    readonly Dictionary<Chat.Line, Bubble> _bubbleFor = new(ReferenceEqualityComparer.Instance); // by identity: lines with the same text are still different messages

    Bubble TextBubble(Chat.Line line, bool right)
    {
        // The previous bubble loses its tail if this one continues the same person's run.
        if (_prevBubble != null && IsInstanceValid(_prevBubble) && _prevBubble.Right == right) _prevBubble.Tail = false;
        var b = new Bubble { Fill = right ? YouBubble : HerBubble, Right = right, SizeFlagsHorizontal = right ? SizeFlags.ShrinkEnd : SizeFlags.ShrinkBegin, MouseFilter = MouseFilterEnum.Stop };
        var l = L(line.Text, 16, right ? Colors.White : HerInk);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        b.AddChild(l);
        _pillLabels.Add(l);
        FitPill(l);
        _prevBubble = b;
        _bubbleFor[line] = b;
        if (!string.IsNullOrEmpty(line.React)) SetBadge(b, line.React);
        // Double-click or right-click for tapbacks, like long-pressing a message in iMessage.
        b.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed &&
                ((mb.ButtonIndex == MouseButton.Left && mb.DoubleClick) || mb.ButtonIndex == MouseButton.Right))
                ShowTapbacks(b, line);
        };
        b.TooltipText = "double-click to react";
        return b;
    }

    void EndRun() => _prevBubble = null;

    // ---------- tapback reactions ----------

    public static readonly string[] Tapbacks = { "❤️", "👍", "👎", "😂", "‼️", "❓" };
    PopupPanel? _tapbar;

    void ShowTapbacks(Bubble b, Chat.Line line)
    {
        _tapbar?.QueueFree();
        _tapbar = new PopupPanel();
        var st = Box(C(86, 50, 64), 22, 6);
        st.ShadowColor = new Color(0, 0, 0, 0.3f); st.ShadowSize = 10;
        _tapbar.AddThemeStyleboxOverride("panel", st);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        foreach (var e in Tapbacks)
        {
            var btn = new Button { Text = e, CustomMinimumSize = new Vector2(40, 36), FocusMode = FocusModeEnum.None };
            btn.AddThemeFontSizeOverride("font_size", 20);
            btn.AddThemeStyleboxOverride("normal", line.React == e ? Box(Hot, 18, 0) : new StyleBoxEmpty());
            btn.AddThemeStyleboxOverride("hover", Box(C(130, 76, 98), 18, 0));
            btn.AddThemeStyleboxOverride("pressed", Box(Hot, 18, 0));
            btn.Pressed += () =>
            {
                // Tapping the same one again takes it off.
                line.React = line.React == e ? null : e;
                SetBadge(b, line.React);
                _tapbar!.Hide();
                Save();
            };
            row.AddChild(btn);
        }
        _tapbar.AddChild(row);
        AddChild(_tapbar);
        var p = b.GetScreenPosition();
        int w = Tapbacks.Length * 42 + 16;
        int x = b.Right ? (int)(p.X + b.Size.X - w) : (int)p.X;
        _tapbar.Popup(new Rect2I(x, (int)p.Y - 52, w, 48));
    }

    // The little reaction bubble on a message's top corner (top-right of hers, top-left of yours).
    void SetBadge(Bubble b, string? emoji)
    {
        foreach (var c in b.GetChildren()) if (c.Name == "badge") { b.RemoveChild(c); c.QueueFree(); }
        if (string.IsNullOrEmpty(emoji)) return;
        var badge = new PanelContainer { Name = "badge", MouseFilter = MouseFilterEnum.Ignore };
        var st = Box(b.Right ? HerBubble : YouBubble, 15, 3);
        st.BorderColor = new Color(Panel, 1f); st.SetBorderWidthAll(2);
        badge.AddThemeStyleboxOverride("panel", st);
        var l = L(emoji, 14, Colors.White);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.CustomMinimumSize = new Vector2(22, 0);
        badge.AddChild(l);
        b.AddChild(badge);
    }

    // Centered event line with a little sparkle trail, like "millie just joined!".
    void Event(string text, Color? color = null)
    {
        _thread.AddChild(Spacer(4));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 8);
        var dots = L("• • •", 11, new Color(Text, 0.7f));
        row.AddChild(dots);
        row.AddChild(Icon.Make(Icon.Kind.Sparkle, 14, Colors.White));
        var l = L(text, 13, color ?? Text);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Math.Min(TextWidth(l) + 4, 300), 0);
        row.AddChild(l);
        _thread.AddChild(row);
        _thread.AddChild(Spacer(4));
        _lastWasHer = false;
    }

    float TextWidth(Label l)
    {
        int fs = l.GetThemeFontSize("font_size");
        return l.Text.Split('\n').Max(line => _font.GetStringSize(line, HorizontalAlignment.Left, -1, fs).X);
    }

    // An autowrapping label has no width of its own in a container, so give the pill the width its text needs, up
    // to about three quarters of the panel.
    void FitPill(Label l)
    {
        float max = Math.Max(160f, _panelWrap.Size.X * 0.74f - 40);
        l.CustomMinimumSize = new Vector2(Math.Min(TextWidth(l) + 4, max), 0);
    }

    async Task ShowTyping(double seconds)
    {
        if (_typing == null)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new Control { CustomMinimumSize = new Vector2(22, 0) });
            var l = L("• • •", 16, NamePink);
            row.AddChild(l);
            var sp = Icon.Make(Icon.Kind.Sparkle, 14, NamePink);
            sp.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(sp);
            _thread.AddChild(row);
            _typing = row;
            var tw = row.CreateTween().SetLoops();
            tw.TweenProperty(row, "modulate:a", 0.3f, 0.45f);
            tw.TweenProperty(row, "modulate:a", 1f, 0.45f);
        }
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    void HideTyping()
    {
        _typing?.QueueFree();
        _typing = null;
    }

    // ---------- saved state (on this computer only) ----------

    void LoadAll()
    {
        if (Godot.FileAccess.FileExists(NameFile)) _name = Godot.FileAccess.GetFileAsString(NameFile).Trim();
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(ChatsDir));

        // The app used to keep one chat in chat.json; carry it over as a saved chat.
        if (Godot.FileAccess.FileExists(HistoryFile))
        {
            try
            {
                var old = JsonSerializer.Deserialize<List<Chat.Line>>(Godot.FileAccess.GetFileAsString(HistoryFile)) ?? new();
                if (old.Any(l => l.Who != "Olivia"))
                {
                    var conv = new Chat.Conversation { Lines = old, Title = FirstLine(old) };
                    SaveConv(conv);
                }
            }
            catch { }
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(HistoryFile));
        }

        using var dir = DirAccess.Open(ChatsDir);
        if (dir != null)
            foreach (var f in dir.GetFiles().Where(f => f.EndsWith(".json")))
            {
                try
                {
                    var conv = JsonSerializer.Deserialize<Chat.Conversation>(Godot.FileAccess.GetFileAsString($"{ChatsDir}/{f}"));
                    if (conv != null && conv.Lines.Any(l => l.Who != "Olivia")) _chats.Add(conv);
                }
                catch { /* unreadable file: skip */ }
            }
        _chats.Sort((a, b) => b.Updated.CompareTo(a.Updated));
    }

    static string FirstLine(List<Chat.Line> lines)
    {
        string t = lines.FirstOrDefault(l => l.Who != "Olivia")?.Text ?? "new chat";
        t = t.ToLowerInvariant();
        return t.Length > 32 ? t[..30].TrimEnd() + "…" : t;
    }

    // Saves the open chat, once the friend has said something, and moves it to the top of the list.
    void Save()
    {
        if (!_cur.Lines.Any(l => l.Who != "Olivia")) return;
        if (!_chats.Contains(_cur)) { _cur.Title = "…"; _chatTitle.Text = ""; } // named once the bot has replied
        _cur.Updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _chats.Remove(_cur);
        _chats.Insert(0, _cur);
        SaveConv(_cur);
        RefreshSidebar();
    }

    static void SaveConv(Chat.Conversation conv)
    {
        if (_deleted.Contains(conv.Id)) return;
        using var f = Godot.FileAccess.Open($"{ChatsDir}/{conv.Id}.json", Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(conv));
    }

    void SaveName()
    {
        using var f = Godot.FileAccess.Open(NameFile, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(_name);
    }

    // ---------- helpers ----------

    static Control Spacer(float h) => new() { CustomMinimumSize = new Vector2(0, h), MouseFilter = MouseFilterEnum.Ignore };

    static Label L(string text, int size, Color color)
    {
        var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    static StyleBoxFlat Box(Color bg, int radius, int pad)
    {
        var sb = new StyleBoxFlat { BgColor = bg, AntiAliasing = true };
        sb.SetCornerRadiusAll(radius);
        sb.SetContentMarginAll(pad);
        return sb;
    }
}

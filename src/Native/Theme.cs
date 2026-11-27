// Thème sombre et contrôles dessinés à la main : repris de FacePhoto (launcher\Theme.cs, projet de l'utilisateur, 17/11),
// pour le Builder natif (plan work\diag\natif\PLAN.md). Code d'origine en C# 5, gardé tel quel.
#nullable disable
#pragma warning disable WFO1000   // propriétés de contrôles dessinés à la main : pas de concepteur WinForms ici
#pragma warning disable CS0649   // champs du thème de FacePhoto gardés tels quels (FlatButton.Round)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GenerateFollower.Native;

// Theme.cs (17/11) - FacePhoto's own controls, drawn by hand in the viewer's colours (dark grey, gold accent):
// cards, buttons, check boxes, slider, drop zone, text field, drop-down list, step list, progress line.
// Plain Windows Forms underneath (part of Windows), no web page.

static class Theme
{
    public static readonly Color Bg = Hex(0x1d1e22), Panel = Hex(0x26282e), Card = Hex(0x2d3036), Line = Hex(0x3d4048),
        Field = Hex(0x33363d), FieldLine = Hex(0x44474f), Text = Hex(0xe8e6e1), Muted = Hex(0x9a978f),
        Accent = Hex(0xc9a26b), Ok = Hex(0x8fbf7f), Warn = Hex(0xd9a441), Err = Hex(0xe07a68), View = Hex(0x2a2c31),
        Log = Hex(0x1a1b1f);
    // police de la langue de l'interface (L.Init est appelé avant la première fenêtre) : Segoe UI n'a pas de caractères
    // chinois, et la police de secours de Windows était grossière. Japonais et coréen : Segoe UI et la police de secours,
    // car Yu Gothic UI et Malgun Gothic dessinent la barre « \ » des chemins en ¥ et ₩
    static readonly string Family = L.Lang switch { "zh" => "Microsoft YaHei UI", _ => "Segoe UI" };
    static Font Make(float size, bool bold) => Family == "Segoe UI"
        ? new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size)
        : new Font(Family, size, bold ? FontStyle.Bold : FontStyle.Regular);
    public static readonly Font Body = Make(9.75f, false), Small = Make(8.5f, false),
        Bold = Make(9.75f, true), H2 = Make(8.25f, true),
        Title = Make(13f, true), Mono = new Font("Consolas", 8.5f);
    static Theme() { }

    public static Color Hex(int v) { return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255); }
    public static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Box(Graphics g, Rectangle r, Color fill, Color line, float rad)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        using (var p = Round(rf, rad))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (line != Color.Empty) using (var pen = new Pen(line)) g.DrawPath(pen, p);
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr h, string app, string id);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    // dark scroll bars (Windows 10 1809+), dark title bar (Windows 10 2004+ / 11) - ignored elsewhere
    public static void DarkScroll(Control c)
    {
        try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
    }
    public static void DarkTitle(Form f)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            int col = Bg.R | (Bg.G << 8) | (Bg.B << 16);
            DwmSetWindowAttribute(f.Handle, 35, ref col, 4);
        }
        catch { }
    }

    public static Icon MakeIcon()
    {
        using (var b = new Bitmap(64, 64))
        {
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                Box(g, new Rectangle(2, 2, 60, 60), Accent, Color.Empty, 14);
                using (var f = new Font("Segoe UI Semibold", 30f, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, "F", f, new Rectangle(0, 0, 64, 62), Bg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            return Icon.FromHandle(b.GetHicon());
        }
    }
}

// base: double-buffered, paints its parent's colour around rounded shapes
class Painted : Control
{
    public Painted()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.Body; ForeColor = Theme.Text;
    }
    protected Color Under { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
    protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Under); }
}

// vertical stack: children full width, one under the other; optional card look
class VStack : Panel
{
    public int Pad = 0, Gap = 10;
    public bool CardLook;
    public Color Fill = Theme.Card, Border = Theme.Line;
    public VStack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
    }
    public static VStack MakeCard()
    {
        var v = new VStack { CardLook = true, Pad = 12, Gap = 10 };
        v.BackColor = v.Fill;
        return v;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!CardLook) { e.Graphics.Clear(BackColor); return; }
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        Theme.Box(e.Graphics, ClientRectangle, Fill, Border, 10);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        int y = Pad;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            var t = c as TextLabel;
            int h = t != null ? t.Need(Width - 2 * Pad) : c.Height;
            c.SetBounds(Pad, y, Math.Max(1, Width - 2 * Pad), h);
            y += h + Gap;
        }
        int want = Math.Max(0, y - Gap + Pad);
        if (Height != want) Height = want;
    }
}

// horizontal row, equal widths
class HRow : Panel
{
    public int Gap = 6;
    public HRow() { Height = 34; }
    protected override void OnLayout(LayoutEventArgs e)
    {
        var vis = new List<Control>();
        foreach (Control c in Controls) if (c.Visible) vis.Add(c);
        if (vis.Count == 0) return;
        int w = (Width - Gap * (vis.Count - 1)) / vis.Count, x = 0;
        for (int i = 0; i < vis.Count; i++)
        {
            int ww = i == vis.Count - 1 ? Width - x : w;
            vis[i].SetBounds(x, 0, ww, Height);
            x += ww + Gap;
        }
    }
    protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); BackColor = Parent.BackColor; }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (Parent != null) BackColor = Parent.BackColor; }
}

// wrapping text, height follows the width
class TextLabel : Painted
{
    public TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
    public int Need(int w)
    {
        if (string.IsNullOrEmpty(Text)) return 0;
        return TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, w), int.MaxValue), Flags).Height + 1;
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (Parent != null) Parent.PerformLayout(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
    }
}

class FlatButton : Painted
{
    public bool Primary, Round;
    bool hover, down;
    public FlatButton(string text) { Text = text; Height = 34; Cursor = Cursors.Hand; }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected virtual string Shown { get { return Text; } }
    protected override void OnPaint(PaintEventArgs e)
    {
        Color fill = Primary ? Theme.Accent : Theme.Field, line = Primary ? Theme.Accent : (hover ? Theme.Accent : Theme.FieldLine),
              text = Primary ? Theme.Bg : Theme.Text;
        if (Primary && hover && Enabled) fill = Theme.Mix(fill, Color.White, 0.10);
        if (down && Enabled) fill = Theme.Mix(fill, Color.Black, 0.12);
        if (!Enabled) { fill = Theme.Mix(Under, fill, 0.45); line = Theme.Mix(Under, line, 0.45); text = Theme.Mix(Under, text, 0.45); }
        float rad = Round ? Height / 2f : 7;
        Theme.Box(e.Graphics, ClientRectangle, fill, line, rad);
        TextRenderer.DrawText(e.Graphics, Shown, Primary ? Theme.Bold : Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

class LinkButton : Painted
{
    bool hover;
    public LinkButton(string text) { Text = text; Height = 20; Cursor = Cursors.Hand; Font = Theme.Small; }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        using (var f = new Font(Font, FontStyle.Underline))
            TextRenderer.DrawText(e.Graphics, Text, f, ClientRectangle, hover ? Theme.Text : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

class Toggle : Painted
{
    bool on;
    public event EventHandler Changed;
    public Toggle(string text, bool initial) { Text = text; on = initial; Height = 24; Cursor = Cursors.Hand; }
    public bool Checked
    {
        get { return on; }
        set { if (on == value) return; on = value; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
    }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, (Height - 16) / 2, 16, 16);
        Theme.Box(g, r, on ? Theme.Accent : Theme.Field, on ? Theme.Accent : Theme.FieldLine, 4);
        if (on)
            using (var p = new Pen(Theme.Bg, 2f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLines(p, new[] { new PointF(r.X + 3.5f, r.Y + 8.5f), new PointF(r.X + 6.5f, r.Y + 11.5f), new PointF(r.X + 12.5f, r.Y + 4.5f) });
            }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(24, 0, Width - 24, Height), Enabled ? Theme.Muted : Theme.Mix(Theme.Card, Theme.Muted, 0.5),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
}

class Slider : Painted
{
    public float Min = 0.2f, Max = 2.5f, Value = 1.2f;
    public event EventHandler Changed;
    bool drag;
    public Slider(string text) { Text = text; Height = 24; Cursor = Cursors.Hand; }
    Rectangle Track { get { return new Rectangle(76, Height / 2 - 2, Math.Max(10, Width - 84), 4); } }
    void Set(int x)
    {
        var t = Track;
        Value = Min + (Max - Min) * Math.Max(0, Math.Min(1, (x - t.X) / (float)t.Width));
        Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty);
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); drag = true; Set(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Set(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var t = Track;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, 76, Height), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        Theme.Box(g, t, Theme.Field, Color.Empty, 2);
        float k = (Value - Min) / (Max - Min);
        int x = t.X + (int)(t.Width * k);
        Theme.Box(g, new Rectangle(t.X, t.Y, Math.Max(4, x - t.X), 4), Theme.Accent, Color.Empty, 2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, x - 7, Height / 2 - 7, 14, 14);
    }
}

// drop zone for the sheet: dashed frame, text or the picture
class DropZone : Painted
{
    public bool Over;
    Image img;
    public DropZone() { Height = 128; Cursor = Cursors.Hand; }
    public Image Picture
    {
        get { return img; }
        set
        {
            if (img != null) img.Dispose();
            img = value;
            Height = img == null ? 128 : Math.Max(110, Math.Min(220, (int)(Width * (double)img.Height / Math.Max(1, img.Width)) + 16));
            if (Parent != null) Parent.PerformLayout();
            Invalidate();
        }
    }
    protected override void OnMouseEnter(EventArgs e) { Over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var p = Theme.Round(r, 9))
        using (var pen = new Pen(Over ? Theme.Accent : Theme.Hex(0x4a4d55), 2f) { DashPattern = new[] { 4f, 3f } })
            g.DrawPath(pen, p);
        if (img != null)
        {
            var box = new Rectangle(8, 8, Width - 16, Height - 16);
            double s = Math.Min(box.Width / (double)img.Width, box.Height / (double)img.Height);
            int w = (int)(img.Width * s), h = (int)(img.Height * s);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var dst = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            using (var clip = Theme.Round(dst, 6)) { g.SetClip(clip); g.DrawImage(img, dst); g.ResetClip(); }
            return;
        }
        var mid = Height / 2;
        TextRenderer.DrawText(g, "Glisse ta planche ici", Theme.Bold, new Rectangle(0, mid - 22, Width, 22), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, "ou clique pour choisir une image", Font, new Rectangle(0, mid + 2, Width, 22),
            Over ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }
}

// text field: a borderless TextBox inside a rounded frame
class Field : Panel
{
    public readonly TextBox Box = new TextBox();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    string hint;
    public Field(string placeholder)
    {
        hint = placeholder;
        Height = 34;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Field;
        Box.BorderStyle = BorderStyle.None; Box.BackColor = Theme.Field; Box.ForeColor = Theme.Text; Box.Font = Theme.Body;
        Controls.Add(Box);
        Box.GotFocus += (s, e) => Invalidate();
        Box.LostFocus += (s, e) => Invalidate();
        Box.HandleCreated += (s, e) => SendMessage(Box.Handle, 0x1501, (IntPtr)1, hint);   // EM_SETCUEBANNER
        Click += (s, e) => Box.Focus();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        Box.SetBounds(10, (Height - Box.PreferredHeight) / 2 + 1, Width - 20, Box.PreferredHeight);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        Theme.Box(e.Graphics, ClientRectangle, Theme.Field, Box.Focused ? Theme.Accent : Theme.FieldLine, 7);
    }
}

// drop-down list in the theme
class Picker : FlatButton
{
    public List<string> Items = new List<string>();
    public List<string> Labels = new List<string>();
    int sel = -1;
    public event EventHandler Changed;
    public Picker() : base("") { }
    public int Index
    {
        get { return sel; }
        set { sel = value; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); }
    }
    public string Value { get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; } }
    public void SetItems(List<string> items, List<string> labels) { Items = items; Labels = labels ?? items; sel = -1; Invalidate(); }
    protected override string Shown { get { return sel >= 0 && sel < Labels.Count ? Labels[sel] : ""; } }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Box(e.Graphics, ClientRectangle, Enabled ? Theme.Field : Theme.Mix(Under, Theme.Field, 0.45), Theme.FieldLine, 7);
        TextRenderer.DrawText(e.Graphics, Shown, Font, new Rectangle(10, 0, Width - 34, Height), Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, "▾", Font, new Rectangle(Width - 26, 0, 20, Height), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Items.Count == 0) return;
        // longue liste (presets OBody : des centaines) : une ListBox, instantanée ; un menu créerait un élément Windows
        // par choix, et l'application ne répondait plus pendant plusieurs secondes (18/11)
        if (Items.Count > 30) { ShowList(); return; }
        var menu = new ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, Font = Theme.Body,
                                          BackColor = Theme.Field, ForeColor = Theme.Text, MaximumSize = new Size(Width, 420) };
        for (int i = 0; i < Items.Count; i++)
        {
            int k = i;
            var it = new ToolStripMenuItem(Labels[i]) { ForeColor = Theme.Text, Checked = i == sel, AutoSize = false,
                                                        Width = Width - 4, Height = 28 };
            it.Click += (s, a) => Index = k;
            menu.Items.Add(it);
        }
        menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point(0, Height + 2));
    }

    /// <summary>Ouvre la liste comme un clic (captures de test).</summary>
    public void Open() { OnClick(EventArgs.Empty); }

    void ShowList()
    {
        const int row = 28;
        var lb = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = row, BorderStyle = BorderStyle.None, IntegralHeight = false,
                               BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Body,
                               Size = new Size(Math.Max(120, Width - 4), Math.Min(420, Items.Count * row + 2)) };
        var labels = Labels.Count == Items.Count ? Labels : Items;
        lb.BeginUpdate();
        lb.Items.AddRange(labels.Cast<object>().ToArray());
        lb.EndUpdate();
        if (sel >= 0 && sel < Items.Count) { lb.SelectedIndex = sel; lb.TopIndex = Math.Max(0, sel - 5); }
        lb.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            bool on = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(on ? Theme.Mix(Theme.Field, Theme.Accent, 0.25) : Theme.Field)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, (e.Index == sel ? "✓  " : "") + labels[e.Index], Theme.Body,
                new Rectangle(e.Bounds.X + (e.Index == sel ? 6 : 22), e.Bounds.Y, e.Bounds.Width - 28, e.Bounds.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        var host = new ToolStripControlHost(lb) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = lb.Size };
        var drop = new ToolStripDropDown { Padding = new Padding(1), Renderer = new DarkMenu(), BackColor = Theme.FieldLine };
        drop.Items.Add(host);
        void Pick() { int k = lb.SelectedIndex; drop.Close(); if (k >= 0) Index = k; }
        lb.MouseMove += (s, e) => { int i = lb.IndexFromPoint(e.Location); if (i >= 0 && i != lb.SelectedIndex) lb.SelectedIndex = i; };
        lb.MouseClick += (s, e) => { if (lb.IndexFromPoint(e.Location) >= 0) Pick(); };
        lb.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; Pick(); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; drop.Close(); }
        };
        drop.Opened += (s, a) => { Theme.DarkScroll(lb); lb.Focus(); };
        drop.Closed += (s, a) => BeginInvoke(new Action(drop.Dispose));
        drop.Show(this, new Point(0, Height + 2));
    }
}

class DarkMenu : ToolStripProfessionalRenderer
{
    class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Field; } }
        public override Color MenuItemSelected { get { return Theme.Mix(Theme.Field, Theme.Accent, 0.25); } }
        public override Color MenuItemBorder { get { return Theme.Accent; } }
        public override Color MenuBorder { get { return Theme.FieldLine; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Field; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Field; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Field; } }
    }
    public DarkMenu() : base(new Colors()) { }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var mi = e.Item as ToolStripMenuItem;
        e.TextColor = mi != null && mi.Checked ? Theme.Accent : Theme.Text;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }
}

// generation steps: done (green tick), current (turning ring), to come (empty ring)
class StepsView : Painted
{
    public List<string> Names = new List<string>();
    public int Current = -1;
    public bool AllDone;
    float angle;
    readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer { Interval = 40 };
    public StepsView()
    {
        spin.Tick += (s, e) => { angle = (angle + 18) % 360; Invalidate(); };
    }
    public void Set(List<string> names, int current, bool done)
    {
        Names = names; Current = current; AllDone = done;
        Height = names.Count * 25;
        spin.Enabled = !done && current >= 0;
        if (Parent != null) Parent.PerformLayout();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < Names.Count; i++)
        {
            int y = i * 25;
            var dot = new RectangleF(1, y + 4, 16, 16);
            bool done = AllDone || i < Current, now = !AllDone && i == Current;
            if (done)
            {
                using (var b = new SolidBrush(Theme.Ok)) g.FillEllipse(b, dot);
                using (var p = new Pen(Theme.Bg, 2f))
                    g.DrawLines(p, new[] { new PointF(dot.X + 4.5f, dot.Y + 8.5f), new PointF(dot.X + 7f, dot.Y + 11f), new PointF(dot.X + 12f, dot.Y + 5.5f) });
            }
            else if (now)
            {
                using (var p = new Pen(Theme.Hex(0x4a4d55), 2f)) g.DrawEllipse(p, dot);
                using (var p = new Pen(Theme.Accent, 2f)) g.DrawArc(p, dot, angle, 270);
            }
            else using (var p = new Pen(Theme.Hex(0x4a4d55), 2f)) g.DrawEllipse(p, dot);
            TextRenderer.DrawText(g, Names[i], now ? Theme.Bold : Font, new Rectangle(26, y, Width - 26, 24),
                done || now ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
    protected override void Dispose(bool d) { if (d) spin.Dispose(); base.Dispose(d); }
}

class ProgressLine : Painted
{
    float v;
    public ProgressLine() { Height = 6; }
    public float Value { get { return v; } set { v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Box(e.Graphics, ClientRectangle, Theme.Field, Color.Empty, 3);
        if (v > 0) Theme.Box(e.Graphics, new Rectangle(0, 0, Math.Max(6, (int)(Width * v)), Height), Theme.Accent, Color.Empty, 3);
    }
}

// two texts on one line (elapsed time / estimate)
class TwoText : Painted
{
    public string Left2 = "", Right2 = "";
    public TwoText() { Height = 18; Font = Theme.Small; }
    public void Set(string l, string r) { Left2 = l; Right2 = r; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Left2, Font, ClientRectangle, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, Right2, Font, ClientRectangle, Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// header: logo, name, subtitle (the help button is a child)
class Header : Painted
{
    public Header() { Height = 46; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Box(g, new Rectangle(0, 6, 34, 34), Theme.Accent, Color.Empty, 9);
        TextRenderer.DrawText(g, "F", new Font("Segoe UI Semibold", 13f), new Rectangle(0, 6, 34, 34), Theme.Bg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "FacePhoto", Theme.Title, new Point(44, 2), Theme.Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Planche de portraits → preset UBE", Theme.Small, new Point(45, 27), Theme.Muted, TextFormatFlags.NoPadding);
    }
}

// card title in small capitals
class CardTitle : Painted
{
    public CardTitle(string t) { Text = t; Height = 16; Font = Theme.H2; ForeColor = Theme.Accent; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text.ToUpperInvariant(), Font, ClientRectangle, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// scrolling side panel without the light Windows scroll bar: wheel + thin gold thumb
class SideScroll : Panel
{
    public readonly VStack Content = new VStack { Pad = 14, Gap = 12 };
    int offset;
    bool dragThumb; int dragY, dragOff;
    public SideScroll()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel; Content.BackColor = Theme.Panel;
        Controls.Add(Content);
        Content.Resize += (s, e) => Clamp();
    }
    // Windows scrolls a panel to the focused control by itself: our offset decides, nothing else
    protected override Point ScrollToControl(Control activeControl) { return DisplayRectangle.Location; }
    int MaxOff { get { return Math.Max(0, Content.Height - Height); } }
    void Clamp()
    {
        offset = Math.Max(0, Math.Min(MaxOff, offset));
        Content.Top = -offset;
        Invalidate();
    }
    public void ScrollBy(int delta) { offset -= delta * 60 / 120; Clamp(); }
    public void ScrollTo(Control c)
    {
        int y = 0; for (var p = c; p != null && p != Content; p = p.Parent) y += p.Top;
        if (y < offset || y + c.Height > offset + Height) { offset = Math.Max(0, y - 12); Clamp(); }
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        Content.SetBounds(0, -offset, Width - 8, Content.Height);
        Clamp();
    }
    public int Offset { get { return offset; } }
    Rectangle Thumb
    {
        get
        {
            if (MaxOff == 0) return Rectangle.Empty;
            int h = Math.Max(30, Height * Height / Content.Height);
            int y = (int)((Height - h) * (offset / (double)MaxOff));
            return new Rectangle(Width - 6, y + 2, 4, h - 4);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var t = Thumb;
        if (!t.IsEmpty) Theme.Box(e.Graphics, t, Theme.Mix(Theme.Panel, Theme.Accent, 0.55), Color.Empty, 2);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.X >= Width - 8 && MaxOff > 0) { dragThumb = true; dragY = e.Y; dragOff = offset; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragThumb) return;
        var t = Thumb;
        offset = dragOff + (int)((e.Y - dragY) * MaxOff / (double)Math.Max(1, Height - t.Height));
        Clamp();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragThumb = false; }
}

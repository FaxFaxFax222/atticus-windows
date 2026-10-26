// Draw on screen, ported from atticus-mac's ScreenDrawing. atticus.py --draw sends "✎ {json}": steps of marks in Windows
// screen pixels. Each display gets a click-through layer for the marks; a card beside them carries the step's words with
// Back / Next. Locate steps move on when the user clicks their hit area; whiteboard steps move on by themselves.
// Esc, Close or the next drawing clears it. Screenshots leave the drawing out, like the box.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using static Atticus.Native;

namespace Atticus;

// A borderless window painted from a 32-bit bitmap with per-pixel alpha (UpdateLayeredWindow): soft edges, translucent fills.
sealed class Layer : Form
{
    bool through;
    public Action<Point> Clicked, Dragged, Released;   // local pixels
    public Func<Point, bool> Hot;       // a button there: the hand cursor
    public Cursor Resting = Cursors.Default;
    public bool IsClickThrough => through;
    public void SetClickThrough(bool on)   // only while the pointer is over a mark does a drawing take the click
    {
        if (on == through || !IsHandleCreated) { through = on; return; }
        through = on;
        var ex = GetWindowLong(Handle, -20);
        SetWindowLong(Handle, -20, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
    }
    public Layer(Rectangle bounds, bool clickThrough)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        Bounds = bounds; through = clickThrough;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var c = base.CreateParams; c.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | (through ? WS_EX_TRANSPARENT : 0); return c; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Environment.GetEnvironmentVariable("ATTICUS_CAPTURABLE") != "1") SetWindowDisplayAffinity(Handle, 0x11);   // never in Atticus's own screenshots
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE: a click never takes focus from the user's app
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Clicked?.Invoke(e.Location); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = Hot?.Invoke(e.Location) == true ? Cursors.Hand : Resting;
        if (e.Button == MouseButtons.Left) Dragged?.Invoke(e.Location);
    }
    protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Released?.Invoke(e.Location); }

    public void Present(Bitmap bmp)
    {
        if (!IsHandleCreated) CreateHandle();
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height }; var src = new POINT(); var dst = new POINT { X = Left, Y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };   // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);   // ULW_ALPHA
        }
        finally { SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE size, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr o);
}

static class ScreenDrawing
{
    sealed class Shape { public string Kind, Color = "red", Size = "m", Text; public RectangleF? Rect; public PointF? At; public PointF[] Points; public bool Fill; }
    sealed class Step { public string Say = ""; public List<Shape> Shapes = new(); public bool Wait; public RectangleF? Hit; public double Timeout = 60; }
    sealed record Line(string Text, Color Color, Font Font, RectangleF Rect, int Step);
    sealed record Board(RectangleF Frame, float Pad, List<Line> Lines);

    static List<Step> steps;
    static JsonElement raw;
    static bool teaching, manual, spoke;
    static int index;
    static readonly List<(Screen screen, Layer layer)> layers = new();
    static Layer card;
    static readonly List<(RectangleF area, Action act)> buttons = new();   // the card's, in card pixels
    static Board board;
    static System.Windows.Forms.Timer timer;
    static DateTime deadline, ready, advanceAt;
    static PointF? previousPointer;
    static bool hooked;
    public static Func<Point, bool> Ours = _ => false;   // the box and its cards: their clicks are not the user's
    public static Action<RectangleF, string> Picked;      // a click on one of the marks: its screen area and what it is, to ask about
    static readonly List<(GraphicsPath area, int step, int shape)> hits = new();   // screen pixels: what a click on each mark selects
    static readonly List<(Screen screen, Layer layer, Bitmap canvas)> input = new();   // Point at your screen
    static readonly List<PointF> stroke = new();
    static Action<List<PointF>> strokeDone;
    static DateTime strokeDrawn;
    public static bool Inputting => strokeDone != null;
    public static bool HasInput => input.Count > 0;
    public static Action Shown;   // layers went up: the box comes back on top of them
    public static bool Takes(Point p) => Inputting || OnMark(p);   // this click is the drawing's, not a click away from the box

    public static bool Active => steps != null;
    public static object Summary => Active ? new { steps = raw.GetProperty("steps"), shown = index + 1 } : null;   // the next ask builds on it

    static readonly Dictionary<string, Color> Colors = new()
    {
        ["red"] = Color.FromArgb(255, 59, 48), ["blue"] = Color.FromArgb(10, 132, 255), ["green"] = Color.FromArgb(52, 199, 89),
        ["yellow"] = Color.FromArgb(255, 204, 0), ["white"] = Color.White,
    };

    // ---- receiving ----
    public static void Receive(string json)
    {
        string receipt = null, result = "The drawing could not be shown.";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            receipt = r.TryGetProperty("receipt", out var rc) ? rc.GetString() : null;
            if (!Prefs.Bool("draw")) { result = "Screen drawings are off"; return; }
            var parsed = r.GetProperty("steps").EnumerateArray().Select(ParseStep).ToList();
            if (parsed.Count is < 1 or > 12 || parsed.Any(s => s.Shapes.Count == 0)) return;
            if (!OnDisplays(parsed)) { result = "A mark is off every screen. Read --look again."; return; }
            Clear();
            steps = parsed; raw = r.Clone(); index = 0; manual = false;
            teaching = r.TryGetProperty("teaching", out var t) && t.GetBoolean();
            board = MakeBoard();
            if (!hooked) { Hooks.Clicked += (x, y) => Program.Post(() => Clicked(new Point(x, y))); hooked = true; }   // off the hook's thread time
            ShowStep();
            if (!Active) return;
            timer = new System.Windows.Forms.Timer { Interval = 150 }; timer.Tick += (_, _) => Tick(); timer.Start();
            result = "shown";
        }
        catch (Exception e) { Program.Err("drawing: " + e.Message); }
        finally
        {
            if (!string.IsNullOrEmpty(receipt)) try { File.WriteAllText(receipt + ".tmp", result); File.Move(receipt + ".tmp", receipt, true); } catch { }
        }
    }

    static Step ParseStep(JsonElement s)
    {
        var step = new Step
        {
            Say = s.TryGetProperty("say", out var v) ? v.GetString() ?? "" : "",
            Wait = s.TryGetProperty("wait", out var w) && w.GetBoolean(),
            Hit = s.TryGetProperty("hit", out var h) ? Rect(h) : null,
            Timeout = s.TryGetProperty("timeout", out var to) ? to.GetDouble() : 60,
        };
        foreach (var x in s.GetProperty("shapes").EnumerateArray())
            step.Shapes.Add(new Shape
            {
                Kind = x.GetProperty("kind").GetString(),
                Color = x.TryGetProperty("color", out var c) ? c.GetString() : "red",
                Size = x.TryGetProperty("size", out var z) ? z.GetString() : "m",
                Text = x.TryGetProperty("text", out var tx) ? tx.GetString() : null,
                Fill = x.TryGetProperty("fill", out var f) && f.GetBoolean(),
                Rect = x.TryGetProperty("rect", out var rr) ? Rect(rr) : null,
                At = x.TryGetProperty("at", out var a) ? Pt(a) : null,
                Points = x.TryGetProperty("points", out var ps) ? ps.EnumerateArray().Select(Pt).ToArray() : null,
            });
        return step;
    }
    static PointF Pt(JsonElement e) => new((float)e[0].GetDouble(), (float)e[1].GetDouble());
    static RectangleF Rect(JsonElement e) => new((float)e[0].GetDouble(), (float)e[1].GetDouble(), (float)e[2].GetDouble(), (float)e[3].GetDouble());

    static bool OnDisplays(List<Step> s)
    {
        var all = Screen.AllScreens.Select(x => x.Bounds).ToArray();
        bool On(PointF p) => all.Any(b => p.X >= b.Left && p.X <= b.Right && p.Y >= b.Top && p.Y <= b.Bottom);
        bool Box(RectangleF r) => On(r.Location) && On(new PointF(r.Right, r.Bottom));
        return s.All(st => (st.Hit is not { } h || Box(h)) && st.Shapes.All(x =>
            (x.At is not { } a || On(a)) && (x.Rect is not { } r || Box(r)) && (x.Points ?? Array.Empty<PointF>()).All(On)));
    }

    // ---- what the user does ----
    public static void Clear()
    {
        timer?.Stop(); timer?.Dispose(); timer = null;
        foreach (var (_, l) in layers) l.Close();
        layers.Clear();
        card?.Close(); card = null; buttons.Clear();
        if (spoke) Voice.Shush();
        ClearHits();
        foreach (var l in board?.Lines ?? new()) l.Font.Dispose();
        steps = null; board = null; previousPointer = null; spoke = false;
    }

    // "next", "back" or "done" said or typed while a guide is up: no AI needed.
    public static bool StepWord(string words) => Active && words is "next" or "next step" or "back" or "go back" or "previous" or "done" or "close" or "close it";
    public static string Command(string words)
    {
        if (!StepWord(words)) return null;
        if (words is "back" or "go back" or "previous") { Back(); return "Back a step"; }
        if (words is "done" or "close" or "close it") { Clear(); return "Closed the drawing"; }
        var last = index + 1 >= steps.Count; Next(); return last ? "Closed the drawing" : $"Step {index + 1} of {steps.Count}";
    }

    static void Next() { if (!Active) return; manual = true; if (index + 1 < steps.Count) { index++; ShowStep(); } else Clear(); }
    static void Back() { if (!Active || index == 0) return; manual = true; index--; ShowStep(false); }
    static void Advance() { if (!Active) return; if (index + 1 < steps.Count) { index++; ShowStep(); } else if (!teaching) Clear(); }

    static void Clicked(Point p)
    {
        if (!Active || DateTime.Now < ready || teaching || Inputting || Ours(p) || card != null && card.Bounds.Contains(p) || OnMark(p)) return;   // a click on a mark selects it
        var step = steps[index];
        if (step.Wait && step.Hit is { } hit && !hit.Contains(p)) return;
        Advance();
    }

    static void Tick()
    {
        if (!Active) return;
        HoverMarks();
        if (!teaching && DateTime.Now >= deadline) { Clear(); return; }
        if (!steps[index].Wait && !manual && index + 1 < steps.Count && DateTime.Now >= advanceAt && !Voice.Speaking) Advance();
    }

    // ---- the user's clicks on marks ----
    static void ClearHits() { foreach (var h in hits) h.area.Dispose(); hits.Clear(); }
    static bool OnMark(Point p) => layers.Any(l => !l.layer.IsClickThrough && l.layer.Bounds.Contains(p));

    // Marks take a click only while the pointer is on one; everywhere else clicks reach the user's apps.
    static void HoverMarks()
    {
        if (Control.MouseButtons != MouseButtons.None || !GetCursorPos(out var c)) return;
        var p = new Point(c.X, c.Y);
        var over = !Ours(p) && !(card?.Bounds.Contains(p) ?? false) && !Inputting && hits.Any(h => h.area.IsVisible(p));
        foreach (var (_, l) in layers) l.SetClickThrough(!(over && l.Bounds.Contains(p)));
    }

    static void Pick(Point p)
    {
        var h = hits.LastOrDefault(x => x.area.IsVisible(p));
        if (h.area == null || h.step < 0 || h.step >= steps.Count || h.shape < 0 || h.shape >= steps[h.step].Shapes.Count) return;
        manual = true; if (spoke) { Voice.Shush(); spoke = false; }   // the user is asking about it: no more talking or stepping on
        var step = steps[h.step]; var shape = step.Shapes[h.shape];
        Picked?.Invoke(h.area.GetBounds(), $"{shape.Kind}{(shape.Text != null ? $" \"{shape.Text}\"" : "")} in step {h.step + 1} of {steps.Count}{(step.Say != "" ? ": " + step.Say : "")}");
    }

    // ---- Point at your screen: the user marks a spot (click) or a part (circle, scribble) and asks about it ----
    public static void BeginInput(Action<List<PointF>> done)
    {
        ClearInput(); stroke.Clear(); strokeDone = done;
        foreach (var s in Screen.AllScreens)
        {
            var l = new Layer(s.Bounds, false) { Resting = Cursors.Cross };
            var canvas = new Bitmap(s.Bounds.Width, s.Bounds.Height, PixelFormat.Format32bppArgb);
            l.Clicked = q => { if (!Inputting) return; stroke.Clear(); stroke.Add(new PointF(q.X + l.Left, q.Y + l.Top)); RenderInput(true); };   // a new stroke replaces the last one
            l.Dragged = q =>
            {
                if (!Inputting || stroke.Count == 0 || stroke.Count >= 256) return;
                var g = new PointF(q.X + l.Left, q.Y + l.Top);
                if (Math.Abs(g.X - stroke[^1].X) + Math.Abs(g.Y - stroke[^1].Y) >= 3) { stroke.Add(g); RenderInput(false); }
            };
            l.Released = _ => { if (!Inputting || stroke.Count == 0) return; RenderInput(true); var d = strokeDone; FinishInput(); d(stroke.ToList()); };
            input.Add((s, l, canvas));
            Fill(canvas, true); l.Present(canvas); l.Show();
        }
        Shown?.Invoke();
    }
    static void Fill(Bitmap b, bool catching)   // alpha 1 everywhere: invisible, but a layered window only gets clicks where it is not fully clear
    {
        using var g = Graphics.FromImage(b); g.CompositingMode = CompositingMode.SourceCopy; g.Clear(Color.FromArgb(catching ? 1 : 0, 0, 0, 0));
    }
    static void RenderInput(bool now)
    {
        if (stroke.Count == 0 || !now && (DateTime.Now - strokeDrawn).TotalMilliseconds < 30) return;   // a fast scribble redraws at most ~30 times a second
        strokeDrawn = DateTime.Now;
        foreach (var (s, l, canvas) in input)
        {
            if (!s.Bounds.IntersectsWith(Rectangle.Round(Bounds(stroke, 20)))) continue;
            Fill(canvas, Inputting);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TranslateTransform(-s.Bounds.X, -s.Bounds.Y);
                DrawMark(g, stroke, Scale(s));
            }
            l.Present(canvas);
        }
    }
    public static void DrawMark(Graphics g, List<PointF> points, float k)   // the user's mark: a red dot for a click, a red line for a stroke
    {
        var red = Colors["red"];
        if (points.Count == 1)
        {
            var r = new RectangleF(points[0].X - 8 * k, points[0].Y - 8 * k, 16 * k, 16 * k);
            using var pen = new Pen(red, 3 * k); g.DrawEllipse(pen, r);
            return;
        }
        using var shadow = new Pen(Color.FromArgb(115, 0, 0, 0), 5 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var line = new Pen(red, 3 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(shadow, points.ToArray()); g.DrawLines(line, points.ToArray());
    }
    public static RectangleF Bounds(List<PointF> points, float pad) =>
        RectangleF.FromLTRB(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad);
    static void FinishInput()   // the mark stays until the ask is sent; clicks reach the user's apps again
    {
        strokeDone = null;
        foreach (var (_, l, _) in input) { l.SetClickThrough(true); l.Resting = Cursors.Default; }
        RenderInput(true);   // without the click-catching tint
    }
    public static void ClearInput()   // the user's own mark only; a guide stays
    {
        strokeDone = null; stroke.Clear();
        foreach (var (_, l, canvas) in input) { l.Close(); canvas.Dispose(); }
        input.Clear();
    }

    // ---- showing a step ----
    static void ShowStep(bool forward = true)
    {
        card?.Close(); card = null; buttons.Clear();
        if (spoke) { Voice.Shush(); spoke = false; }
        var step = steps[index];
        var screens = Screen.AllScreens;
        if (layers.Count != screens.Length || layers.Any(l => !screens.Any(s => s.Bounds == l.screen.Bounds)))
        {
            foreach (var (_, l) in layers) l.Close();
            layers.Clear();
            foreach (var s in screens) layers.Add((s, new Layer(s.Bounds, true)));
        }
        RectangleF marks = RectangleF.Empty, boardFrame = RectangleF.Empty;
        var earlier = new List<RectangleF>();
        ClearHits();
        var page = board == null ? null : Page(board, index);
        foreach (var (screen, layer) in layers)
        {
            var b = screen.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(-b.X, -b.Y);   // everything below is in screen pixels
                var k = Scale(screen);
                if (teaching) for (int i = 0; i < index; i++) Add(earlier, Render(g, steps[i].Shapes, k, b, Array.Empty<RectangleF>(), true, i));
                if (page != null) { RenderBoard(g, page.Value.frame, page.Value.lines, k); boardFrame = page.Value.frame; }
                marks = Union(marks, Render(g, step.Shapes, k, b, step.Hit is { } h ? new[] { h } : Array.Empty<RectangleF>(), false, index));
            }
            layer.Present(bmp);
            if (!layer.Visible) layer.Show();
            layer.Resting = Cursors.Hand;   // it only takes clicks while the pointer is on a mark
            layer.Clicked = q => Pick(new Point(q.X + layer.Left, q.Y + layer.Top));
        }
        Shown?.Invoke();
        if (!boardFrame.IsEmpty) { if (page.Value.lines.Any(l => l.Step == index)) marks = Union(marks, boardFrame); else earlier.Add(boardFrame); }
        if (marks.IsEmpty) { Clear(); return; }
        if (step.Shapes.LastOrDefault(s => s.Kind == "pointer")?.At is { } pp) previousPointer = pp;
        var now = DateTime.Now;
        deadline = teaching ? DateTime.MaxValue : now.AddSeconds(step.Timeout);
        ready = now.AddSeconds(0.35);
        advanceAt = now.AddSeconds(Math.Clamp(1.5 + step.Say.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 3.5, 3, 12));   // long enough to read the card
        if (step.Say != "" || steps.Count > 1) ShowCard(step.Say, marks, earlier.Concat(step.Hit is { } hh ? new[] { hh } : Array.Empty<RectangleF>()).ToList());
        if (step.Say != "" && Prefs.Bool("speak")) { Voice.Say(step.Say, () => { }); spoke = true; }
    }

    static void Add(List<RectangleF> list, RectangleF r) { if (!r.IsEmpty) list.Add(r); }
    static RectangleF Union(RectangleF a, RectangleF b) => a.IsEmpty ? b : b.IsEmpty ? a : RectangleF.Union(a, b);

    // Physical pixels per point on that display (1 at 100 %, 1.5 at 150 %).
    public static float Scale(Screen s)
    {
        try { var m = MonitorFromPoint(new POINT { X = s.Bounds.Left + 1, Y = s.Bounds.Top + 1 }, 2); if (GetDpiForMonitor(m, 0, out var x, out _) == 0) return x / 96f; } catch { }
        return 1;
    }
    [DllImport("user32")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
    static Screen ScreenAt(PointF p) => Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(Point.Round(p))) ?? Screen.PrimaryScreen;

    // ---- marks ----
    // Marks sit around or beside what they mark, never on it: `gap` is the clear space they keep.
    static RectangleF Ring(RectangleF r, bool circle, float gap)
    {
        var o = RectangleF.Inflate(r, gap + 1.5f, gap + 1.5f);
        return circle ? RectangleF.Inflate(o, o.Width * (MathF.Sqrt(2) - 1) / 2, o.Height * (MathF.Sqrt(2) - 1) / 2) : o;
    }
    static PointF ArrowTip(PointF a, PointF b, IEnumerable<RectangleF> targets, float gap)   // the head stops at the target's edge
    {
        var box = targets.Select(t => RectangleF.Inflate(t, gap + 2, gap + 2)).FirstOrDefault(t => t.Contains(b) && !t.Contains(a));
        if (box.IsEmpty) return b;
        float lo = 0, hi = 1;
        PointF At(float s) => new(a.X + (b.X - a.X) * s, a.Y + (b.Y - a.Y) * s);
        for (int i = 0; i < 24; i++) { var m = (lo + hi) / 2; if (box.Contains(At(m))) hi = m; else lo = m; }
        return At(lo);
    }
    static PointF Beside(PointF p, IEnumerable<RectangleF> targets, float gap)   // a pointer on a target rests at its lower-right corner
    {
        foreach (var t in targets) if (t.Contains(p)) return new PointF(t.Right + gap, t.Bottom + gap);
        return p;
    }
    static RectangleF LabelBox(SizeF size, PointF p, List<RectangleF> keepOff, RectangleF area, float gap)
    {
        var spots = new List<PointF> { new(p.X + 12, p.Y - size.Height - 4), new(p.X + 12, p.Y + 8), new(p.X - 12 - size.Width, p.Y - size.Height - 4), new(p.X - 12 - size.Width, p.Y + 8) };
        foreach (var t in keepOff.Where(t => t.Contains(p)))   // anchored on a target: beside it instead
            spots.AddRange(new PointF[] { new(t.Right + 4, t.Top + t.Height / 2 - size.Height / 2), new(t.Left - 4 - size.Width, t.Top + t.Height / 2 - size.Height / 2),
                                          new(t.Left + t.Width / 2 - size.Width / 2, t.Top - 4 - size.Height), new(t.Left + t.Width / 2 - size.Width / 2, t.Bottom + 4) });
        bool Clear(RectangleF box) => area.Contains(box) && !keepOff.Any(k => k.IntersectsWith(box) && !RectangleF.Inflate(k, -2 * gap, -2 * gap).Contains(box));
        foreach (var s in spots) { var box = new RectangleF(s, size); if (Clear(box)) return box; }
        return new RectangleF(Math.Max(area.Left, Math.Min(area.Right - size.Width, spots[0].X)), Math.Max(area.Top, Math.Min(area.Bottom - size.Height, spots[0].Y)), size.Width, size.Height);
    }
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    static Color Fade(Color c, double a) => Color.FromArgb((int)Math.Round(c.A * a), c);
    static Color Darker(Color c, float f) => Color.FromArgb(c.A, (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

    // Draws one step's marks (not its text lines) in screen pixels; returns what it covered on this display.
    static RectangleF Render(Graphics g, List<Shape> shapes, float k, Rectangle screen, RectangleF[] avoid, bool dim, int stepIndex)
    {
        void Hit(GraphicsPath area, Shape s) { if (RectangleF.Intersect(area.GetBounds(), screen) != RectangleF.Empty) hits.Add((area, stepIndex, shapes.IndexOf(s))); else area.Dispose(); }
        GraphicsPath Box(RectangleF r) { var a = new GraphicsPath(); a.AddRectangle(r); return a; }
        float gap = 6 * k, width = 3 * k;
        var targets = shapes.Where(s => s.Rect.HasValue).Select(s => s.Rect.Value).Concat(avoid).ToList();
        var keepOff = targets.Select(t => RectangleF.Inflate(t, gap, gap)).Concat(shapes.Where(s => s.Rect.HasValue).Select(s => Ring(s.Rect.Value, s.Kind == "circle", gap))).ToList();
        var drawn = RectangleF.Empty;
        double alpha = dim ? 0.5 : 1;
        void Covered(RectangleF r) { var i = RectangleF.Intersect(r, screen); if (!i.IsEmpty) drawn = Union(drawn, i); }
        bool lines = shapes.Any(s => s.Kind is "arrow" or "line" or "path");
        foreach (var shape in shapes.Where(s => s.Kind != "text"))
        {
            var color = Fade(Colors.GetValueOrDefault(shape.Color ?? "red", Colors["red"]), alpha);
            using var path = new GraphicsPath();
            if (shape.Rect is { } r)
            {
                var frame = shape.Fill ? r : Ring(r, shape.Kind == "circle", gap);
                if (shape.Kind == "circle") path.AddEllipse(frame); else using (var rounded = Rounded(frame, 6 * k)) path.AddPath(rounded, false);
            }
            else if (shape.Points is { Length: >= 2 } pts)
            {
                pts = (PointF[])pts.Clone();
                if (shape.Kind == "arrow") pts[^1] = ArrowTip(pts[0], pts[^1], targets, gap);
                path.AddLines(pts);
                if (shape.Kind == "arrow")
                {
                    var tip = pts[^1]; var angle = Math.Atan2(tip.Y - pts[0].Y, tip.X - pts[0].X);
                    foreach (var d in new[] { -0.5, 0.5 })
                    {
                        path.StartFigure();
                        path.AddLine(tip, new PointF(tip.X - 13 * k * (float)Math.Cos(angle + d), tip.Y - 13 * k * (float)Math.Sin(angle + d)));
                    }
                }
            }
            else if (shape.At is { } at)
            {
                if (shape.Kind == "label")
                {
                    if (!screen.Contains(Point.Round(at))) continue;
                    bool light = shape.Color is "green" or "yellow" or "white";
                    using var font = new Font("Segoe UI Semibold", (shape.Size switch { "s" => 12, "l" => 19, _ => 15 }) * k, GraphicsUnit.Pixel);
                    var text = g.MeasureString(shape.Text ?? "", font, (int)(screen.Width - 30 * k));
                    var size = new SizeF(text.Width + 16 * k, text.Height + 10 * k);
                    // a label set near a mark but off it sits right beside that mark; with an arrow or line it stays where the model put it
                    var anchor = at;
                    if (!lines && targets.Count > 0)
                    {
                        var near = targets.Select(t => new PointF(Math.Clamp(at.X, t.Left, t.Right), Math.Clamp(at.Y, t.Top, t.Bottom)))
                                          .OrderBy(q => Math.Pow(q.X - at.X, 2) + Math.Pow(q.Y - at.Y, 2)).First();
                        if (Math.Sqrt(Math.Pow(near.X - at.X, 2) + Math.Pow(near.Y - at.Y, 2)) < 160 * k) anchor = near;
                    }
                    var box = LabelBox(size, anchor, keepOff, RectangleF.Inflate(screen, -6 * k, -6 * k), gap);
                    using (var bg = Rounded(box, 7 * k)) using (var fill = new SolidBrush(light ? color : Darker(color, 0.2f))) g.FillPath(fill, bg);
                    using (var ink = new SolidBrush(Fade(light ? Color.Black : Color.White, alpha)))
                        g.DrawString(shape.Text ?? "", font, ink, new RectangleF(box.X + 8 * k, box.Y + 5 * k, box.Width - 14 * k, box.Height - 8 * k));
                    Covered(box); Hit(Box(box), shape); continue;
                }
                var p = Beside(at, targets, gap);
                var dot = new RectangleF(p.X - 5 * k, p.Y - 5 * k, 10 * k, 10 * k);
                using (var fill = new SolidBrush(color)) g.FillEllipse(fill, dot);
                using (var ring = new Pen(Fade(Color.White, alpha), 2 * k)) g.DrawEllipse(ring, dot);
                Covered(RectangleF.Inflate(dot, k, k)); Hit(Box(RectangleF.Inflate(dot, 5 * k, 5 * k)), shape); continue;
            }
            else continue;
            if (shape.Fill) using (var fill = new SolidBrush(Fade(color, 0.18))) g.FillPath(fill, path);
            using (var shadow = new Pen(Color.FromArgb((int)(115 * alpha), 0, 0, 0), width + 2 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawPath(shadow, path);
            using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawPath(pen, path);
            Covered(RectangleF.Inflate(path.GetBounds(), width, width));
            var area = (GraphicsPath)path.Clone();   // strokes take clicks near the line only, so the target inside a ring stays clickable
            if (!shape.Fill) using (var near = new Pen(Color.Black, 12 * k)) area.Widen(near);
            Hit(area, shape);
        }
        return drawn;
    }

    // ---- the whiteboard: every text line of the guide on one dark panel, laid out once so it grows in place ----
    static float TextSize(string size, Screen s, float k) => Math.Min(32, Math.Max(22, Math.Min(28, s.Bounds.Height / k / 40))) * (size switch { "s" => 0.8f, "l" => 1.2f, _ => 1 }) * k;

    static Board MakeBoard()
    {
        var texts = steps.SelectMany((s, i) => s.Shapes.Where(x => x.Kind == "text").Select(x => (step: i, shape: x))).ToList();
        if (texts.Count == 0) return null;
        var first = texts[0].shape;
        var others = steps.SelectMany(s => s.Shapes).Where(x => x.Kind != "text").SelectMany(x => x.Rect is { } r ? new[] { r.Location, new PointF(r.Right, r.Bottom) } : x.At is { } a ? new[] { a } : x.Points ?? Array.Empty<PointF>()).ToList();
        var near = others.Count > 0 ? RectangleF.FromLTRB(others.Min(p => p.X), others.Min(p => p.Y), others.Max(p => p.X), others.Max(p => p.Y)) : RectangleF.Empty;
        var anchor = first.At ?? (near.IsEmpty ? new PointF(Screen.PrimaryScreen.Bounds.Width / 2f, Screen.PrimaryScreen.Bounds.Height / 2f) : new PointF(near.X + near.Width / 2, near.Y + near.Height / 2));
        var screen = ScreenAt(anchor); var k = Scale(screen);
        var area = RectangleF.Inflate(screen.WorkingArea, -12 * k, -12 * k);
        float bas = TextSize(null, screen, k), pad = MathF.Round(bas * 0.7f), gap = MathF.Round(bas * 0.3f), wrap = Math.Max(200 * k, Math.Min(area.Width * 0.5f, 820 * k));
        using var probe = Graphics.FromHwnd(IntPtr.Zero);
        var sized = texts.Select(t =>
        {
            var font = new Font("Segoe UI", TextSize(t.shape.Size, screen, k), GraphicsUnit.Pixel);
            return (t.shape.Text ?? "", Colors.GetValueOrDefault(t.shape.Color ?? "white", Color.White), font, size: probe.MeasureString(t.shape.Text ?? "", font, (int)wrap), t.step);
        }).ToList();
        var width = sized.Max(x => x.size.Width);
        var height = sized.Sum(x => x.size.Height + gap) - gap + 2 * pad;
        var size = new SizeF(Math.Min(area.Width, width + 2 * pad), Math.Min(area.Height, height));
        var origin = first.At is { } at ? new PointF(Math.Clamp(at.X, area.Left, area.Right - size.Width), Math.Clamp(at.Y, area.Top, area.Bottom - size.Height))
                                        : CardFrame(size, near.IsEmpty ? new RectangleF(anchor, SizeF.Empty) : near, new(), area).card.Location;
        float y = pad; var lines = new List<Line>();
        foreach (var s in sized) { lines.Add(new Line(s.Item1, s.Item2, s.font, new RectangleF(origin.X + pad, origin.Y + y, s.size.Width, s.size.Height), s.step)); y += s.size.Height + gap; }
        return new Board(new RectangleF(origin, size), pad, lines);
    }

    // Follow the active step down a long board; Back pages earlier lines into view again.
    static (RectangleF frame, List<Line> lines)? Page(Board b, int through)
    {
        var shown = b.Lines.Where(l => l.Step <= through).ToList();
        if (shown.Count == 0) return null;
        var offset = Math.Max(0, shown[^1].Rect.Bottom + b.Pad - b.Frame.Bottom);
        var view = RectangleF.Inflate(b.Frame, 0, -b.Pad);
        var lines = shown.Select(l => l with { Rect = new RectangleF(l.Rect.X, l.Rect.Y - offset, l.Rect.Width, l.Rect.Height) }).Where(l => l.Rect.IntersectsWith(view)).ToList();
        if (lines.Count == 0) return null;
        var frame = offset > 0 ? b.Frame : new RectangleF(b.Frame.X, b.Frame.Y, b.Frame.Width, lines[^1].Rect.Bottom + b.Pad - b.Frame.Y);
        return (frame, lines);
    }

    static void RenderBoard(Graphics g, RectangleF frame, List<Line> lines, float k)
    {
        using (var bg = Rounded(frame, 14 * k))
        {
            using (var fill = new SolidBrush(Color.FromArgb(240, 20, 20, 20))) g.FillPath(fill, bg);
            using (var edge = new Pen(Color.FromArgb(56, 255, 255, 255), Math.Max(1, k / 2))) g.DrawPath(edge, bg);
        }
        foreach (var l in lines)
        {
            using (var ink = new SolidBrush(l.Step < index ? Fade(l.Color, 0.7) : l.Color)) g.DrawString(l.Text, l.Font, ink, RectangleF.Inflate(l.Rect, 2, 0));
            var area = new GraphicsPath(); area.AddRectangle(RectangleF.Inflate(l.Rect, 4, 2));
            hits.Add((area, l.Step, steps[l.Step].Shapes.FindIndex(x => x.Kind == "text" && x.Text == l.Text)));
        }
    }

    // ---- the step card ----
    // Beside the step's marks (right, left, below, above), clear of them and, if it can, of earlier steps; else the far corner.
    static (RectangleF card, bool placed) CardFrame(SizeF size, RectangleF marks, List<RectangleF> avoid, RectangleF area)
    {
        const float g = 15;
        float X(float v) => Math.Clamp(v, area.Left, Math.Max(area.Left, area.Right - size.Width));
        float Y(float v) => Math.Clamp(v, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        float mx = marks.X + marks.Width / 2, my = marks.Y + marks.Height / 2;
        var options = new List<PointF>();
        foreach (var v in new[] { my - size.Height / 2, marks.Top, marks.Bottom - size.Height }.Select(Y)) { options.Add(new(marks.Right + g, v)); options.Add(new(marks.Left - g - size.Width, v)); }
        foreach (var v in new[] { mx - size.Width / 2, marks.Left, marks.Right - size.Width }.Select(X)) { options.Add(new(v, marks.Bottom + g)); options.Add(new(v, marks.Top - g - size.Height)); }
        foreach (var keep in new[] { avoid.Append(marks).ToList(), new List<RectangleF> { marks } })
            foreach (var o in options)
            {
                var c = new RectangleF(o, size);
                if (area.Contains(c) && !keep.Any(x => x.IntersectsWith(c))) return (c, true);
            }
        return (new RectangleF(mx < area.X + area.Width / 2 ? area.Right - size.Width : area.Left, my < area.Y + area.Height / 2 ? area.Bottom - size.Height : area.Top, size.Width, size.Height), false);
    }

    static void ShowCard(string text, RectangleF marks, List<RectangleF> avoid)
    {
        var screen = ScreenAt(new PointF(marks.X + marks.Width / 2, marks.Y + marks.Height / 2)); var k = Scale(screen);
        var area = RectangleF.Inflate(screen.WorkingArea, -12 * k, -12 * k);
        int step = index + 1, total = steps.Count;
        using var body = new Font("Segoe UI", 14 * k, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI Semibold", 11 * k, GraphicsUnit.Pixel);
        using var label = new Font("Segoe UI Semibold", 13 * k, GraphicsUnit.Pixel);
        using var probe = Graphics.FromHwnd(IntPtr.Zero);
        var measured = text == "" ? SizeF.Empty : probe.MeasureString(text, body, (int)(300 * k));
        var w = Math.Max((total > 1 ? 232 : 180) * k, measured.Width + 34 * k);
        var textH = text == "" ? 0 : Math.Min(240 * k, probe.MeasureString(text, body, (int)(w - 32 * k)).Height);
        var h = 14 * k + (total > 1 ? 24 * k : 0) + (text == "" ? 0 : textH + 12 * k) + 26 * k + 14 * k;
        var size = new SizeF(Math.Min(w, area.Width), Math.Min(h, area.Height));
        var (place, _) = CardFrame(size, marks, avoid, area);
        float margin = 12 * k;
        var frame = Rectangle.Round(RectangleF.Inflate(place, margin, margin));   // the margin holds the shadow
        var c = new RectangleF(margin, margin, place.Width, place.Height);   // the card, in its own pixels
        using var bmp = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (int i = 6; i >= 1; i--) using (var sh = Rounded(RectangleF.Inflate(c, i * k * 1.3f, i * k * 1.3f), (12 + i) * k)) using (var b = new SolidBrush(Color.FromArgb(10, 0, 0, 0))) g.FillPath(b, sh);
            using (var bg = Rounded(c, 12 * k))
            {
                using (var fill = new SolidBrush(Color.FromArgb(240, 20, 20, 20))) g.FillPath(fill, bg);
                using (var edge = new Pen(Color.FromArgb(56, 255, 255, 255), Math.Max(1, k / 2))) g.DrawPath(edge, bg);
            }
            float y = c.Top + 14 * k;
            using var white = new SolidBrush(Color.White);
            if (total > 1)
            {
                using (var dim = new SolidBrush(Color.FromArgb(173, 255, 255, 255))) g.DrawString($"Step {step} of {total}", small, dim, c.Left + 16 * k, y);
                var x = new RectangleF(c.Right - 34 * k, y - 1 * k, 18 * k, 18 * k);
                using (var dim = new Pen(Color.FromArgb(153, 255, 255, 255), 1.6f * k))
                {
                    g.DrawLine(dim, x.Left + 4 * k, x.Top + 4 * k, x.Right - 4 * k, x.Bottom - 4 * k);
                    g.DrawLine(dim, x.Right - 4 * k, x.Top + 4 * k, x.Left + 4 * k, x.Bottom - 4 * k);
                }
                buttons.Add((RectangleF.Inflate(x, 4 * k, 4 * k), Clear));
                y += 24 * k;
            }
            if (text != "")
            {
                g.DrawString(text, body, white, new RectangleF(c.Left + 16 * k, y, c.Width - 32 * k, textH));
                y += textH + 12 * k;
            }
            bool last = step >= total;
            var next = new RectangleF(c.Right - 16 * k - 68 * k, y, 68 * k, 26 * k);
            Button(g, next, last ? "Done" : "Next", label, Colors["blue"], true, k);
            buttons.Add((next, Next));
            if (total > 1)
            {
                var back = new RectangleF(next.Left - 8 * k - 68 * k, y, 68 * k, 26 * k);
                Button(g, back, "Back", label, Color.FromArgb(46, 255, 255, 255), step > 1, k);
                if (step > 1) buttons.Add((back, Back));
            }
        }
        card = new Layer(frame, false);
        card.Clicked = p => { foreach (var (a, act) in buttons.ToList()) if (a.Contains(p)) { act(); return; } };
        card.Hot = p => buttons.Any(b => b.area.Contains(p));
        card.Present(bmp); card.Show();
    }

    static void Button(Graphics g, RectangleF r, string text, Font font, Color fill, bool enabled, float k)
    {
        using (var p = Rounded(r, 6 * k)) using (var b = new SolidBrush(enabled ? fill : Fade(fill, 0.4))) g.FillPath(b, p);
        using var ink = new SolidBrush(Color.FromArgb(enabled ? 255 : 110, 255, 255, 255));
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, ink, r, center);
    }
}

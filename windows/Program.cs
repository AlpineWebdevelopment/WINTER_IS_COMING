// WinterIsComing for Windows — snow on your desktop.
// Snow piles up on top of windows and on the taskbar, and flies off when you drag a window.
// Slide the mouse sideways over the snow to roll a snowball, carry one with the left button,
// and stack three of them into a snowman.
// Pure Win32 + a tiny software renderer: no WinForms, no extra packages.
//
// Run:     dotnet run -c Release
// Build:   dotnet publish -c Release -r win-x64   (or win-arm64) --self-contained -p:PublishSingleFile=true
// A ❄ icon appears in the system tray (bottom right): pause / snow amount / snowballs / clear / quit.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinterIsComing;

// MARK: - Settings (feel free to tweak)

static class Cfg
{
    public static int TargetFlakes = 260;          // number of flakes falling at the same time
    public const float ColW = 3;                   // width of one column of the snow height map
    public const float WindowPileMax = 22;         // max snow height on top of a window
    public const float MinPileDepth = 3;           // below this there is no room for a snow line at all,
                                                   // so a maximised window simply doesn't get one
    public const float GroundPileMax = 34;         // max snow height at the bottom of the screen
    public const float TaskbarPileMax = 18;        // max snow height on the taskbar
    public const float TaskbarGain = 2.0f;         // snow on the taskbar builds up this much faster
    public const float Gravity = 900;              // gravity for snow sliding off
    public const float PollInterval = 1f / 30f;    // how often window positions are checked (s)
    public const int MaxClumps = 900;              // max number of falling snow clumps at once
    public const float FallSpeed = 1.5f;           // falling speed multiplier
    public const bool FlakesInFront = true;        // some of the smallest flakes fall in front of windows
    public const float InFrontFraction = 0.7f;     // share of the smallest layer that falls in front
                                                   // (keeps the sky snowing over a maximised window)
    public const float OnTopFraction = 0.5f;       // of those, how many settle on the frontmost window
                                                   // instead of passing over everything to the ground
    public const float WindowGain = 3.5f;          // snow landing on a window top counts this much more
    public const int FrameMs = 16;                 // timer interval (~60 fps)
    public const float BallStart = 3;              // a new snowball starts this small
    public const float BallMax = 28;               // and stops growing at this size
    public const int MaxBalls = 20;                // oldest resting snowballs are removed beyond this
    public static bool Snowballs = true;           // rolling snowballs and snowmen on / off
    public const float SweepWidth = 14;            // how wide a brush the cursor is on a window's snow line
    public const float SweepBite = 1.1f;           // snow taken per logical px of cursor travel

    // Wind. The ambient gusts are always there; this is the steady breeze on top of them, from
    // -1 (hard from the right) through 0 (calm) to +1 (hard from the left).
    public static float Wind = 0;
    public const float WindMax = 160;              // logical px per second at full strength

    // Fairy lights, hung just under the top edge of every window. Off unless you ask for them.
    public static bool Lights = false;
    public const float LightSpacing = 22;          // distance between bulbs
    public const float LightSpan = 120;            // distance between the points the wire is hooked at
    public const float LightSag = 9;               // how far the wire droops between hooks
    public const float LightInset = 14;            // keep clear of the window's rounded corners
    public const float BulbGlow = 19;              // size of a bulb's soft glow
    public const float BulbCore = 5.5f;            // size of a bulb's bright centre
}

// Three depth layers: far = small, faint, slow; near = big, bright, fast
readonly record struct Layer(float SizeMin, float SizeMax, float SpeedMin, float SpeedMax,
                             float AlphaMin, float AlphaMax, float SwayMin, float SwayMax, float Wind);

static class Rnd
{
    static readonly Random R = new();
    public static float F(float a, float b) => a + (float)R.NextDouble() * (b - a);
}

// MARK: - Flake

sealed class Flake
{
    public float X, Y, Vx, Vy;
    public float Size = 4, Alpha = 1;
    public float SwayAmp, SwayFreq = 1, Phase, WindFactor = 1;
    public int Layer;                // 0 far, 1 middle, 2 near
    public bool Clump;               // sliding off a surface (affected by gravity)
    public bool InFront;             // falls in front of windows
    public bool OnTop;               // ...and settles on the frontmost window instead of the ground
    public long IgnoreId = -1;       // don't land right back on the surface it came from
    public float IgnoreUntil;
}

// MARK: - Snow pile (a height map drawn as a smooth white mound). y grows downwards.

sealed class Pile
{
    public float MinX, TopY;          // TopY = the edge the snow sits on
    public readonly float Width, MaxH;
    public readonly float[] H, Caps;  // current height / max height per column
    public readonly bool[] Occ;       // column hidden behind a window in front
    public readonly float Seed;       // so two piles never get the same lumps
    public bool Dirty;

    static int seedCounter;

    /// A few sines at unrelated frequencies: a stable, non-repeating-looking ripple in the range ±1.
    /// Column index in, so the lumps stay put while the pile lives and travel with it when it moves.
    public static float Ripple(float u, float seed)
        => 0.55f * MathF.Sin(u * 0.91f + seed)
         + 0.30f * MathF.Sin(u * 2.13f + seed * 1.7f)
         + 0.15f * MathF.Sin(u * 4.57f + seed * 2.9f);

    public Pile(float minX, float topY, float width, float maxH)
    {
        MinX = minX; TopY = topY; Width = width; MaxH = maxH;
        int n = Math.Max(1, (int)MathF.Ceiling(width / Cfg.ColW));
        H = new float[n]; Caps = new float[n]; Occ = new bool[n];
        Seed = (seedCounter++ * 0.618f) % 6.283f;
        for (int i = 0; i < n; i++)
        {
            float edgeDist = MathF.Min(i + 0.5f, n - i - 0.5f) * Cfg.ColW;
            float t = MathF.Min(1, edgeDist / (maxH * 2.2f));
            // Every column used to fill to exactly maxH, so a pile that had filled up was a dead
            // straight white bar. Giving each column its own ceiling keeps the top gently uneven
            // even when it is full. The wave is slow enough that Relax() does not flatten it out.
            float wave = 1 - 0.27f * (0.5f + 0.5f * Ripple(i * 0.22f, Seed));
            Caps[i] = maxH * MathF.Sqrt(t) * wave;   // taper at both ends so it looks rounded
        }
    }

    public int Count => H.Length;
    public float Total { get { float s = 0; foreach (var h in H) s += h; return s; } }
    public int Col(float x) { int c = (int)MathF.Floor((x - MinX) / Cfg.ColW); return c >= 0 && c < Count ? c : -1; }
    public float ColX(int c) => MinX + (c + 0.5f) * Cfg.ColW;
    public float Surface(int c) => TopY - H[c];

    static readonly (int, float)[] Spread = { (0, 1), (-1, .75f), (1, .75f), (-2, .45f), (2, .45f), (-3, .2f), (3, .2f) };

    public void Add(int c, float amount)
    {
        foreach (var (o, w) in Spread)
        {
            int i = c + o;
            if (i < 0 || i >= Count || Occ[i]) continue;
            H[i] = MathF.Min(Caps[i], H[i] + amount * w);
        }
        Dirty = true;
    }

    // Let snow slump sideways where the slope is too steep
    public void Relax()
    {
        if (!Dirty) return;
        bool moved = false;
        for (int i = 0; i < Count - 1; i++)
        {
            if (Occ[i] || Occ[i + 1]) continue;
            float d = H[i] - H[i + 1];
            if (MathF.Abs(d) > 1f)
            {
                float m = (MathF.Abs(d) - 1f) * 0.3f * MathF.Sign(d);
                // Move only what the other column can actually take. Clamping each side separately
                // destroyed snow wherever the neighbour was already at its ceiling, and since the
                // caps are uneven that happens along every slope: the surface then sank and refilled
                // frame after frame, and Relax never settled.
                m = m > 0 ? MathF.Min(m, MathF.Min(H[i], Caps[i + 1] - H[i + 1]))
                          : -MathF.Min(-m, MathF.Min(H[i + 1], Caps[i] - H[i]));
                if (MathF.Abs(m) <= 0.0001f) continue;
                H[i] -= m;
                H[i + 1] += m;
                if (MathF.Abs(m) > 0.02f) moved = true;
            }
        }
        Dirty = moved;
    }

    public void Clear() { Array.Clear(H); Dirty = true; }
}

// MARK: - Fairy lights
//
// A string of bulbs hanging just under the top edge of one window, ported from the macOS version.
// The wire droops between hooks, the bulbs twinkle out of step with each other, the whole string
// bounces for a moment after it is hung, and the wind swings it sideways.

sealed class LightString
{
    public struct Bulb { public float Phase, Speed; public int Colour; }

    // red, amber, green, blue, pink
    public static readonly float[] Colours =
    {
        1.00f, 0.10f, 0.12f,
        1.00f, 0.62f, 0.00f,
        0.05f, 0.95f, 0.20f,
        0.10f, 0.42f, 1.00f,
        1.00f, 0.15f, 0.62f,
    };

    public float L, T, Width;     // the window edge it hangs from
    public float HungAt;
    public Bulb[] Bulbs = Array.Empty<Bulb>();

    const float SwingTime = 1.4f;

    /// Follow the window. New bulbs are only made when the window's width changes, so the string
    /// doesn't re-hang itself every time you nudge the window.
    public void Place(float l, float t, float width, float now)
    {
        L = l; T = t; Width = width;
        float w = MathF.Max(0, width - 2 * Cfg.LightInset);
        int count = Math.Max(2, (int)(w / Cfg.LightSpacing));
        if (Bulbs.Length == count) return;
        Bulbs = new Bulb[count];
        for (int i = 0; i < count; i++)
            Bulbs[i] = new Bulb { Phase = Rnd.F(0, 6.28f), Speed = Rnd.F(1.2f, 3.0f), Colour = i % 5 };
        HungAt = now;
    }

    // Just after being hung the string bounces and the bulbs swing, then settle: a damped wobble.
    public float Swing(float t)
    {
        float tau = t - HungAt;
        return tau >= SwingTime ? 0 : 0.9f * MathF.Exp(-4 * tau) * MathF.Sin(13 * tau);
    }

    public float Sway(float t)
    {
        float tau = t - HungAt;
        return tau >= SwingTime ? 0 : 3.5f * MathF.Exp(-4 * tau) * MathF.Sin(9.5f * tau);
    }

    /// The bulbs light up one after another, left to right, as the string goes up
    public float Reveal(int i, float t) => Math.Clamp((t - HungAt - i * 0.015f) / 0.08f, 0, 1);

    /// Height of the wire at x: it droops between hooks like a real string of lights
    public float WireY(float x, float swing)
    {
        float x0 = L + Cfg.LightInset;
        float w = MathF.Max(1, Width - 2 * Cfg.LightInset);
        float span = w / MathF.Max(1, MathF.Round(w / Cfg.LightSpan));
        float r = (x - x0) / span;
        float f = r <= 0 ? 0 : MathF.Min(1, r - MathF.Floor(r));
        return T + 3 + Cfg.LightSag * (1 + swing) * MathF.Sin(MathF.PI * f);
    }

    public float BulbX(int i) => L + Cfg.LightInset + (i + 0.5f) * MathF.Max(0, Width - 2 * Cfg.LightInset) / Bulbs.Length;
}

// MARK: - Snowball
//
// Rolled out of the snow by sliding the mouse sideways over a pile. Three stacked balls become a snowman.

sealed class Snowball
{
    public float X, Y;              // centre, y grows downwards
    public float R = Cfg.BallStart;
    public float Vx, Vy;
    public float Spin;              // rotation of the speck layer, so you can see the ball roll
    public Pile Pile;               // the surface it rests on (null while falling, dragged or stacked)
    public Snowball Below;          // the ball it is stacked on (snowman), if any
    public bool Falling, Dragging, Hidden;
    public int Decor;               // 0 = none, 1 = snowman head, 2 = snowman body

    public const int SpeckCount = 30;
    public readonly float[] Specks; // 4 floats per speck: angle, radius, size, alpha (in unit-disc space)

    public Snowball()
    {
        Specks = new float[SpeckCount * 4];
        for (int i = 0; i < SpeckCount; i++)
        {
            Specks[i * 4 + 0] = Rnd.F(0, 6.2832f);
            Specks[i * 4 + 1] = MathF.Sqrt(Rnd.F(0, 1)) * 0.78f;   // even spread over the disc
            Specks[i * 4 + 2] = Rnd.F(0.024f, 0.062f);
            Specks[i * 4 + 3] = Rnd.F(0.25f, 0.55f);
        }
        Spin = Rnd.F(0, 6.2832f);
    }

    public void Drop(float vx, float vy)
    {
        Falling = true; Dragging = false; Hidden = false;
        Pile = null; Below = null; Decor = 0;
        Vx = vx; Vy = vy;
    }
}

// MARK: - Snowplough
//
// A little truck that drives along the bottom of the screen, scraping the snow off the taskbar
// and shoving a growing heap along in front of its blade. Snowballs in the way get rolled along too.

sealed class Snowplow
{
    public float X;             // the tip of the blade
    public int Dir = 1;         // +1 drives right, -1 drives left
    public float Heap;          // how much snow is piled up in front of the blade
    public float Wheel;         // wheel rotation

    public const float Speed = 150;       // logical px per second
    public const float Scale = 1.7f;      // how big the truck is drawn
    public const float Length = 40 * Scale;   // body length behind the blade
    public const float HeapMax = 420;
    public static int Runs;               // how many times a plough has been sent (diagnostics)
}

record struct Win(long Id, float L, float T, float R, float B)
{
    public float Width => R - L;
    public bool Contains(float x, float y) => x >= L && x < R && y >= T && y < B;
}

// MARK: - The snow world (all coordinates in logical pixels, y down)

sealed class SnowWorld
{
    static readonly Layer[] Layers =
    {
        new(1.8f, 3.0f, 22, 38,  0.30f, 0.50f, 5, 10,  0.5f),
        new(3.0f, 4.8f, 40, 65,  0.60f, 0.80f, 9, 16,  0.8f),
        new(4.8f, 7.5f, 70, 105, 0.85f, 1.00f, 14, 24, 1.1f),
    };

    public float W, H;
    public readonly List<Flake> Flakes = new();
    readonly Stack<Flake> pool = new();
    public readonly Dictionary<long, Pile> Piles = new();
    readonly Dictionary<long, Win> lastRect = new();
    public List<Win> Order = new();              // windows from front to back
    public Pile Ground;
    public bool GroundIsTaskbar;
    public int TaskbarZ;             // windows before this index in Order are drawn in front of the taskbar
    public int PileChurn, MoveShakes;   // piles rebuilt, and piles shaken off because a window moved
    public readonly Dictionary<long, LightString> Lights = new();
    float pollAcc, t;
    public float Time => t;
    const long GroundId = -2;

    // Snowplough
    public Snowplow Plow;
    public bool AutoPlow = true;          // you asked for one that comes by itself, so it does
    public float AutoPlowEvery = 180;     // seconds between automatic runs
    float plowAcc = 135;                  // but the first turns up sooner, so you get to see one

    // Snowballs
    public readonly List<Snowball> Balls = new();
    Snowball activeBall, dragBall;
    float dragOffX, dragOffY, dragVx, dragVy;
    float lastMx, lastMy;
    bool haveMouse;

    public SnowWorld(float w, float h)
    {
        W = w; H = h;
        ResetGround(h, false);
        for (int i = 0; i < Cfg.TargetFlakes; i++) SpawnFlake(Rnd.F(0, H));
    }

    public void Resize(float w, float h)
    {
        W = w; H = h;
        ResetGround(h, false);
    }

    void ResetGround(float top, bool taskbar)
    {
        if (Ground != null) Release(Ground, 0, 0, GroundId);
        GroundIsTaskbar = taskbar;
        Ground = new Pile(0, top, W, taskbar ? Cfg.TaskbarPileMax : Cfg.GroundPileMax);
    }

    public void ClearAll()
    {
        foreach (var p in Piles.Values) p.Clear();
        Ground.Clear();
        Balls.Clear();
        activeBall = null;
        dragBall = null;
    }

    // MARK: Flake pool

    Flake Obtain()
    {
        var f = pool.Count > 0 ? pool.Pop() : new Flake();
        Flakes.Add(f);
        return f;
    }

    void Recycle(int i)
    {
        pool.Push(Flakes[i]);
        Flakes[i] = Flakes[^1];
        Flakes.RemoveAt(Flakes.Count - 1);
    }

    void SpawnFlake(float? y = null)
    {
        var f = Obtain();
        float r = Rnd.F(0, 1);
        int li = r < 0.40f ? 0 : (r < 0.68f ? 1 : 2);   // 40% far, 28% middle, 32% near
        var L = Layers[li];
        f.Layer = li;
        f.Clump = false;
        f.InFront = Cfg.FlakesInFront && li == 0 && Rnd.F(0, 1) < Cfg.InFrontFraction;
        f.OnTop = f.InFront && Rnd.F(0, 1) < Cfg.OnTopFraction;
        f.IgnoreId = -1;
        f.Size = Rnd.F(L.SizeMin, L.SizeMax);
        f.Vx = 0;
        f.Vy = Rnd.F(L.SpeedMin, L.SpeedMax) * Cfg.FallSpeed;
        f.SwayAmp = Rnd.F(L.SwayMin, L.SwayMax);
        f.SwayFreq = Rnd.F(0.6f, 1.6f);
        f.Phase = Rnd.F(0, 6.28f);
        f.WindFactor = L.Wind;
        f.Alpha = Rnd.F(L.AlphaMin, L.AlphaMax);
        f.X = Rnd.F(-40, W + 40);
        // Start clear of the top edge by more than the flake is wide. A flake is drawn with radius
        // Size * 1.1, so spawning 2 px above the edge made the big near-layer ones pop into
        // existence half on screen -- a constant flicker along the top, six times a frame.
        f.Y = y ?? -(f.Size * 1.4f + Rnd.F(8, 70));
    }

    void SpawnClump(float x, float y, float vx, float vy, long sourceId)
    {
        var f = Obtain();
        f.Clump = true;
        // Snow coming off a surface slides down the FRONT of whatever is below it. Without this a
        // clump is swallowed the moment it enters a window's rectangle, so dragging a window looked
        // like its snow blinking out of existence instead of tumbling down the window.
        f.InFront = true;
        f.OnTop = false;
        f.Layer = 2;
        f.Size = Rnd.F(2.5f, 5);
        f.Alpha = 0.95f;
        f.Vx = vx; f.Vy = vy;
        f.IgnoreId = sourceId;
        f.IgnoreUntil = t + 0.35f;
        f.X = x; f.Y = y;
    }

    // Turn a pile into falling clumps; (vx, vy) is the window's velocity, so faster drags fling snow farther
    void Release(Pile p, float vx, float vy, long id)
    {
        // Snowballs resting on this surface are flung off too
        foreach (var b in Balls)
            if (b.Pile == p && !b.Falling && !b.Dragging)
                b.Drop(Math.Clamp(vx * 0.5f, -700, 700) + Rnd.F(-40, 40),
                       Math.Clamp(vy * 0.4f, -400, 300) - Rnd.F(40, 140));

        float total = p.Total;
        if (total > 0.5f)
        {
            float budget = Math.Max(0, Cfg.MaxClumps - Flakes.Count + Cfg.TargetFlakes);
            float k = MathF.Min(1, budget / MathF.Max(1, total / 3.5f));
            float fx = Math.Clamp(vx * 0.5f, -900, 900);
            float fy = Math.Clamp(vy * 0.4f, -500, 300);
            for (int c = 0; c < p.Count; c++)
            {
                if (p.H[c] <= 0.6f) continue;
                float amount = p.H[c] / 3.5f * k;
                while (amount > 0)
                {
                    if (amount < 1 && Rnd.F(0, 1) > amount) break;
                    SpawnClump(p.ColX(c) + Rnd.F(-1.5f, 1.5f), p.TopY - Rnd.F(0, p.H[c]),
                               fx + Rnd.F(-40, 40), fy - Rnd.F(10, 80), id);
                    amount -= 1;
                }
            }
        }
        p.Clear();
    }

    // MARK: Per-frame update

    public void Update(float dt, Func<List<Win>> readWindows, Func<float?> readTaskbarTop, Func<int> readTaskbarZ,
                       float mx, float my)
    {
        t += dt;
        pollAcc += dt;
        if (pollAcc >= Cfg.PollInterval)
        {
            // argument order matters: readWindows() is what works out where the taskbar sits in the z-order
            PollWindows(pollAcc, readWindows(), readTaskbarTop(), readTaskbarZ());
            pollAcc = 0;
        }
        StepFlakes(dt);

        if (Cfg.Snowballs)
        {
            if (dragBall != null) StepDrag(dragBall, dt, mx, my);
            else UpdateRolling(mx, my);
            StepBalls(dt);
        }
        else if (Balls.Count > 0)
        {
            Balls.Clear();
            activeBall = null;
            dragBall = null;
        }

        StepPlow(dt);
    }

    // MARK: Snowplough

    public void SendPlow()
    {
        if (Plow != null) return;
        int dir = Rnd.F(0, 1) < 0.5f ? 1 : -1;
        Plow = new Snowplow { Dir = dir, X = dir > 0 ? -Snowplow.Length - 10 : W + Snowplow.Length + 10 };
        Snowplow.Runs++;
        plowAcc = 0;
    }

    void StepPlow(float dt)
    {
        if (Plow == null)
        {
            if (!AutoPlow) { plowAcc = 0; return; }
            plowAcc += dt;
            if (plowAcc >= AutoPlowEvery && Ground.Total > 80) SendPlow();
            return;
        }

        var p = Plow;
        float step = Snowplow.Speed * dt * p.Dir;
        p.X += step;
        p.Wheel += step / 5f;

        // Scrape the ground clean from the blade backwards
        float from = p.Dir > 0 ? p.X - Snowplow.Length : p.X;
        float to = p.Dir > 0 ? p.X : p.X + Snowplow.Length;
        int c0 = Ground.Col(MathF.Max(from, Ground.MinX));
        int c1 = Ground.Col(MathF.Min(to, Ground.MinX + Ground.Width - 0.1f));
        if (c0 >= 0 && c1 >= 0)
        {
            for (int c = c0; c <= c1; c++)
            {
                if (Ground.Occ[c] || Ground.H[c] <= 0) continue;
                p.Heap = MathF.Min(Snowplow.HeapMax, p.Heap + Ground.H[c]);
                Ground.H[c] = 0;
            }
            Ground.Dirty = true;
        }

        // Shove snowballs along in front of the blade
        foreach (var b in Balls)
        {
            if (b.Falling || b.Dragging || b.Pile != Ground || b.Below != null) continue;
            float gap = (b.X - p.X) * p.Dir;
            if (gap > b.R + 4 || gap < -(Snowplow.Length + b.R)) continue;
            float need = p.X + p.Dir * (b.R + 4);
            float lo = Ground.MinX + b.R, hi = Ground.MinX + Ground.Width - b.R;
            if (need >= lo && need <= hi) Roll(b, Ground, need);
        }

        // Off the far edge: the heap goes with it
        if ((p.Dir > 0 && p.X > W + Snowplow.Length + 20) || (p.Dir < 0 && p.X < -Snowplow.Length - 20))
            Plow = null;
    }

    void PollWindows(float dt, List<Win> wins, float? taskbarTop, int taskbarZ)
    {
        TaskbarZ = taskbarZ;
        // Taskbar at the bottom: the snow on the "ground" sits on top of it
        float groundTop = taskbarTop ?? H;
        bool taskbar = taskbarTop.HasValue;
        if (MathF.Abs(groundTop - Ground.TopY) > 1 || taskbar != GroundIsTaskbar) ResetGround(groundTop, taskbar);

        // Closed / minimized windows: their snow just falls
        var alive = new HashSet<long>();
        foreach (var w in wins) alive.Add(w.Id);
        var gone = new List<long>();
        foreach (var kv in Piles) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            Release(Piles[id], 0, 0, id);
            Piles.Remove(id);
            lastRect.Remove(id);
        }

        foreach (var r in wins)
        {
            // The snow line sits above the window's top edge, so it can only be as deep as the room
            // left above it. A maximised window has none and gets no snow line at all — better than a
            // white band glued to the top of the screen, and the windows in front of it still get theirs.
            float depth = MathF.Min(Cfg.WindowPileMax, r.T - 1);
            Piles.TryGetValue(r.Id, out var pile);
            bool known = lastRect.TryGetValue(r.Id, out var old);

            if (depth < Cfg.MinPileDepth)
            {
                if (pile != null) { Release(pile, 0, 0, r.Id); Piles.Remove(r.Id); }
            }
            else if (pile == null || !known)
            {
                PileChurn++;
                if (pile != null) Release(pile, 0, 0, r.Id);
                Piles[r.Id] = new Pile(r.L, r.T, r.Width, depth);
            }
            else if (MathF.Abs(old.Width - r.Width) > 0.5f || MathF.Abs(pile.MaxH - depth) > 0.5f)
            {
                // Width changed, or the window moved near enough to the top edge to change how much
                // snow fits: drop what's there and start a new pile
                PileChurn++;
                Release(pile, 0, 0, r.Id);
                Piles[r.Id] = new Pile(r.L, r.T, r.Width, depth);
            }
            else if (MathF.Abs(old.L - r.L) > 0.3f || MathF.Abs(old.T - r.T) > 0.3f)
            {
                // Window dragged: fling the snow off
                MoveShakes++;
                float vx = (r.L - old.L) / dt, vy = (r.T - old.T) / dt;
                pile.MinX = r.L;
                pile.TopY = r.T;
                Release(pile, vx, vy, r.Id);
            }
            lastRect[r.Id] = r;
        }
        Order = wins;

        // Fairy lights follow the windows they hang on
        if (!Cfg.Lights) Lights.Clear();
        else
        {
            foreach (var r in wins)
            {
                if (!Lights.TryGetValue(r.Id, out var ls)) Lights[r.Id] = ls = new LightString { HungAt = t };
                ls.Place(r.L, r.T, r.Width, t);
            }
            if (Lights.Count > wins.Count)
            {
                var dead = new List<long>();
                foreach (var kv in Lights) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (var id in dead) Lights.Remove(id);
            }
        }

        ComputeOcclusion();
        foreach (var p in Piles.Values) p.Relax();
        Ground.Relax();
    }

    // Parts of a window top hidden behind a front window get no snow and aren't drawn
    void ComputeOcclusion()
    {
        for (int i = 0; i < Order.Count; i++)
        {
            if (!Piles.TryGetValue(Order[i].Id, out var p)) continue;
            for (int c = 0; c < p.Count; c++)
            {
                float x = p.ColX(c), y = p.TopY - 1;
                bool o = x < 0 || x > W || y < 0;
                if (!o) for (int j = 0; j < i; j++) if (Order[j].Contains(x, y)) { o = true; break; }
                if (o != p.Occ[c]) { p.Occ[c] = o; if (o) p.H[c] = 0; p.Dirty = true; }
            }
        }
        // Snow lying on the plain bottom of the screen is hidden by any window over it. Snow lying on
        // the taskbar is hidden only by a window that is drawn in front of the taskbar AND actually
        // hangs down over it — a maximised window stops exactly at the taskbar's top edge, and must
        // not blank the whole line just because the snow sticks up into its last few rows.
        float gy = Ground.TopY - 1;
        int limit = GroundIsTaskbar ? Math.Min(TaskbarZ, Order.Count) : Order.Count;
        for (int c = 0; c < Ground.Count; c++)
        {
            bool o = false;
            float x = Ground.ColX(c);
            for (int j = 0; j < limit; j++)
            {
                var w = Order[j];
                if (GroundIsTaskbar && w.B <= Ground.TopY + 1) continue;
                if (w.Contains(x, gy)) { o = true; break; }
            }
            if (o != Ground.Occ[c]) { Ground.Occ[c] = o; if (o) Ground.H[c] = 0; Ground.Dirty = true; }
        }
    }

    void StepFlakes(float dt)
    {
        // A few sines at different frequencies add up to gusty wind. The steady breeze you pick in
        // the menu rides on top, and makes the gusts rougher as it gets stronger.
        float gust = 14 * MathF.Sin(t * 0.11f) + 9 * MathF.Sin(t * 0.37f + 1.3f) + 6 * MathF.Sin(t * 0.9f + 0.4f);
        float wind = gust * (1 + MathF.Abs(Cfg.Wind)) + Cfg.Wind * Cfg.WindMax;
        int normal = 0;
        for (int i = Flakes.Count - 1; i >= 0; i--)
        {
            var f = Flakes[i];
            float prevY = f.Y;
            if (f.Clump)
            {
                f.Vy += Cfg.Gravity * dt;
                f.Vx += wind * 0.4f * dt;          // the wind pushes flying clumps about too
                f.Vx *= MathF.Max(0, 1 - 1.2f * dt);
                f.X += f.Vx * dt;
                f.Y += f.Vy * dt;
            }
            else
            {
                float sway = MathF.Sin(t * f.SwayFreq + f.Phase) * f.SwayAmp;
                f.X += (wind * f.WindFactor + sway) * dt;
                f.Y += f.Vy * dt;
                if (f.X < -40) f.X += W + 80; else if (f.X > W + 40) f.X -= W + 80;
            }

            bool gone = false;
            if (f.Y > prevY)
            {
                // Window tops. A flake falling behind the windows settles on the first surface it
                // meets, front to back. A flake falling in front of them either settles on the
                // frontmost window covering it, or passes over every window down to the ground.
                //
                // That first kind is what puts snow on a window that sits over a maximised one: the
                // maximised window's own snow line catches every flake behind it, so without this a
                // window in front of it would never collect anything, however far down its edge is.
                if (!f.InFront || f.OnTop)
                {
                    foreach (var w in Order)
                    {
                        if (!Piles.TryGetValue(w.Id, out var pile)) continue;
                        if (f.Clump && w.Id == f.IgnoreId && t < f.IgnoreUntil) continue;
                        int c = pile.Col(f.X);
                        if (c < 0 || pile.Occ[c]) continue;
                        float s = pile.Surface(c);
                        if (prevY <= s && f.Y > s)
                        {
                            pile.Add(c, (f.Clump ? 1.6f : f.Size * 0.16f) * (f.OnTop ? Cfg.WindowGain : 1));
                            gone = true;
                            break;
                        }
                        if (f.OnTop) break;   // never settles on a window behind the frontmost one
                    }
                }
                // Then the taskbar / bottom of the screen
                if (!gone)
                {
                    int c = Ground.Col(f.X);
                    if (c >= 0 && f.Y > Ground.Surface(c))
                    {
                        if (!Ground.Occ[c])
                        {
                            float gain = GroundIsTaskbar ? Cfg.TaskbarGain : 1;
                            Ground.Add(c, (f.Clump ? 1.6f : f.Size * 0.12f) * gain);
                        }
                        gone = true;
                    }
                }
            }
            if (!gone)
            {
                // Flakes that fall "behind" a window disappear
                if (!f.InFront) foreach (var w in Order) if (w.Contains(f.X, f.Y)) { gone = true; break; }
                if (f.Y > H + 20) gone = true;
                if (f.Clump && (f.X < -80 || f.X > W + 80)) gone = true;
            }

            if (gone) Recycle(i);
            else if (!f.Clump) normal++;
        }

        // Top up flakes (max 6 per frame so they don't appear in a row)
        int deficit = Math.Min(6, Cfg.TargetFlakes - normal);
        for (int k = 0; k < deficit; k++) SpawnFlake();
    }

    // MARK: Snowballs — rolling

    /// Is the mouse inside the snow band of this pile (a bit below the edge up to a bit above the snow)?
    bool MouseOver(Pile p, float mx, float my)
    {
        int c = p.Col(mx);
        if (c < 0 || p.Occ[c]) return false;
        return my <= p.TopY + 6 && my >= p.TopY - p.H[c] - 14;
    }

    /// Which snow surface is the mouse hovering over? Taskbar first, then windows front to back, then the ground.
    Pile SurfaceUnderMouse(float mx, float my)
    {
        // Hovering over a snowball counts as hovering over the snow it sits on. Without this, a big ball
        // stops rolling as soon as the cursor rises above the thin snow band, which makes pushing jerky.
        foreach (var b in Balls)
        {
            if (b.Falling || b.Dragging || b.Hidden || b.Below != null || b.Pile == null) continue;
            if (MathF.Abs(mx - b.X) >= b.R + 8) continue;
            if (my <= b.Pile.TopY + 6 && my >= b.Pile.TopY - 2 * b.R - 8) return b.Pile;
        }
        // Snowballs belong on the ground. A window's top edge is a few pixels of snow you walk
        // the cursor over all day; balls rolled up there sat on the edge until you cleared them
        // by hand. Dragging the window still sweeps its snow off.
        if (GroundIsTaskbar && MouseOver(Ground, mx, my)) return Ground;
        foreach (var w in Order)
            if (w.Contains(mx, my)) return null;            // over a window's content
        if (!GroundIsTaskbar && MouseOver(Ground, mx, my)) return Ground;
        return null;
    }

    /// The window snow line under the cursor, if any, with the window it belongs to
    (Pile Pile, long Id) WindowLineUnderMouse(float mx, float my)
    {
        foreach (var w in Order)
        {
            if (Piles.TryGetValue(w.Id, out var p) && MouseOver(p, mx, my)) return (p, w.Id);
            if (w.Contains(mx, my)) return (null, 0);       // over a window's content
        }
        return (null, 0);
    }

    /// Brush snow off a window's top edge. No ball is made — that is the whole point of keeping
    /// them on the ground — the snow just comes loose and tumbles down the front of the window.
    void Sweep(Pile p, long id, float mx, float dx)
    {
        int c0 = Math.Max(0, p.Col(mx - Cfg.SweepWidth));
        int cm = p.Col(mx);
        int c1 = p.Col(mx + Cfg.SweepWidth);
        if (c1 < 0) c1 = p.Count - 1;
        if (cm < 0) return;
        float push = Math.Clamp(dx * 22, -420, 420);
        float bite = MathF.Max(1.2f, MathF.Abs(dx) * Cfg.SweepBite);
        int budget = Math.Max(0, Cfg.MaxClumps - Flakes.Count + Cfg.TargetFlakes);
        for (int c = c0; c <= c1 && budget > 0; c++)
        {
            if (p.Occ[c] || p.H[c] <= 0.3f) continue;
            // the middle of the brush takes the most, the edges feather out
            float t = 1 - MathF.Abs(c - cm) / (Cfg.SweepWidth / Cfg.ColW + 1);
            float take = MathF.Min(p.H[c], bite * MathF.Max(0.25f, t));
            float top = p.TopY - p.H[c];
            p.H[c] -= take;
            p.Dirty = true;
            float amount = take / 2.2f;
            while (amount > 0 && budget > 0)
            {
                if (amount < 1 && Rnd.F(0, 1) > amount) break;
                SpawnClump(p.ColX(c) + Rnd.F(-1.5f, 1.5f), top + Rnd.F(0, take),
                           push + Rnd.F(-50, 50), -Rnd.F(20, 90), id);
                budget--;
                amount -= 1;
            }
        }
    }

    void UpdateRolling(float mx, float my)
    {
        if (!haveMouse) { haveMouse = true; lastMx = mx; lastMy = my; }
        float dx = mx - lastMx;
        lastMx = mx; lastMy = my;

        var p = SurfaceUnderMouse(mx, my);
        if (p == null)
        {
            activeBall = null;
            if (MathF.Abs(dx) > 0.2f)
            {
                var (wp, id) = WindowLineUnderMouse(mx, my);
                if (wp != null) Sweep(wp, id, mx, dx);
            }
            return;
        }
        if (MathF.Abs(dx) <= 0.2f) return;

        // Keep rolling the current ball, pick up a resting one under the mouse, or start a new one
        if (activeBall != null && activeBall.Pile == p && !activeBall.Falling && !activeBall.Dragging
            && Above(activeBall) == null && MathF.Abs(activeBall.X - (mx - dx)) < activeBall.R + 25)
        {
            // keep it
        }
        else
        {
            Snowball under = null;
            foreach (var b in Balls)
                if (!b.Falling && !b.Dragging && b.Pile == p && MathF.Abs(b.X - mx) < b.R + 10) { under = b; break; }

            if (under != null)
            {
                if (Above(under) != null) { activeBall = null; return; }   // don't roll the bottom of a snowman
                activeBall = under;
            }
            else
            {
                int c = p.Col(mx);
                if (c < 0 || p.Occ[c] || p.H[c] <= 1.5f) { activeBall = null; return; }
                var nb = new Snowball { X = mx, Pile = p };
                nb.Y = p.TopY - nb.R + 1.5f;
                Balls.Add(nb);
                activeBall = nb;
                TrimBalls();
            }
        }

        var ball = activeBall;
        if (ball == null) return;

        // Move the ball to the mouse, staying on the surface
        float lo = p.MinX + ball.R, hi = p.MinX + p.Width - ball.R;
        float newX = lo < hi ? Math.Clamp(mx, lo, hi) : ball.X;
        if (MathF.Abs(newX - ball.X) <= 0.01f) return;
        float oldX = ball.X;
        float dir = newX > ball.X ? 1 : -1;
        Roll(ball, p, newX);
        Push(ball, oldX, dir, p);
    }

    /// Roll a ball to newX: it picks up snow from the columns it passes over and grows
    void Roll(Snowball b, Pile p, float newX)
    {
        float moved = newX - b.X;
        if (MathF.Abs(moved) <= 0.01f) return;
        if (b.R < Cfg.BallMax)
        {
            float collected = 0;
            float from = MathF.Min(b.X, newX) - b.R * 0.8f;
            float to = MathF.Max(b.X, newX) + b.R * 0.8f;
            float frac = MathF.Min(0.6f, 0.6f * MathF.Abs(moved) / (1.6f * b.R));
            int c0 = p.Col(MathF.Max(from, p.MinX)), c1 = p.Col(MathF.Min(to, p.MinX + p.Width - 0.1f));
            if (c0 >= 0 && c1 >= 0 && c0 <= c1)
            {
                for (int c = c0; c <= c1; c++)
                {
                    if (p.Occ[c]) continue;
                    float take = p.H[c] * frac;
                    p.H[c] -= take;
                    collected += take;
                }
                p.Dirty = true;
            }
            // Ball area grows by the snow area it picked up
            float area = MathF.PI * b.R * b.R + collected * Cfg.ColW * 0.8f;
            b.R = MathF.Min(Cfg.BallMax, MathF.Sqrt(area / MathF.PI));
        }
        b.X = newX;
        b.Spin += moved / b.R;
    }

    /// The moving ball pushes any ball it touches in front of it; that ball can push the next one, and so on.
    /// A ball pushed past the end of a surface falls off.
    void Push(Snowball b, float oldX, float dir, Pile p)
    {
        // Look for balls ahead of where the pusher *was*: after a fast mouse move it may already have
        // jumped past a ball, and that ball must still be pushed instead of being passed through.
        var ahead = new List<Snowball>();
        foreach (var o in Balls)
            if (o != b && !o.Falling && !o.Dragging && o.Pile == p && (o.X - oldX) * dir > 0) ahead.Add(o);
        if (ahead.Count == 0) return;
        ahead.Sort((u, v) => MathF.Abs(u.X - oldX).CompareTo(MathF.Abs(v.X - oldX)));

        var pusher = b;
        foreach (var o in ahead)
        {
            float need = pusher.X + dir * (pusher.R + o.R);     // where o must be to just touch the pusher
            if ((need - o.X) * dir <= 0) break;                 // not touching: the chain stops here
            float lo = p.MinX + o.R, hi = p.MinX + p.Width - o.R;
            if (need < lo || need > hi)
            {
                o.X = need;
                o.Drop(dir * 140, -40);                         // pushed off the edge
            }
            else Roll(o, p, need);
            pusher = o;
        }
    }

    void TrimBalls()
    {
        while (Balls.Count > Cfg.MaxBalls)
        {
            int idx = -1;
            for (int i = 0; i < Balls.Count; i++)
            {
                var b = Balls[i];
                if (b.Falling || b.Dragging || b.Decor != 0 || b == activeBall) continue;
                if (b.Below != null || Above(b) != null) continue;   // never break up a stack
                idx = i;
                break;
            }
            if (idx < 0) break;
            Balls.RemoveAt(idx);
        }
    }

    // MARK: Snowballs — stacking, dragging, physics

    /// The visible, resting snowball under a point (top of a stack first)
    public Snowball BallAt(float mx, float my)
    {
        Snowball best = null;
        foreach (var b in Balls)
        {
            if (b.Falling || b.Hidden) continue;
            float dx = mx - b.X, dy = my - b.Y;
            if (dx * dx + dy * dy <= (b.R + 2) * (b.R + 2) && (best == null || b.Y < best.Y)) best = b;
        }
        return best;
    }

    /// The overlay normally lets every click through. Only while the mouse is over a snowball (or dragging one)
    /// does it catch clicks, so you can grab the ball without clicking the window underneath.
    public bool WantsMouse(float mx, float my) => Cfg.Snowballs && (dragBall != null || BallAt(mx, my) != null);

    Snowball Above(Snowball b)
    {
        foreach (var x in Balls) if (x.Below == b) return x;
        return null;
    }

    Snowball BaseOf(Snowball b)
    {
        int guard = 0;
        while (b.Below != null && guard++ < 10) b = b.Below;
        return b;
    }

    /// Balls in a stack from the bottom up
    List<Snowball> StackFrom(Snowball baseBall)
    {
        var o = new List<Snowball> { baseBall };
        Snowball a;
        while (o.Count < 10 && (a = Above(o[o.Count - 1])) != null) o.Add(a);
        return o;
    }

    public void MouseDown(float mx, float my)
    {
        if (!Cfg.Snowballs) return;
        var b = BallAt(mx, my);
        if (b == null) return;

        // Anything stacked on top of the grabbed ball tumbles off
        for (var a = Above(b); a != null;)
        {
            var next = Above(a);
            a.Drop(Rnd.F(-50, 50), -Rnd.F(40, 100));
            a = next;
        }
        ClearDecor(b);
        b.Pile = null; b.Below = null; b.Falling = false; b.Dragging = true;
        dragBall = b;
        activeBall = null;
        dragOffX = b.X - mx; dragOffY = b.Y - my;
        dragVx = dragVy = 0;
    }

    public void MouseUp()
    {
        var b = dragBall;
        if (b == null) return;
        dragBall = null;
        b.Dragging = false;

        var target = StackTarget(b);
        if (target != null)
        {
            b.Below = target;
            b.Vx = b.Vy = 0;
            var st = StackFrom(BaseOf(b));
            if (st.Count == 3) MakeSnowman(st);
        }
        else
        {
            // Let go: it falls (with a bit of the throw) and lands on whatever is below
            b.Drop(Math.Clamp(dragVx * 0.5f, -600, 600), Math.Clamp(dragVy * 0.5f, -400, 400));
        }
    }

    void StepDrag(Snowball b, float dt, float mx, float my)
    {
        float tx = mx + dragOffX, ty = my + dragOffY;
        if (dt > 0)
        {
            float vx = (tx - b.X) / dt, vy = (ty - b.Y) / dt;
            dragVx = dragVx * 0.7f + vx * 0.3f;
            dragVy = dragVy * 0.7f + vy * 0.3f;
        }
        b.Spin += (tx - b.X) / b.R;
        b.X = tx; b.Y = ty;
        lastMx = mx; lastMy = my;
    }

    /// A ball dropped just above another ball (that isn't already a full snowman) gets stacked on it
    Snowball StackTarget(Snowball b)
    {
        Snowball best = null;
        float bestDist = float.MaxValue;
        foreach (var c in Balls)
        {
            if (c == b || c.Falling || c.Dragging || c.Hidden) continue;
            if (Above(c) != null || StackFrom(BaseOf(c)).Count >= 3) continue;
            float dx = MathF.Abs(b.X - c.X), dy = c.Y - b.Y;      // b must sit above c
            if (dx >= c.R * 0.9f + b.R * 0.3f || dy <= 0 || dy >= c.R + b.R + 30) continue;
            if (dx < bestDist) { best = c; bestDist = dx; }
        }
        return best;
    }

    void ClearDecor(Snowball b)
    {
        foreach (var x in StackFrom(BaseOf(b))) x.Decor = 0;
    }

    /// Three stacked balls become a snowman: face and scarf on the head, buttons and stick arms on the body
    void MakeSnowman(List<Snowball> st)
    {
        st[0].Decor = 0;
        st[1].Decor = 2;   // body
        st[2].Decor = 1;   // head
    }

    void StepBalls(float dt)
    {
        for (int i = Balls.Count - 1; i >= 0; i--)
        {
            var b = Balls[i];
            bool gone = false;

            if (b.Falling)
            {
                b.Decor = 0;
                b.Hidden = false;
                b.Vy += Cfg.Gravity * dt;
                b.X += b.Vx * dt;
                b.Y += b.Vy * dt;
                b.Spin += b.Vx * dt / b.R;
                if (b.Vy > 0)
                {
                    var p = LandingSurface(b.X, b.Y + b.R);
                    if (p != null) { Splat(b, p); gone = true; }   // falling snowballs burst into a heap of snow
                }
                if (!gone && (b.Y > H + b.R * 2 || b.X < -100 || b.X > W + 100)) gone = true;
            }
            else if (b.Dragging)
            {
                b.Hidden = false;   // position is set by StepDrag
            }
            else if (b.Below != null)
            {
                var u = b.Below;
                if (u.Falling || u.Dragging)
                    b.Drop(u.Vx + Rnd.F(-40, 40), MathF.Min(u.Vy, 0) - Rnd.F(30, 80));
                else
                {
                    b.X = u.X;
                    b.Y = u.Y - u.R - b.R * 0.82f;   // sits slightly sunk into the ball below
                    b.Hidden = u.Hidden;
                }
            }
            else if (b.Pile != null)
            {
                int c = b.Pile.Col(b.X);
                b.Hidden = c < 0 || b.Pile.Occ[c];   // hidden behind a window in front
                b.Y = b.Pile.TopY - b.R + 1.5f;      // sits slightly sunk into the snow
            }
            else b.Drop(0, 0);                       // lost its support

            if (gone)
            {
                if (activeBall == b) activeBall = null;
                Balls.RemoveAt(i);
            }
        }
    }

    /// Where a falling ball lands. Only the ground catches one — a ball dropped over a window
    /// falls straight past its snow line, for the same reason you cannot roll one up there.
    /// The ground catches everything that reaches the bottom, even where a window covers it.
    Pile LandingSurface(float x, float newBottom)
    {
        int gc = Ground.Col(x);
        return gc >= 0 && newBottom > Ground.Surface(gc) ? Ground : null;
    }

    /// A falling ball bursts where it lands: its snow becomes a mound on that surface, plus a puff of powder
    void Splat(Snowball b, Pile p)
    {
        int c0 = p.Col(b.X);
        if (c0 >= 0)
        {
            float snow = MathF.PI * b.R * b.R * 0.7f / Cfg.ColW;   // ball area -> total column height
            int half = Math.Max(2, (int)(b.R * 1.4f / Cfg.ColW));
            float sum = 0;
            for (int o = -half; o <= half; o++) sum += 1 - MathF.Abs((float)o) / (half + 1f);
            for (int o = -half; o <= half; o++)
            {
                int i = c0 + o;
                if (i < 0 || i >= p.Count || p.Occ[i]) continue;
                float w = (1 - MathF.Abs((float)o) / (half + 1f)) / sum;
                p.H[i] = MathF.Min(p.Caps[i], p.H[i] + snow * w);
            }
            p.Dirty = true;
        }
        int n = Math.Min(14, (int)(b.R * 0.6f) + 2);
        for (int k = 0; k < n; k++)
            SpawnClump(b.X + Rnd.F(-b.R, b.R), b.Y + b.R * 0.5f - Rnd.F(0, b.R * 0.5f),
                       b.Vx * 0.2f + Rnd.F(-90, 90), -Rnd.F(60, 180), -3);
    }
}

// MARK: - Software renderer into a premultiplied BGRA buffer

unsafe static class Render
{
    static void Blend(uint* p, float a, float r, float g, float b)
    {
        if (a <= 0.003f) return;
        if (a > 1) a = 1;
        uint d = *p;
        float inv = 1 - a;
        uint nb = (uint)MathF.Min(255, b * a * 255 + (d & 0xFF) * inv + 0.5f);
        uint ng = (uint)MathF.Min(255, g * a * 255 + ((d >> 8) & 0xFF) * inv + 0.5f);
        uint nr = (uint)MathF.Min(255, r * a * 255 + ((d >> 16) & 0xFF) * inv + 0.5f);
        uint na = (uint)MathF.Min(255, a * 255 + (d >> 24) * inv + 0.5f);
        *p = (na << 24) | (nr << 16) | (ng << 8) | nb;
    }

    // Soft round flake: solid center, fading edge
    public static void Disc(uint* buf, int bw, int bh, float cx, float cy, float radius, float alpha)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(cx - radius)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - radius)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + radius));
        float inv = 1 / radius;
        for (int y = y0; y <= y1; y++)
        {
            float dy = (y + 0.5f - cy) * inv;
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * inv;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= 1) continue;
                float a = d < 0.45f ? 1 - 0.4f * (d / 0.45f) : 0.6f * (1 - (d - 0.45f) / 0.55f);
                Blend(row + x, a * alpha, 1, 1, 1);
            }
        }
    }

    // A pile: a lumpy mound above its edge, lit from the top-left and shaded towards the surface it
    // rests on, with a thin blue-grey shadow just below it.
    public static void Pile(uint* buf, int bw, int bh, Pile p, float s)
    {
        int px0 = Math.Max(0, (int)MathF.Floor(p.MinX * s)), px1 = Math.Min(bw - 1, (int)MathF.Ceiling((p.MinX + p.Width) * s));
        float baseY = p.TopY * s;
        float shEnd = baseY + 1.5f * s;
        int yBase = Math.Min(bh, (int)MathF.Ceiling(baseY));

        // Height of the drawn surface at a pixel column: the settled heights interpolated between
        // column centres, plus fine lumps that are too small for the physics to carry but are what
        // stops a filled-up pile looking like a ruled white bar.
        float Surface(float px)
        {
            float u = ((px + 0.5f) / s - p.MinX) / Cfg.ColW - 0.5f;
            int c0 = (int)MathF.Floor(u);
            float fr = u - c0;
            int ca = Math.Clamp(c0, 0, p.Count - 1), cb = Math.Clamp(c0 + 1, 0, p.Count - 1);
            float h = (p.H[ca] * (1 - fr) + p.H[cb] * fr) * s;
            if (h <= 0.05f) return 0;
            return MathF.Max(0, h + WinterIsComing.Pile.Ripple(u * 0.55f, p.Seed + 7) * MathF.Min(h * 0.20f, 2.0f * s));
        }

        for (int px = px0; px <= px1; px++)
        {
            float u = ((px + 0.5f) / s - p.MinX) / Cfg.ColW - 0.5f;
            int c0 = (int)MathF.Floor(u);
            int ca = Math.Clamp(c0, 0, p.Count - 1), cb = Math.Clamp(c0 + 1, 0, p.Count - 1);
            if (p.Occ[u - c0 < 0.5f ? ca : cb]) continue;
            float h = Surface(px);
            if (h < 0.3f * s) continue;

            // Light from the top-left: the faces turned away from it go slightly grey. The slope is
            // measured over a few pixels, not one — column to column it jumps about, and shading
            // each column on its own turns the whole pile into vertical stripes.
            float slope = (Surface(px + 3) - Surface(px - 3)) * -0.1667f;
            float lum = (0.83f - 0.55f * slope) / MathF.Sqrt(slope * slope + 1);
            float shade = Math.Clamp(0.80f + 0.25f * lum, 0.80f, 1f);

            float top = baseY - h;
            for (int y = Math.Max(0, (int)MathF.Floor(baseY)); y < Math.Min(bh, (int)MathF.Ceiling(shEnd)); y++)
            {
                float cov = MathF.Min(1, shEnd - y);
                Blend(buf + (long)y * bw + px, 0.9f * cov, 0.70f, 0.78f, 0.92f);
            }
            int yTop = (int)MathF.Floor(top);
            float invH = 1f / MathF.Max(1f, h);
            for (int y = Math.Max(0, yTop); y < yBase; y++)
            {
                float v = Math.Clamp((y + 0.5f - top) * invH, 0, 1);
                float t = v * v * 0.85f;                        // the crust near the surface stays white
                float cov = y == yTop ? 1 - (top - yTop) : 1;   // anti-aliased top edge
                // the slope only lights the crust; deeper down every column is the same, or the
                // differences between neighbouring columns would read as stripes
                float m = 1 - (1 - shade) * Math.Clamp(1 - v * 2.2f, 0, 1);
                Blend(buf + (long)y * bw + px, 0.97f * cov,
                      (1 - 0.26f * t) * m, (1 - 0.19f * t) * m, (1 - 0.07f * t) * m);
            }
        }

        // sparse grain, the same bluish specks the snowballs are made of
        for (int c = 2; c < p.Count; c += 11)
        {
            if (p.Occ[c] || p.H[c] < 4) continue;
            float g = WinterIsComing.Pile.Ripple(c * 1.7f, p.Seed + 3);
            float depth = 0.35f + 0.3f * (0.5f + 0.5f * g);
            FillCircle(buf, bw, bh, (p.ColX(c) + g * 1.1f) * s, baseY - p.H[c] * s * depth,
                       MathF.Max(0.7f, (0.8f + 0.5f * g) * s), 0.72f, 0.78f, 0.91f, 0.42f);
        }
    }

    // MARK: Shape primitives (anti-aliased, used for snowballs and snowman decorations)

    public static void FillCircle(uint* buf, int bw, int bh, float cx, float cy, float r,
                                  float cr, float cg, float cb, float alpha)
    {
        if (r <= 0) return;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - r - 1)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + r + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - r - 1)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + r + 1));
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            float dy = y + 0.5f - cy;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                float cov = Math.Clamp(r - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0, 1);
                if (cov > 0) Blend(row + x, cov * alpha, cr, cg, cb);
            }
        }
    }

    /// Line segment with round caps — the snowman's stick arms
    public static void FillSegment(uint* buf, int bw, int bh, float ax, float ay, float bx, float by, float half,
                                   float cr, float cg, float cb, float alpha)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, bx) - half - 1));
        int x1 = Math.Min(bw - 1, (int)MathF.Ceiling(MathF.Max(ax, bx) + half + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, by) - half - 1));
        int y1 = Math.Min(bh - 1, (int)MathF.Ceiling(MathF.Max(ay, by) + half + 1));
        float ex = bx - ax, ey = by - ay;
        float len2 = MathF.Max(1e-5f, ex * ex + ey * ey);
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f - ax, py = y + 0.5f - ay;
                float tt = Math.Clamp((px * ex + py * ey) / len2, 0, 1);
                float qx = px - ex * tt, qy = py - ey * tt;
                float cov = Math.Clamp(half - MathF.Sqrt(qx * qx + qy * qy) + 0.5f, 0, 1);
                if (cov > 0) Blend(row + x, cov * alpha, cr, cg, cb);
            }
        }
    }

    /// Convex polygon, optionally rounded off by `round` (pass the inset outline to get a rounded rect)
    public static void FillPoly(uint* buf, int bw, int bh, float[] vx, float[] vy, int n, float round,
                                float cr, float cg, float cb, float alpha)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        float area = 0;
        for (int i = 0; i < n; i++)
        {
            minX = MathF.Min(minX, vx[i]); maxX = MathF.Max(maxX, vx[i]);
            minY = MathF.Min(minY, vy[i]); maxY = MathF.Max(maxY, vy[i]);
            int j = (i + 1) % n;
            area += vx[i] * vy[j] - vx[j] * vy[i];
        }
        float sign = area >= 0 ? 1 : -1;      // so the normals point outwards whichever way it was wound
        int x0 = Math.Max(0, (int)MathF.Floor(minX - round - 1)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(maxX + round + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(minY - round - 1)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(maxY + round + 1));
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float d = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ex = vx[j] - vx[i], ey = vy[j] - vy[i];
                    float len = MathF.Max(1e-5f, MathF.Sqrt(ex * ex + ey * ey));
                    float nx = sign * ey / len, ny = -sign * ex / len;
                    d = MathF.Max(d, (px - vx[i]) * nx + (py - vy[i]) * ny);
                }
                float cov = Math.Clamp(round - d + 0.5f, 0, 1);
                if (cov > 0) Blend(row + x, cov * alpha, cr, cg, cb);
            }
        }
    }

    // MARK: Snowballs

    static readonly float[] polyX = new float[4], polyY = new float[4];

    static void Rect(uint* buf, int bw, int bh, float cx, float cy, float x0, float y0, float x1, float y1,
                     float round, float rot, float cr, float cg, float cb)
    {
        float cs = MathF.Cos(rot), sn = MathF.Sin(rot);
        // inset by `round` so the rounded-off outline matches the requested rectangle
        float ix0 = x0 + round, iy0 = y0 + round, ix1 = x1 - round, iy1 = y1 - round;
        if (ix1 < ix0) { ix0 = ix1 = (x0 + x1) * 0.5f; }
        if (iy1 < iy0) { iy0 = iy1 = (y0 + y1) * 0.5f; }
        float[] lx = { ix0, ix1, ix1, ix0 }, ly = { iy0, iy0, iy1, iy1 };
        for (int i = 0; i < 4; i++)
        {
            polyX[i] = cx + lx[i] * cs - ly[i] * sn;
            polyY[i] = cy + lx[i] * sn + ly[i] * cs;
        }
        FillPoly(buf, bw, bh, polyX, polyY, 4, round, cr, cg, cb, 1);
    }

    /// A snowball: lit from the top-left, bluish shade at the bottom-right, with specks that rotate as it rolls
    public static void Ball(uint* buf, int bw, int bh, Snowball ball, float s)
    {
        float cx = ball.X * s, cy = ball.Y * s, r = ball.R * s;
        if (r < 0.5f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - r - 1)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + r + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - r - 1)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + r + 1));
        float lx = cx - 0.24f * r, ly = cy - 0.28f * r;     // where the light comes from
        float invEnd = 1f / (1.1f * r);
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float cov = Math.Clamp(r - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0, 1);
                if (cov <= 0) continue;
                float ex = x + 0.5f - lx, ey = y + 0.5f - ly;
                float tt = Math.Clamp(MathF.Sqrt(ex * ex + ey * ey) * invEnd, 0, 1);
                float cr, cg, cb;
                if (tt < 0.55f) { float u = tt / 0.55f; cr = 1 - 0.06f * u; cg = 1 - 0.04f * u; cb = 1; }
                else { float u = (tt - 0.55f) / 0.45f; cr = 0.94f - 0.26f * u; cg = 0.96f - 0.21f * u; cb = 1 - 0.12f * u; }
                Blend(row + x, cov, cr, cg, cb);
            }
        }

        float rc = MathF.Cos(ball.Spin), rs = MathF.Sin(ball.Spin);
        for (int i = 0; i < Snowball.SpeckCount; i++)
        {
            float a = ball.Specks[i * 4], rad = ball.Specks[i * 4 + 1];
            float sz = ball.Specks[i * 4 + 2], al = ball.Specks[i * 4 + 3];
            float ux = MathF.Cos(a) * rad, uy = MathF.Sin(a) * rad;
            float px = cx + (ux * rc - uy * rs) * r, py = cy + (ux * rs + uy * rc) * r;
            FillCircle(buf, bw, bh, px, py, MathF.Max(0.6f, sz * r), 0.66f, 0.73f, 0.87f, al);
        }
    }

    /// Snowman trimmings: face and scarf on the head ball, buttons and stick arms on the body ball
    public static void Decor(uint* buf, int bw, int bh, Snowball ball, float s)
    {
        float cx = ball.X * s, cy = ball.Y * s, R = ball.R * s;
        const float darkR = 0.08f, darkG = 0.08f, darkB = 0.08f;

        if (ball.Decor == 1)
        {
            // eyes
            FillCircle(buf, bw, bh, cx - R * 0.30f, cy - R * 0.22f, MathF.Max(1, R * 0.09f), darkR, darkG, darkB, 1);
            FillCircle(buf, bw, bh, cx + R * 0.30f, cy - R * 0.22f, MathF.Max(1, R * 0.09f), darkR, darkG, darkB, 1);
            // carrot nose
            polyX[0] = cx + R * 0.02f; polyY[0] = cy - R * 0.06f;
            polyX[1] = cx + R * 0.75f; polyY[1] = cy + R * 0.04f;
            polyX[2] = cx + R * 0.02f; polyY[2] = cy + R * 0.12f;
            FillPoly(buf, bw, bh, polyX, polyY, 3, 0, 0.96f, 0.52f, 0.13f, 1);
            // mouth
            for (int k = 0; k < 5; k++)
            {
                float a = MathF.PI * (1.2f + 0.15f * k);
                FillCircle(buf, bw, bh, cx + MathF.Cos(a) * R * 0.42f, cy - MathF.Sin(a) * R * 0.42f + R * 0.05f,
                           MathF.Max(0.7f, R * 0.05f), darkR, darkG, darkB, 1);
            }
            // scarf and its tail
            Rect(buf, bw, bh, cx, cy, -R * 0.85f, R * 0.68f, R * 0.85f, R * 0.98f, R * 0.12f, 0, 0.82f, 0.16f, 0.18f);
            Rect(buf, bw, bh, cx, cy, R * 0.30f, R * 0.85f, R * 0.58f, R * 1.55f, R * 0.08f, 0.15f, 0.82f, 0.16f, 0.18f);
        }
        else if (ball.Decor == 2)
        {
            float B = R;
            for (int k = 0; k < 3; k++)
                FillCircle(buf, bw, bh, cx, cy + B * (0.33f * k - 0.35f), MathF.Max(0.8f, B * 0.07f),
                           darkR, darkG, darkB, 1);
            float lw = MathF.Max(1.2f, B * 0.09f) * 0.5f;
            for (int k = 0; k < 2; k++)
            {
                float sx = k == 0 ? -1 : 1;
                FillSegment(buf, bw, bh, cx + sx * B * 0.85f, cy - B * 0.15f,
                            cx + sx * B * 1.75f, cy - B * 0.65f, lw, 0.42f, 0.28f, 0.16f, 1);
                FillSegment(buf, bw, bh, cx + sx * B * 1.40f, cy - B * 0.45f,
                            cx + sx * B * 1.55f, cy - B * 0.85f, lw, 0.42f, 0.28f, 0.16f, 1);
            }
        }
    }

    // MARK: Snowplough

    static readonly float[] pvx = new float[6], pvy = new float[6];

    /// Rounded box with a vertical gradient, given in screen pixels
    static void GradBox(uint* buf, int bw, int bh, float xa, float xb, float ya, float yb, float round,
                        float r0, float g0, float b0, float r1, float g1, float b1, float alpha)
    {
        float lo = MathF.Min(xa, xb), hi = MathF.Max(xa, xb);
        float top = MathF.Min(ya, yb), bot = MathF.Max(ya, yb);
        float r = MathF.Max(0, MathF.Min(round, MathF.Min((hi - lo) * 0.5f, (bot - top) * 0.5f)));
        float ix0 = lo + r, ix1 = hi - r, iy0 = top + r, iy1 = bot - r;
        float inv = 1f / MathF.Max(1e-3f, bot - top);
        int x0 = Math.Max(0, (int)MathF.Floor(lo - 1)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(hi + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(top - 1)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(bot + 1));
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            float py = y + 0.5f;
            float ddy = MathF.Max(iy0 - py, py - iy1);
            float t = Math.Clamp((py - top) * inv, 0, 1);
            float cr = r0 + (r1 - r0) * t, cg = g0 + (g1 - g0) * t, cb = b0 + (b1 - b0) * t;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float ddx = MathF.Max(ix0 - px, px - ix1);
                // signed distance to the inset rectangle, so pixels well inside stay fully covered
                float dd = ddx > 0 || ddy > 0
                    ? MathF.Sqrt(MathF.Max(ddx, 0) * MathF.Max(ddx, 0) + MathF.Max(ddy, 0) * MathF.Max(ddy, 0))
                    : MathF.Max(ddx, ddy);
                float cov = Math.Clamp(r - dd + 0.5f, 0, 1);
                if (cov > 0) Blend(row + x, cov * alpha, cr, cg, cb);
            }
        }
    }

    /// Convex polygon with a vertical gradient. `round` dilates the outline, so drawing the same
    /// vertices twice — once with round + outline in a dark colour, once with round — gives a clean edge.
    static void GradPoly(uint* buf, int bw, int bh, float[] vx, float[] vy, int n, float round,
                         float r0, float g0, float b0, float r1, float g1, float b1, float alpha)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        float area = 0;
        for (int i = 0; i < n; i++)
        {
            minX = MathF.Min(minX, vx[i]); maxX = MathF.Max(maxX, vx[i]);
            minY = MathF.Min(minY, vy[i]); maxY = MathF.Max(maxY, vy[i]);
            int j = (i + 1) % n;
            area += vx[i] * vy[j] - vx[j] * vy[i];
        }
        float sign = area >= 0 ? 1 : -1;
        float inv = 1f / MathF.Max(1e-3f, maxY - minY + 2 * round);
        int x0 = Math.Max(0, (int)MathF.Floor(minX - round - 1)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(maxX + round + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(minY - round - 1)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(maxY + round + 1));
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            float py = y + 0.5f;
            float t = Math.Clamp((py - minY + round) * inv, 0, 1);
            float cr = r0 + (r1 - r0) * t, cg = g0 + (g1 - g0) * t, cb = b0 + (b1 - b0) * t;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float dd = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ex = vx[j] - vx[i], ey = vy[j] - vy[i];
                    float len = MathF.Max(1e-5f, MathF.Sqrt(ex * ex + ey * ey));
                    float nx = sign * ey / len, ny = -sign * ex / len;
                    dd = MathF.Max(dd, (px - vx[i]) * nx + (py - vy[i]) * ny);
                }
                float cov = Math.Clamp(round - dd + 0.5f, 0, 1);
                if (cov > 0) Blend(row + x, cov * alpha, cr, cg, cb);
            }
        }
    }

    /// A body panel: dark outline first, then the gradient fill on top of it
    static void Panel(uint* buf, int bw, int bh, float xa, float xb, float ya, float yb, float round, float outline,
                      float r0, float g0, float b0, float r1, float g1, float b1)
    {
        float lo = MathF.Min(xa, xb), hi = MathF.Max(xa, xb);
        float top = MathF.Min(ya, yb), bot = MathF.Max(ya, yb);
        if (outline > 0)
            GradBox(buf, bw, bh, lo - outline, hi + outline, top - outline, bot + outline, round + outline,
                    0.13f, 0.10f, 0.08f, 0.13f, 0.10f, 0.08f, 0.85f);
        GradBox(buf, bw, bh, lo, hi, top, bot, round, r0, g0, b0, r1, g1, b1, 1);
    }

    // How far forward the face of the blade moves per unit of height, at its steepest. The heap is
    // clipped to this line so it never shows a white wall sticking out from behind the board; taking
    // the steepest part of the curve keeps the snow tucked in rather than leaving a gap.
    const float BladeLean = 0.12f;

    /// Soft radial glow — beacon light, headlight halo, exhaust puffs. A flat translucent circle
    /// would show up as a hard-edged grey disc against the desktop, which is what this avoids.
    static void Glow(uint* buf, int bw, int bh, float cx, float cy, float r, float cr, float cg, float cb, float a)
    {
        if (r <= 0 || a <= 0) return;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - r)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + r));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - r)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + r));
        float inv = 1f / r;
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            float dy = (y + 0.5f - cy) * inv;
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * inv;
                float t = 1 - MathF.Sqrt(dx * dx + dy * dy);
                if (t <= 0) continue;
                Blend(row + x, a * t * t, cr, cg, cb);
            }
        }
    }

    static float Bump(float u, float c, float w, float a)
    {
        float t = MathF.Abs(u - c);
        return t >= w ? 0 : a * 0.5f * (1 + MathF.Cos(MathF.PI * t / w));
    }

    // Specks scattered through the shoved heap, in (along, up, size) heap-local coordinates, so the
    // heap is made of the same grainy snow as the snowballs rather than being a flat white wedge.
    static readonly float[] heapSpecks =
    {
        0.06f, 0.62f, 0.055f,  0.11f, 0.25f, 0.040f,  0.19f, 0.70f, 0.048f,
        0.24f, 0.38f, 0.034f,  0.33f, 0.60f, 0.044f,  0.39f, 0.20f, 0.030f,
        0.47f, 0.52f, 0.038f,  0.56f, 0.30f, 0.028f,  0.63f, 0.58f, 0.032f,
        0.74f, 0.35f, 0.026f,  0.83f, 0.45f, 0.022f,
    };

    /// The heap being shoved along in front of the blade: a lumpy mound, lit from the top-left and
    /// shaded towards the ground, drawn column by column so its base sits flat on the snow line.
    static void PlowHeap(uint* buf, int bw, int bh, Snowplow p, float gy, float k, float d, float back, float s)
    {
        float hh = MathF.Min(18, MathF.Sqrt(p.Heap) * 0.95f) * k;
        if (hh < 1.5f) return;
        float hw = hh * 2.6f;

        float Height(float u)
        {
            if (u <= 0 || u >= 1) return 0;
            float h = hh * MathF.Pow(1 - u, 1.35f);
            h += Bump(u, 0.17f, 0.16f, 0.15f * hh);
            h += Bump(u, 0.45f, 0.15f, 0.10f * hh);
            h += Bump(u, 0.72f, 0.13f, 0.06f * hh);
            // The board leans forward, so close to it the snow can only be as tall as the board face
            // is at that point. Without this the heap shows a white wall standing behind the blade.
            return MathF.Min(h, u * hw / BladeLean);
        }

        int pxa = Math.Max(0, (int)MathF.Floor(MathF.Min(back, back + d * hw)) - 1);
        int pxb = Math.Min(bw - 1, (int)MathF.Ceiling(MathF.Max(back, back + d * hw)) + 1);
        float du = 1f / MathF.Max(1f, hw);
        float shEnd = gy + 1.5f * s;
        for (int px = pxa; px <= pxb; px++)
        {
            float u = (px + 0.5f - back) * d * du;
            if (u < 0 || u > 1) continue;
            float h = Height(u);
            if (h < 0.4f) continue;

            // surface slope in screen pixels, so the face turned away from the light goes grey
            float slope = -d * (Height(Math.Clamp(u + du, 0, 1)) - Height(Math.Clamp(u - du, 0, 1))) * 0.5f;
            float lum = (0.83f - 0.55f * slope) / MathF.Sqrt(slope * slope + 1);
            float shade = Math.Clamp(0.60f + 0.48f * lum, 0.60f, 1f);

            float top = gy - h;
            int yTop = (int)MathF.Floor(top);
            float invH = 1f / MathF.Max(1f, h);
            for (int y = Math.Max(0, yTop); y < Math.Min(bh, (int)MathF.Ceiling(gy)); y++)
            {
                float v = Math.Clamp((y + 0.5f - top) * invH, 0, 1);
                float t = v * v * 0.9f;
                float cov = y == yTop ? MathF.Min(1, 1 - (top - yTop)) : 1;
                Blend(buf + (long)y * bw + px, 0.97f * cov,
                      (1 - 0.30f * t) * shade, (1 - 0.22f * t) * shade, (1 - 0.09f * t) * shade);
            }
            for (int y = Math.Max(0, (int)MathF.Floor(gy)); y < Math.Min(bh, (int)MathF.Ceiling(shEnd)); y++)
                Blend(buf + (long)y * bw + px, 0.85f * MathF.Min(1, shEnd - y), 0.70f, 0.78f, 0.92f);
        }

        for (int i = 0; i < heapSpecks.Length; i += 3)
        {
            float u = heapSpecks[i], v = heapSpecks[i + 1];
            float h = Height(u);
            if (h < 3) continue;
            FillCircle(buf, bw, bh, back + d * u * hw, gy - h * v,
                       MathF.Max(0.7f, heapSpecks[i + 2] * hh), 0.70f, 0.76f, 0.90f, 0.5f);
        }

        // a couple of clods that have tumbled off the front of the heap
        for (int i = 0; i < 2; i++)
        {
            float r = hh * (i == 0 ? 0.10f : 0.07f);
            if (r < 1.1f) continue;
            float cx = back + d * hw * (i == 0 ? 1.07f : 1.19f), cy = gy - r * 0.9f;
            FillCircle(buf, bw, bh, cx, cy, r, 0.99f, 0.99f, 1f, 0.97f);
            FillCircle(buf, bw, bh, cx + r * 0.3f, cy + r * 0.28f, r * 0.6f, 0.80f, 0.85f, 0.94f, 0.55f);
        }
    }

    public static void PlowTruck(uint* buf, int bw, int bh, Snowplow p, float groundTop, float s)
    {
        float d = p.Dir, gy = groundTop * s, k = Snowplow.Scale * s;
        float SX(float lx) => p.X * s + d * lx * k;   // local x: 0 is the blade tip, negative is behind
        float SY(float ly) => gy + ly * k;            // local y: 0 is the ground, negative is up
        float o = MathF.Max(0.75f, 0.55f * k);        // outline thickness

        PlowHeap(buf, bw, bh, p, gy, k, d, SX(-5.8f), s);

        // soft shadow on the ground under the truck
        GradBox(buf, bw, bh, SX(-42), SX(-5), SY(-1.4f), SY(1.6f), 1.5f * k, 0.04f, 0.05f, 0.10f, 0.04f, 0.05f, 0.10f, 0.20f);
        GradBox(buf, bw, bh, SX(-37), SX(-9), SY(-2.6f), SY(1.2f), 1.8f * k, 0.04f, 0.05f, 0.10f, 0.04f, 0.05f, 0.10f, 0.16f);

        // exhaust: the puffs climb and fade as the truck rolls
        float clock = MathF.Abs(p.Wheel);
        for (int i = 0; i < 3; i++)
        {
            float ph = (clock * 0.09f + i * 0.3333f) % 1f;
            Glow(buf, bw, bh, SX(-37.5f - 7.5f * ph), SY(-37f - 13f * ph),
                 (2.4f + 4.6f * ph) * k, 0.80f, 0.82f, 0.86f, 0.42f * (1 - ph));
        }

        // wheels
        for (int i = 0; i < 2; i++)
        {
            float wx = SX(i == 0 ? -33 : -13), wy = SY(-6.6f), wr = 6.4f * k;
            FillCircle(buf, bw, bh, wx, wy, wr + o, 0.09f, 0.08f, 0.09f, 0.9f);
            FillCircle(buf, bw, bh, wx, wy, wr, 0.13f, 0.14f, 0.16f, 1);
            for (int t = 0; t < 7; t++)   // tread blocks, so you can see it roll
            {
                float a = p.Wheel + t * 0.8976f;
                FillCircle(buf, bw, bh, wx + MathF.Cos(a) * wr * 0.88f, wy + MathF.Sin(a) * wr * 0.88f,
                           0.85f * k, 0.28f, 0.29f, 0.33f, 1);
            }
            FillCircle(buf, bw, bh, wx, wy, wr * 0.46f, 0.60f, 0.62f, 0.67f, 1);
            FillCircle(buf, bw, bh, wx - wr * 0.11f, wy - wr * 0.13f, wr * 0.33f, 0.80f, 0.82f, 0.86f, 1);
            for (int t = 0; t < 4; t++)   // hub bolts
            {
                float a = p.Wheel + t * 1.5708f;
                FillCircle(buf, bw, bh, wx + MathF.Cos(a) * wr * 0.28f, wy + MathF.Sin(a) * wr * 0.28f,
                           MathF.Max(0.5f, 0.5f * k), 0.42f, 0.44f, 0.48f, 1);
            }
        }

        // chassis under the body, reaching forward to carry the blade
        Panel(buf, bw, bh, SX(-38), SX(-6), SY(-10.5f), SY(-6.2f), 1.2f * k, 0, 0.24f, 0.25f, 0.28f, 0.13f, 0.14f, 0.16f);

        // push frame out to the blade: a beam, a diagonal strut and the post the board hangs on
        FillSegment(buf, bw, bh, SX(-13.5f), SY(-11.5f), SX(-7.6f), SY(-15.5f), 0.9f * k, 0.22f, 0.23f, 0.26f, 1);
        GradBox(buf, bw, bh, SX(-14), SX(-6.8f), SY(-11.6f), SY(-8.8f), 0.5f * k, 0.30f, 0.31f, 0.34f, 0.18f, 0.19f, 0.22f, 1);
        GradBox(buf, bw, bh, SX(-8.2f), SX(-6.6f), SY(-17.5f), SY(-3.5f), 0.5f * k, 0.30f, 0.31f, 0.34f, 0.18f, 0.19f, 0.22f, 1);

        // body, lit along the top and deepening towards the chassis
        Panel(buf, bw, bh, SX(-43), SX(-9.5f), SY(-22), SY(-7.5f), 2.6f * k, o, 1f, 0.66f, 0.16f, 0.74f, 0.34f, 0.03f);
        GradBox(buf, bw, bh, SX(-41.5f), SX(-11), SY(-15.5f), SY(-13.5f), 1f * k,
                0.42f, 0.22f, 0.05f, 0.42f, 0.22f, 0.05f, 0.5f);                       // rubbing strip
        for (int i = 0; i < 4; i++)                                                    // ribs on the tipper body
            GradBox(buf, bw, bh, SX(-39f + 7.3f * i), SX(-37.6f + 7.3f * i), SY(-21.2f), SY(-8.4f), 0.6f * k,
                    0.52f, 0.27f, 0.05f, 0.52f, 0.27f, 0.05f, 0.35f);
        GradBox(buf, bw, bh, SX(-42), SX(-10.5f), SY(-23.6f), SY(-21.6f), 1f * k,
                1f, 1f, 1f, 0.86f, 0.90f, 0.96f, 0.95f);                               // snow caught on the body

        // cab: a boxy shell with the windscreen raked back
        pvx[0] = SX(-32.8f); pvy[0] = SY(-23.2f);
        pvx[1] = SX(-32.8f); pvy[1] = SY(-31.8f);
        pvx[2] = SX(-23.0f); pvy[2] = SY(-31.8f);
        pvx[3] = SX(-19.2f); pvy[3] = SY(-26.6f);
        pvx[4] = SX(-19.2f); pvy[4] = SY(-23.2f);
        GradPoly(buf, bw, bh, pvx, pvy, 5, 2.2f * k + o, 0.13f, 0.10f, 0.08f, 0.13f, 0.10f, 0.08f, 0.85f);
        GradPoly(buf, bw, bh, pvx, pvy, 5, 2.2f * k, 1f, 0.68f, 0.18f, 0.80f, 0.40f, 0.05f, 1);

        // windscreen and side glass, with a slanted highlight across it
        pvx[0] = SX(-31.3f); pvy[0] = SY(-30.6f);
        pvx[1] = SX(-24.3f); pvy[1] = SY(-30.6f);
        pvx[2] = SX(-21.0f); pvy[2] = SY(-27.4f);
        pvx[3] = SX(-31.3f); pvy[3] = SY(-27.4f);
        GradPoly(buf, bw, bh, pvx, pvy, 4, 1.1f * k, 0.44f, 0.60f, 0.73f, 0.20f, 0.31f, 0.43f, 1);
        pvx[0] = SX(-29.5f); pvy[0] = SY(-30.4f);
        pvx[1] = SX(-27.6f); pvy[1] = SY(-30.4f);
        pvx[2] = SX(-23.2f); pvy[2] = SY(-26.6f);
        pvx[3] = SX(-25.1f); pvy[3] = SY(-26.6f);
        GradPoly(buf, bw, bh, pvx, pvy, 4, 0.4f * k, 1f, 1f, 1f, 1f, 1f, 1f, 0.22f);

        // visor over the screen, snow on the roof, and the wing mirror
        GradBox(buf, bw, bh, SX(-33.4f), SX(-20.6f), SY(-34.4f), SY(-32.9f), 0.7f * k,
                0.62f, 0.30f, 0.05f, 0.50f, 0.23f, 0.03f, 1);
        GradBox(buf, bw, bh, SX(-33.6f), SX(-21.8f), SY(-35.3f), SY(-33.7f), 0.8f * k,
                1f, 1f, 1f, 0.84f, 0.89f, 0.96f, 0.95f);
        FillSegment(buf, bw, bh, SX(-19.6f), SY(-29.6f), SX(-17.4f), SY(-30.4f), MathF.Max(0.5f, 0.4f * k),
                    0.18f, 0.16f, 0.15f, 1);
        GradBox(buf, bw, bh, SX(-17.9f), SX(-16.3f), SY(-32.2f), SY(-29.4f), 0.5f * k,
                0.26f, 0.26f, 0.28f, 0.16f, 0.16f, 0.18f, 1);

        // exhaust stack behind the cab
        Panel(buf, bw, bh, SX(-37.2f), SX(-34.8f), SY(-36.5f), SY(-22f), 1f * k, 0, 0.46f, 0.47f, 0.50f, 0.24f, 0.25f, 0.28f);
        FillCircle(buf, bw, bh, SX(-36f), SY(-36.6f), 1.7f * k, 0.16f, 0.17f, 0.19f, 1);

        // amber beacon, pulsing
        GradBox(buf, bw, bh, SX(-30.2f), SX(-27.3f), SY(-37.4f), SY(-35.6f), 0.6f * k,
                0.20f, 0.19f, 0.20f, 0.14f, 0.13f, 0.14f, 1);
        float pulse = 0.34f + 0.3f * (0.5f + 0.5f * MathF.Sin(clock * 1.7f));
        Glow(buf, bw, bh, SX(-28.75f), SY(-38.3f), 8f * k, 1f, 0.72f, 0.15f, pulse);
        FillCircle(buf, bw, bh, SX(-28.75f), SY(-38.3f), 2.5f * k, 1f, 0.74f, 0.18f, 1);
        FillCircle(buf, bw, bh, SX(-29.3f), SY(-38.9f), 1f * k, 1f, 0.94f, 0.72f, 1);

        // headlight
        FillCircle(buf, bw, bh, SX(-11.2f), SY(-19.8f), 2.5f * k, 0.14f, 0.11f, 0.08f, 0.9f);
        FillCircle(buf, bw, bh, SX(-11.2f), SY(-19.8f), 1.8f * k, 1f, 0.95f, 0.72f, 1);
        Glow(buf, bw, bh, SX(-11.2f), SY(-19.8f), 6f * k, 1f, 0.92f, 0.60f, 0.40f);

        // The board, as three plates curling further forward the higher they go. All three outlines
        // go down first and the fills on top of them, or the shared edges would show as dark seams.
        // Back and front of the plate at each height, bottom to top:
        float[] bx = { -8.0f, -7.0f, -4.8f, -1.0f }, by = { -0.5f, -9.0f, -16.6f, -21.8f };
        float[] fx = { -5.8f, -4.8f, -2.6f, 1.2f }, fy = { -1.0f, -9.6f, -17.3f, -22.6f };
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < 3; i++)
            {
                pvx[0] = SX(bx[i]); pvy[0] = SY(by[i]);
                pvx[1] = SX(bx[i + 1]); pvy[1] = SY(by[i + 1]);
                pvx[2] = SX(fx[i + 1]); pvy[2] = SY(fy[i + 1]);
                pvx[3] = SX(fx[i]); pvy[3] = SY(fy[i]);
                if (pass == 0)
                    GradPoly(buf, bw, bh, pvx, pvy, 4, 0.9f * k + o, 0.14f, 0.15f, 0.17f, 0.14f, 0.15f, 0.17f, 0.9f);
                else
                {
                    // each plate a little brighter than the one below, so the curve reads
                    float t = i / 2f;
                    GradPoly(buf, bw, bh, pvx, pvy, 4, 0.9f * k,
                             0.72f + 0.23f * t, 0.76f + 0.21f * t, 0.84f + 0.15f * t,
                             0.52f + 0.22f * t, 0.56f + 0.21f * t, 0.64f + 0.19f * t, 1);
                }
            }
        FillSegment(buf, bw, bh, SX(-0.6f), SY(-21.9f), SX(0.9f), SY(-22.4f), 0.8f * k, 1f, 1f, 1f, 0.85f);
        FillSegment(buf, bw, bh, SX(-7.8f), SY(-0.6f), SX(-6.0f), SY(-0.9f), 0.8f * k, 0.28f, 0.30f, 0.34f, 1);

        // snow spraying over the top lip
        for (int i = 0; i < 3; i++)
        {
            float a = clock * 0.7f + i * 2.1f;
            FillCircle(buf, bw, bh, SX(-0.5f + 2.6f * i), SY(-23.6f - 1.6f * i + MathF.Sin(a) * 1.4f),
                       MathF.Max(0.7f, (1.5f - 0.25f * i) * k), 1f, 1f, 1f, 0.55f - 0.12f * i);
        }
    }

    /// Is this point covered by a window in front of the one at index `j`?
    static bool HiddenInFront(SnowWorld w, int j, float x, float y)
    {
        for (int i = 0; i < j; i++) if (w.Order[i].Contains(x, y)) return true;
        return false;
    }

    /// Strings of fairy lights: the drooping wire first, then a soft glow and a bright core for
    /// every bulb. Back window to front, and anything behind a window in front of it is skipped.
    public static void Lights(uint* buf, int bw, int bh, SnowWorld w, float s)
    {
        float t = w.Time;
        float windSway = Cfg.Wind * 3.5f * (0.65f + 0.35f * MathF.Sin(t * 0.73f));
        for (int j = w.Order.Count - 1; j >= 0; j--)
        {
            var win = w.Order[j];
            if (!w.Lights.TryGetValue(win.Id, out var ls) || ls.Bulbs.Length == 0) continue;
            float swing = ls.Swing(t), sway = ls.Sway(t) + windSway;

            float xa = ls.L + Cfg.LightInset * 0.5f, xb = ls.L + ls.Width - Cfg.LightInset * 0.5f;
            int px0 = Math.Max(0, (int)MathF.Floor(xa * s)), px1 = Math.Min(bw - 1, (int)MathF.Ceiling(xb * s));
            float half = 0.65f * s;
            for (int px = px0; px <= px1; px++)
            {
                float x = (px + 0.5f) / s;
                if (x < xa || x > xb) continue;
                float y = ls.WireY(x, swing);
                if (HiddenInFront(w, j, x, y)) continue;
                float cy = y * s;
                int y0 = Math.Max(0, (int)MathF.Floor(cy - half)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + half));
                for (int py = y0; py <= y1; py++)
                {
                    float cov = Math.Clamp(half - MathF.Abs(py + 0.5f - cy) + 0.5f, 0, 1);
                    if (cov > 0) Blend(buf + (long)py * bw + px, 0.8f * cov, 0.16f, 0.16f, 0.18f);
                }
            }

            for (int i = 0; i < ls.Bulbs.Length; i++)
            {
                ref var b = ref ls.Bulbs[i];
                float bx = ls.BulbX(i) + sway * (0.6f + 0.4f * MathF.Sin(b.Phase));
                float by = ls.WireY(ls.BulbX(i), swing) + 4;
                if (HiddenInFront(w, j, bx, by)) continue;
                float rev = ls.Reveal(i, t);
                if (rev <= 0) continue;
                float k = 0.75f + 0.25f * MathF.Sin(t * b.Speed + b.Phase);
                int c = b.Colour * 3;
                float cr = LightString.Colours[c], cg = LightString.Colours[c + 1], cb = LightString.Colours[c + 2];
                Glow(buf, bw, bh, bx * s, by * s, Cfg.BulbGlow * 0.5f * s, cr, cg, cb, 0.8f * k * rev);
                FillCircle(buf, bw, bh, bx * s, by * s, Cfg.BulbCore * 0.5f * s,
                           cr + (1 - cr) * 0.2f, cg + (1 - cg) * 0.2f, cb + (1 - cb) * 0.2f,
                           (0.85f + 0.15f * k) * rev);
            }
        }
    }

    static uint[] plowSave = Array.Empty<uint>();

    /// Draw the plough, then put back the pixel columns where a window hangs over the taskbar, so
    /// it drives behind that window the way the piles and the snowballs already do. Masking every
    /// drawing primitive would mean threading the mask through all of them; saving and restoring a
    /// few columns around the truck costs one small buffer and keeps the drawing code plain.
    static void Plough(uint* buf, int bw, int bh, SnowWorld w, float s)
    {
        var p = w.Plow;
        int x0 = Math.Max(0, (int)MathF.Floor((p.X - 300) * s));
        int x1 = Math.Min(bw - 1, (int)MathF.Ceiling((p.X + 300) * s));
        int y0 = Math.Max(0, (int)MathF.Floor((w.Ground.TopY - 95) * s));
        int y1 = Math.Min(bh - 1, (int)MathF.Ceiling((w.Ground.TopY + 8) * s));
        if (x1 < x0 || y1 < y0) { PlowTruck(buf, bw, bh, p, w.Ground.TopY, s); return; }

        int cols = x1 - x0 + 1, rows = y1 - y0 + 1;
        if (plowSave.Length < cols * rows) plowSave = new uint[cols * rows];

        bool any = false;
        for (int x = x0; x <= x1; x++)
        {
            int c = w.Ground.Col((x + 0.5f) / s);
            if (c < 0 || !w.Ground.Occ[c]) continue;
            any = true;
            for (int y = y0; y <= y1; y++) plowSave[(x - x0) * rows + (y - y0)] = buf[(long)y * bw + x];
        }

        PlowTruck(buf, bw, bh, p, w.Ground.TopY, s);
        if (!any) return;

        for (int x = x0; x <= x1; x++)
        {
            int c = w.Ground.Col((x + 0.5f) / s);
            if (c < 0 || !w.Ground.Occ[c]) continue;
            for (int y = y0; y <= y1; y++) buf[(long)y * bw + x] = plowSave[(x - x0) * rows + (y - y0)];
        }
    }

    static readonly List<Snowball> drawList = new();

    public static void World(uint* buf, int bw, int bh, SnowWorld w, float s)
    {
        new Span<uint>(buf, bw * bh).Clear();
        // far + middle flakes, then piles, then snowballs, then near flakes and clumps on top
        foreach (var f in w.Flakes) if (!f.Clump && f.Layer < 2) Disc(buf, bw, bh, f.X * s, f.Y * s, f.Size * 1.1f * s, f.Alpha);
        foreach (var p in w.Piles.Values) Pile(buf, bw, bh, p, s);
        Pile(buf, bw, bh, w.Ground, s);
        if (Cfg.Lights) Lights(buf, bw, bh, w, s);

        if (w.Balls.Count > 0)
        {
            drawList.Clear();
            drawList.AddRange(w.Balls);
            drawList.Sort((a, b) => b.Y.CompareTo(a.Y));   // lower on screen first, so a head covers its body
            foreach (var ball in drawList)
            {
                if (ball.Hidden || ball.Dragging) continue;
                Ball(buf, bw, bh, ball, s);
                if (ball.Decor != 0) Decor(buf, bw, bh, ball, s);
            }
            foreach (var ball in drawList)                 // the ball in your hand goes on top
                if (ball.Dragging && !ball.Hidden) Ball(buf, bw, bh, ball, s);
        }

        if (w.Plow != null) Plough(buf, bw, bh, w, s);

        foreach (var f in w.Flakes) if (f.Clump || f.Layer == 2) Disc(buf, bw, bh, f.X * s, f.Y * s, f.Size * 1.1f * s, f.Alpha);
    }

    // 32×32 tray icon: white six-armed snowflake on a blue circle (straight alpha, as icons expect)
    public static void TrayIcon(uint* buf, int n)
    {
        float c = n / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float px = x + 0.5f - c, py = y + 0.5f - c;
                float r = MathF.Sqrt(px * px + py * py);
                float bgA = Math.Clamp(c - 0.5f - r, 0, 1);
                float dist = 99;
                for (int k = 0; k < 6; k++)
                {
                    float ang = k * MathF.PI / 3, ex = MathF.Cos(ang), ey = MathF.Sin(ang);
                    float tt = Math.Clamp(px * ex + py * ey, 0, c * 0.72f);
                    float qx = px - ex * tt, qy = py - ey * tt;
                    dist = MathF.Min(dist, MathF.Sqrt(qx * qx + qy * qy));
                }
                float flake = Math.Clamp(1.7f - dist, 0, 1) * bgA;
                float rr = 0.25f + 0.75f * flake, gg = 0.47f + 0.53f * flake, bb = 0.85f + 0.15f * flake;
                uint a = (uint)(bgA * 255);
                buf[y * n + x] = (a << 24) | ((uint)(rr * 255) << 16) | ((uint)(gg * 255) << 8) | (uint)(bb * 255);
            }
    }
}

// MARK: - Win32 plumbing

static unsafe class Program
{
    // --- constants
    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
               WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_EXSTYLE = -20;
    const uint WM_DESTROY = 0x2, WM_TIMER = 0x113, WM_DISPLAYCHANGE = 0x7E, WM_NULL = 0,
               WM_APP = 0x8000, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
               WM_RBUTTONUP = 0x205, WM_CAPTURECHANGED = 0x215, WM_HOTKEY = 0x312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_S = 0x53;
    const int HOTKEY_ID = 1;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    const uint WM_TRAY = WM_APP + 1;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint ULW_ALPHA = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const int CMD_PAUSE = 1, CMD_CLEAR = 2, CMD_QUIT = 3, CMD_BALLS = 4, CMD_PLOW = 5, CMD_AUTOPLOW = 6,
              CMD_FAIRY = 7, CMD_LIGHT = 10, CMD_MEDIUM = 11, CMD_HEAVY = 12, CMD_WIND = 20;

    // Wind settings, strongest from the left to strongest from the right
    static readonly (float V, string Label)[] WindSteps =
    {
        (1.0f, "Gale from the left"),
        (0.4f, "Breeze from the left"),
        (0f, "Calm"),
        (-0.4f, "Breeze from the right"),
        (-1.0f, "Gale from the right"),
    };

    // --- structs
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    delegate IntPtr WndProcFn(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // --- imports
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                         IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                           ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    // --- state
    static WndProcFn wndProc;                 // kept alive so the GC doesn't collect the callback
    static EnumWindowsProc enumProc;
    static IntPtr overlay, tray, screenDC, memDC, dib, bits, trayIcon;
    static int bw, bh;                        // buffer size in physical pixels
    static float scale = 1;                   // physical pixels per logical pixel (DPI / 96)
    static SnowWorld world;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double lastT;
    static bool paused;

    /// The one switch: a left click on the tray icon, Ctrl+Alt+S and the menu item all come here
    static void TogglePause()
    {
        paused = !paused;
        if (paused) { world.MouseUp(); SetClickThrough(true); }
        ShowWindow(overlay, paused ? SW_HIDE : SW_SHOWNOACTIVATE);
        if (!paused) KeepOnTop(true);
        var d = TrayData();
        d.uFlags = NIF_TIP;
        d.szTip = paused ? "WinterIsComing — snow paused" : "WinterIsComing ❄";
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    /// Put the overlay back on top.
    ///
    /// WS_EX_TOPMOST is not a promise. The style bit stays set, but the window can still be pushed
    /// down the z-order — by another process raising itself, by a shell restart, by a full-screen
    /// app — and then the snow goes on falling behind every window, which looks exactly like the
    /// snow having stopped. Nothing tells us when it happens, so we simply put it back once a
    /// second. Checking first would mean another round of window enumeration for no gain.
    static float topAcc;

    static void KeepOnTop(bool force)
    {
        if (paused) return;
        SetWindowPos(overlay, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (force) topAcc = 0;
    }
    static bool clickThrough = true;          // WS_EX_TRANSPARENT currently set?
    static string diagPath;
    static double lastDiag;
    static string shotDir;
    static double lastShot;
    static int shotNo;
    static readonly List<Win> found = new();
    static int overlayZ;             // how many accepted windows are in front of the overlay
    static int taskbarZ;                      // how many accepted windows are drawn in front of the taskbar
    static readonly StringBuilder clsName = new(64);

    [STAThread]
    static void Main(string[] args)
    {
        // --diag <file>: dump what the app thinks the desktop looks like, every couple of seconds.
        // Only for working out why the snow is landing somewhere odd; off unless asked for.
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--diag") diagPath = args[i + 1];
        // --shots <folder>: save the next few rendered frames as BMPs and stop. This is the overlay's
        // own output, not a picture of the screen — the way to see what it is actually drawing.
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--shots") shotDir = args[i + 1];

        SetProcessDpiAwarenessContext((IntPtr)(-4));   // per-monitor DPI aware v2: real pixel coordinates
        IntPtr inst = GetModuleHandleW(null);

        wndProc = WndProc;
        enumProc = EnumProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = inst,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = "WinterIsComingWnd",
        };
        RegisterClassExW(ref wc);

        bw = GetSystemMetrics(0);   // SM_CXSCREEN (primary monitor)
        bh = GetSystemMetrics(1);   // SM_CYSCREEN
        // Transparent, click-through, always on top, never takes focus, not in Alt+Tab
        overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                                  "WinterIsComingWnd", "WinterIsComing", WS_POPUP, 0, 0, bw, bh,
                                  IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        // A hidden window that owns the tray icon and its menu
        tray = CreateWindowExW(WS_EX_TOOLWINDOW, "WinterIsComingWnd", "WinterIsComing tray", WS_POPUP, 0, 0, 0, 0,
                               IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        // A switch that works wherever you are, even with the tray icon tucked under the arrow
        RegisterHotKey(tray, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_S);

        uint dpi = GetDpiForWindow(overlay);
        scale = dpi > 0 ? dpi / 96f : 1;
        screenDC = GetDC(IntPtr.Zero);
        CreateSurface();
        world = new SnowWorld(bw / scale, bh / scale);

        ShowWindow(overlay, SW_SHOWNOACTIVATE);
        AddTrayIcon();
        SetTimer(overlay, (UIntPtr)1, Cfg.FrameMs, IntPtr.Zero);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    static void CreateSurface()
    {
        if (memDC == IntPtr.Zero) memDC = CreateCompatibleDC(IntPtr.Zero);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = bw, biHeight = -bh,   // negative = top-down rows
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            }
        };
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memDC, dib);
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                if (hWnd == overlay) Tick();
                return IntPtr.Zero;
            case WM_DISPLAYCHANGE:
                if (hWnd == overlay)
                {
                    bw = GetSystemMetrics(0);
                    bh = GetSystemMetrics(1);
                    CreateSurface();
                    world.Resize(bw / scale, bh / scale);
                }
                return IntPtr.Zero;
            case WM_TRAY:
                // Left click is the switch, right click opens the menu — the Windows convention,
                // and it means the snow can be stopped without going hunting through a menu.
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_LBUTTONUP) TogglePause();
                else if (ev == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;
            case WM_HOTKEY:
                if (wParam.ToInt32() == HOTKEY_ID) TogglePause();
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                // Only reaches us while the cursor is over a snowball (see SetClickThrough)
                if (hWnd == overlay && !paused)
                {
                    GetCursorPos(out POINT dp);
                    world.MouseDown(dp.X / scale, dp.Y / scale);
                    SetCapture(overlay);
                }
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (hWnd == overlay)
                {
                    ReleaseCapture();
                    world.MouseUp();
                }
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                if (hWnd == overlay) world.MouseUp();   // something else grabbed the mouse: drop the ball
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // The overlay normally lets every click through. Only while the mouse is over a snowball
    // (or dragging one) does it catch clicks, so you can grab the ball without clicking the window underneath.
    static void SetClickThrough(bool on)
    {
        if (on == clickThrough) return;
        clickThrough = on;
        long ex = GetWindowLongPtrW(overlay, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtrW(overlay, GWL_EXSTYLE, (IntPtr)ex);
    }

    static void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - lastT, 1.0 / 20);
        lastT = now;
        if (paused) return;

        GetCursorPos(out POINT mp);
        float mx = mp.X / scale, my = mp.Y / scale;

        world.Update(dt, ReadWindows, ReadTaskbarTop, ReadTaskbarZ, mx, my);
        SetClickThrough(!world.WantsMouse(mx, my));
        Render.World((uint*)bits, bw, bh, world, scale);

        var dst = new POINT();
        var src = new POINT();
        var size = new SIZE { cx = bw, cy = bh };
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };   // AC_SRC_ALPHA
        UpdateLayeredWindow(overlay, screenDC, ref dst, ref size, memDC, ref src, 0, ref blend, ULW_ALPHA);

        topAcc += dt;
        if (topAcc >= 1) { topAcc = 0; KeepOnTop(false); }

        if (shotDir != null && shotNo < 14 && now - lastShot > 0.05) { lastShot = now; WriteShot(); }

        if (diagPath != null && now - lastDiag > 2) { lastDiag = now; WriteDiag(); }
    }

    /// Save the frame just rendered as a 32-bit BMP, premultiplied pixels and all
    static void WriteShot()
    {
        try
        {
            Directory.CreateDirectory(shotDir);
            string path = Path.Combine(shotDir, $"frame{shotNo:00}.bmp");
            int stride = bw * 4, size = stride * bh;
            using var fs = new FileStream(path, FileMode.Create);
            using var bwr = new BinaryWriter(fs);
            bwr.Write((byte)'B'); bwr.Write((byte)'M');
            bwr.Write(54 + size); bwr.Write(0); bwr.Write(54);
            bwr.Write(40); bwr.Write(bw); bwr.Write(bh); bwr.Write((short)1); bwr.Write((short)32);
            bwr.Write(0); bwr.Write(size); bwr.Write(2835); bwr.Write(2835); bwr.Write(0); bwr.Write(0);
            var row = new byte[stride];
            uint* px = (uint*)bits;
            for (int y = bh - 1; y >= 0; y--)          // BMP rows run bottom-up
            {
                for (int x = 0; x < bw; x++)
                {
                    uint c = px[(long)y * bw + x];
                    row[x * 4 + 0] = (byte)(c & 0xFF);
                    row[x * 4 + 1] = (byte)((c >> 8) & 0xFF);
                    row[x * 4 + 2] = (byte)((c >> 16) & 0xFF);
                    row[x * 4 + 3] = (byte)(c >> 24);
                }
                bwr.Write(row);
            }
            shotNo++;
        }
        catch { shotDir = null; }
    }

    static void WriteDiag()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("WinterIsComing state at ").Append(DateTime.Now.ToString("HH:mm:ss")).Append('\n');
            sb.Append($"screen {bw}x{bh} physical, dpi scale {scale:0.00}, world {world.W:0}x{world.H:0} logical\n");
            sb.Append($"ground: topY {world.Ground.TopY:0}  onTaskbar {world.GroundIsTaskbar}  taskbarZ {world.TaskbarZ}  ");
            int occ = 0, cols = world.Ground.Count;
            float deepest = 0;
            for (int c = 0; c < cols; c++) { if (world.Ground.Occ[c]) occ++; deepest = MathF.Max(deepest, world.Ground.H[c]); }
            sb.Append($"hidden {occ}/{cols} columns  deepest {deepest:0.0}\n");
            sb.Append($"windows front-to-back ({world.Order.Count}):\n");
            for (int i = 0; i < world.Order.Count; i++)
            {
                var w = world.Order[i];
                bool overTaskbar = world.GroundIsTaskbar && w.B > world.Ground.TopY + 1;
                string pile = world.Piles.TryGetValue(w.Id, out var p)
                    ? $"snow line y={p.TopY:0} cap {p.MaxH:0} deepest {Deepest(p):0.0}"
                    : "no snow line (no room above its top edge)";
                sb.Append($"  [{i}] x {w.L:0}..{w.R:0}  y {w.T:0}..{w.B:0}  ")
                  .Append(i < world.TaskbarZ ? "in front of taskbar" : "behind taskbar")
                  .Append(overTaskbar ? ", hangs over it -> hides its snow" : ", does not hang over it")
                  .Append("  ").Append(pile).Append('\n');
            }
            sb.Append($"piles rebuilt {world.PileChurn}, shaken off by a moving window {world.MoveShakes}\n");
            sb.Append($"snow {(paused ? "PAUSED" : "running")}, {world.Flakes.Count} flakes in the air, ")
              .Append(overlayZ == 0
                      ? "overlay is in front of every window\n"
                      : $"OVERLAY IS BEHIND {overlayZ} WINDOW(S) -- the snow is falling out of sight\n");
            sb.Append($"snowballs {world.Balls.Count}, plough ")
              .Append(world.Plow == null ? "not on screen" : $"at x={world.Plow.X:0} dir {world.Plow.Dir} heap {world.Plow.Heap:0}")
              .Append($", runs so far {Snowplow.Runs}, auto {world.AutoPlow}\n");
            System.IO.File.WriteAllText(diagPath, sb.ToString());
        }
        catch { /* diagnostics must never take the app down */ }
    }

    static float Deepest(Pile p)
    {
        float m = 0;
        foreach (var h in p.H) m = MathF.Max(m, h);
        return m;
    }

    // MARK: Windows on screen

    static List<Win> ReadWindows()
    {
        found.Clear();
        taskbarZ = 0;
        EnumWindows(enumProc, IntPtr.Zero);       // top-level windows, front to back
        return new List<Win>(found);
    }

    static int ReadTaskbarZ() => taskbarZ;

    static bool EnumProc(IntPtr h, IntPtr _)
    {
        if (h == overlay) { overlayZ = found.Count; return true; }
        if (h == tray) return true;
        if (!IsWindowVisible(h) || IsIconic(h)) return true;

        clsName.Clear();
        GetClassNameW(h, clsName, clsName.Capacity);
        string cls = clsName.ToString();
        if (cls == "Shell_TrayWnd")
        {
            // Remember where the taskbar sits in the z-order: everything found before it is drawn in
            // front of it, and only those windows can hide the snow lying on it. This has to come
            // before the style checks below — the taskbar is itself a WS_EX_TOOLWINDOW, so testing
            // that first threw it away before we ever looked at the class name.
            taskbarZ = found.Count;
            return true;
        }

        long ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return true;                       // tool windows, popups
        if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true;  // other overlays
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true; // other desktops, hidden UWP
        if (GetWindowTextLengthW(h) == 0) return true;
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_SecondaryTrayWnd") return true;

        // The visible frame (GetWindowRect includes invisible resize borders on Windows 10/11)
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rc, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(h, out rc);
        float l = rc.Left / scale, t = rc.Top / scale, r = rc.Right / scale, b = rc.Bottom / scale;
        if (r - l < 80 || b - t < 40) return true;
        found.Add(new Win(h.ToInt64(), l, t, r, b));
        return true;
    }

    // Top edge of the taskbar when it sits at the bottom and is visible; otherwise null
    static float? ReadTaskbarTop()
    {
        IntPtr tb = FindWindowW("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !IsWindowVisible(tb) || !GetWindowRect(tb, out RECT rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= h || rc.Top < bh / 2 || rc.Top >= bh - 4) return null;   // not at the bottom, or auto-hidden
        return rc.Top / scale;
    }

    // MARK: Tray icon and menu

    static void AddTrayIcon()
    {
        const int n = 32;
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = n, biHeight = -n, biPlanes = 1, biBitCount = 32,
            }
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr px, IntPtr.Zero, 0);
        Render.TrayIcon((uint*)px, n);
        IntPtr mask = CreateBitmap(n, n, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        trayIcon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);

        var nid = TrayData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    static NOTIFYICONDATAW TrayData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = tray, uID = 1, uCallbackMessage = WM_TRAY, hIcon = trayIcon,
        szTip = "WinterIsComing ❄", szInfo = "", szInfoTitle = "",
    };

    static void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu(), amount = CreatePopupMenu(), breeze = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_PAUSE,
                    (paused ? "Resume snow" : "Pause snow") + "\tCtrl+Alt+S");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 120 ? MF_CHECKED : 0), (UIntPtr)CMD_LIGHT, "Light");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 260 ? MF_CHECKED : 0), (UIntPtr)CMD_MEDIUM, "Medium");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 500 ? MF_CHECKED : 0), (UIntPtr)CMD_HEAVY, "Heavy");
        AppendMenuW(menu, MF_POPUP, (UIntPtr)(ulong)amount.ToInt64(), "Snow amount");
        for (int i = 0; i < WindSteps.Length; i++)
            AppendMenuW(breeze, MF_STRING | (MathF.Abs(Cfg.Wind - WindSteps[i].V) < 0.01f ? MF_CHECKED : 0),
                        (UIntPtr)(CMD_WIND + i), WindSteps[i].Label);
        AppendMenuW(menu, MF_POPUP, (UIntPtr)(ulong)breeze.ToInt64(), "Wind");
        AppendMenuW(menu, MF_STRING | (Cfg.Lights ? MF_CHECKED : 0), (UIntPtr)CMD_FAIRY, "Fairy lights");
        AppendMenuW(menu, MF_STRING | (Cfg.Snowballs ? MF_CHECKED : 0), (UIntPtr)CMD_BALLS, "Snowballs && snowmen");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_PLOW, "Send the snowplough");
        AppendMenuW(menu, MF_STRING | (world.AutoPlow ? MF_CHECKED : 0), (UIntPtr)CMD_AUTOPLOW, "Snowplough comes by itself");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_CLEAR, "Clear snow");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_QUIT, "Quit");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(tray);   // so the menu closes when you click elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, tray, IntPtr.Zero);
        PostMessageW(tray, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);   // also destroys the submenu

        switch (cmd)
        {
            case CMD_PAUSE: TogglePause(); break;
            case CMD_BALLS:
                Cfg.Snowballs = !Cfg.Snowballs;
                if (!Cfg.Snowballs) SetClickThrough(true);
                break;
            case CMD_PLOW: world.SendPlow(); break;
            case CMD_AUTOPLOW: world.AutoPlow = !world.AutoPlow; break;
            case CMD_FAIRY: Cfg.Lights = !Cfg.Lights; break;
            case CMD_LIGHT: Cfg.TargetFlakes = 120; break;
            case CMD_MEDIUM: Cfg.TargetFlakes = 260; break;
            case CMD_HEAVY: Cfg.TargetFlakes = 500; break;
            case CMD_CLEAR: world.ClearAll(); break;
            case CMD_QUIT: Quit(); break;
            default:
                if (cmd >= CMD_WIND && cmd < CMD_WIND + WindSteps.Length) Cfg.Wind = WindSteps[cmd - CMD_WIND].V;
                break;
        }
    }

    static void Quit()
    {
        KillTimer(overlay, (UIntPtr)1);
        var nid = TrayData();
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        DestroyWindow(overlay);
        DestroyWindow(tray);
        PostQuitMessage(0);
    }
}

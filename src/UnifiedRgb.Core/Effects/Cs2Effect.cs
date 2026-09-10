using UnifiedRgb.Core.Games;

namespace UnifiedRgb.Core.Effects;

/// <summary>Counter-Strike 2 on the rig.
///
/// Two layers. The steady one is a health BAR across the device: the lit
/// fraction is how much health you have, green through amber to red. The loud
/// one is events, because the steady layer alone is a light that sits on green
/// and occasionally changes shade, which is not something you notice while you
/// are playing. Taking a hit, getting a kill and dying each throw a short
/// full-device flash over the top.
///
/// Ordered by urgency and the loudest wins outright rather than blending: a
/// flashbang beats a kill, a kill beats a bomb, a bomb beats your health bar.
/// Blending them would make a color that means nothing.
///
/// Reads the server's published snapshot and nothing else. No allocation per
/// frame, and no work at all when the game is not running.</summary>
public sealed class Cs2Effect : IEffect
{
    /// <summary>Set by the app when it starts the GSI server. Static because an
    /// effect instance is created per channel and they all read one game.</summary>
    public static GsiServer? Server;

    public string Name => "CS2";
    public bool UsesBaseColor => false;
    public bool Bakeable => false;       // driven by the game, nothing to loop
    public bool LiveInput => true;       // a flashbang must not wait for the idle throttle
    public bool HasSpeed => false;       // the game sets the pace

    /// <summary>The bomb's fuse in CS2.</summary>
    const double BombFuseSeconds = 40;

    /// <summary>How long each event flash lasts. Short: these fire often, and
    /// anything that outlasts the next one just reads as noise.</summary>
    const double HurtFlash = 0.35, KillFlash = 0.22, DeathFade = 1.2;

    long _plantedAtTicks;                // when we first saw it planted, 0 when not

    // Events are differences, and GSI only ever sends state, so the previous
    // values have to be kept to spot them.
    int _lastHealth = -1, _lastKills = -1;
    long _hurtAtTicks, _killAtTicks, _diedAtTicks;
    double _hurtAmount;                  // 0..1, how hard the last hit landed

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        var server = Server;
        var state = server?.State ?? GameState.Empty;
        bool live = server is { Connected: true };

        if (!live)
        {
            Forget();
            Fill(buf, Idle(t));
            return;
        }

        // The plant time has to be inferred: GSI says the bomb is planted, not
        // when. First sighting starts the clock, and it only resets when the
        // bomb is no longer planted, so a round restart cannot leave a stale one.
        if (state.Bomb == BombState.Planted)
        {
            if (_plantedAtTicks == 0) _plantedAtTicks = Environment.TickCount64;
        }
        else _plantedAtTicks = 0;

        NoteEvents(state);

        // An event covers the whole device, because half a device flashing red
        // is not something you catch out of the corner of an eye.
        if (EventColor(state, t) is Rgb flash) { Fill(buf, flash); return; }

        // Anything the game itself is painting over your whole screen covers
        // the whole device too.
        if (WholeDeviceColor(state, t) is Rgb whole) { Fill(buf, whole); return; }

        HealthBar(buf, pos, state, t);
    }

    /// <summary>Spot the things worth reacting to by comparing against the last
    /// state we saw. Only while actually playing: a spectated player's health
    /// dropping is not you being shot.</summary>
    void NoteEvents(GameState s)
    {
        if (!s.Playing) { _lastHealth = -1; _lastKills = -1; return; }
        long now = Environment.TickCount64;

        if (_lastHealth >= 0 && s.Health < _lastHealth)
        {
            // A bigger hit flashes harder. 50 damage is about as much as one
            // shot takes, so that is where it maxes out.
            _hurtAmount = Math.Clamp((_lastHealth - s.Health) / 50.0, 0.25, 1.0);
            _hurtAtTicks = now;
            if (s.Health <= 0) _diedAtTicks = now;
        }
        if (_lastKills >= 0 && s.RoundKills > _lastKills) _killAtTicks = now;

        _lastHealth = s.Health;
        _lastKills = s.RoundKills;
    }

    void Forget()
    {
        _plantedAtTicks = 0;
        _lastHealth = -1; _lastKills = -1;
        _hurtAtTicks = _killAtTicks = _diedAtTicks = 0;
    }

    /// <summary>The momentary stuff, or null when nothing is firing. Each fades
    /// out over its own window so a burst of hits reads as a burst rather than
    /// one long smear.</summary>
    Rgb? EventColor(GameState s, double t)
    {
        // Flashed: the screen is white, so the rig is too. Loudest thing there
        // is, and it passes in under a second.
        if (s.Flashed > 20)
            return ColorUtil.Scale(new Rgb(255, 255, 255), Math.Clamp(s.Flashed / 255.0, 0, 1));

        // Died: a hard red that fades down to the dim "you are out" state,
        // rather than snapping straight to it.
        if (Since(_diedAtTicks) is double dead && dead < DeathFade)
            return ColorUtil.Scale(new Rgb(255, 0, 0), 1.0 - dead / DeathFade);

        // Got one. Short and white: the brightest thing that is not a flashbang,
        // and over before it can be mistaken for one.
        if (Since(_killAtTicks) is double kill && kill < KillFlash)
            return ColorUtil.Scale(new Rgb(255, 255, 255), 1.0 - kill / KillFlash);

        // Took a hit. This is the one that makes the rig feel connected to the
        // game: every shot that lands on you is a red spike.
        if (Since(_hurtAtTicks) is double hurt && hurt < HurtFlash)
            return ColorUtil.Scale(new Rgb(255, 30, 0), _hurtAmount * (1.0 - hurt / HurtFlash));

        return null;
    }

    /// <summary>States that own the whole device rather than a bar, or null to
    /// fall through to the health bar.</summary>
    Rgb? WholeDeviceColor(GameState s, double t)
    {
        // Round over: the winner's color, held rather than pulsed, because the
        // round is done and there is nothing left to react to.
        if (s.Phase == RoundPhase.Over && s.WinTeam != Team.None)
            return TeamColor(s.WinTeam);

        // Bomb down: red, tightening as the fuse runs out.
        if (_plantedAtTicks != 0)
        {
            double elapsed = (Environment.TickCount64 - _plantedAtTicks) / 1000.0;
            double left = Math.Clamp(1.0 - elapsed / BombFuseSeconds, 0, 1);
            double rate = 1.5 + 6.0 * (1.0 - left);          // beats/sec, faster as it runs down
            double pulse = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t * rate * Math.PI * 2));
            return ColorUtil.Scale(new Rgb(255, 40, 0), pulse);
        }

        // Dead, or watching someone else: dim and out of the way.
        if (!s.Playing || s.Health <= 0)
            return ColorUtil.Scale(TeamColor(s.Team), 0.10 + 0.04 * Math.Sin(t * 0.9));

        // Freezetime: the team color, so buy time reads as calm.
        if (s.Phase == RoundPhase.FreezeTime)
            return ColorUtil.Scale(TeamColor(s.Team), 0.55);

        // On fire: amber, fast. Molotov damage is fast enough that the health
        // gradient alone lags behind it.
        if (s.Burning > 20)
        {
            double flicker = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(t * 14));
            return ColorUtil.Scale(new Rgb(255, 120, 0), flicker);
        }
        return null;
    }

    /// <summary>Health as a bar along the device: the lit part is what you have
    /// left, in the color it deserves, and the rest is a dark rail so the empty
    /// part still reads as part of the bar rather than as a dead device.
    ///
    /// One flat color used to be the whole effect, which meant a healthy player
    /// saw an unchanging green light. A bar gives it something to say at a
    /// glance, and it degrades gracefully: a one-LED device is just the color.</summary>
    void HealthBar(Rgb[] buf, LedPos[] pos, GameState s, double t)
    {
        double health = Math.Clamp(s.Health / 100.0, 0, 1);
        var lit = ColorUtil.HsvToRgb(120.0 * health, 1.0, 1.0);

        // Low health breathes, so a bad state is visible from the corner of an
        // eye without having to read the bar.
        double brightness = 1.0;
        if (health < 0.35)
        {
            double urgency = 1.0 - health / 0.35;
            brightness = 1.0 - (0.30 * urgency) * (0.5 + 0.5 * Math.Sin(t * (2.0 + 3.0 * urgency) * Math.PI));
        }
        else if (s.AmmoFraction <= 0.2)
        {
            // Low magazine: a shallow dip, well under the low-health pulse so
            // the two are never confused.
            brightness = 0.82 + 0.18 * (0.5 + 0.5 * Math.Sin(t * 6));
        }
        lit = ColorUtil.Scale(lit, brightness);
        var empty = ColorUtil.Scale(lit, 0.08);

        // The bar has to run across THIS device, so the coordinates are
        // normalised to the range's own extent first. Without that, with the
        // desk switched on the positions are desk coordinates: a device sitting
        // in the left third of the desk never reads above 0.36, so its bar was
        // full at any health above that and only moved near zero.
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = 0; i < pos.Length && i < buf.Length; i++)
        {
            if (pos[i].X < minX) minX = pos[i].X;
            if (pos[i].X > maxX) maxX = pos[i].X;
        }
        float spanX = maxX - minX;

        for (int i = 0; i < buf.Length; i++)
        {
            double x = i < pos.Length && spanX > 1e-4f
                ? (pos[i].X - minX) / spanX
                // No geometry worth the name (a single LED, or a device with no
                // horizontal spread): fall back to index order.
                : buf.Length <= 1 ? 0 : i / (double)(buf.Length - 1);
            buf[i] = x <= health ? lit : empty;
        }
    }

    /// <summary>Seconds since a moment, or null if it never happened.</summary>
    static double? Since(long ticks) =>
        ticks == 0 ? null : (Environment.TickCount64 - ticks) / 1000.0;

    /// <summary>Not playing: a slow, dim blue breath. Unmistakably "no game",
    /// rather than a color that could be mistaken for a game state.</summary>
    static Rgb Idle(double t) =>
        ColorUtil.Scale(ColorUtil.HsvToRgb(215, 0.85, 1.0), 0.14 + 0.08 * Math.Sin(t * 0.7));

    static Rgb TeamColor(Team team) => team switch
    {
        Team.CT => new Rgb(90, 150, 255),      // CT blue
        Team.T => new Rgb(255, 190, 60),       // T yellow
        _ => new Rgb(160, 160, 160),
    };

    static void Fill(Rgb[] buf, Rgb c)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

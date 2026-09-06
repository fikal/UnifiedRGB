using UnifiedRgb.Core.Games;

namespace UnifiedRgb.Core.Effects;

/// <summary>Counter-Strike 2 on the rig: health as a colour, the bomb as a
/// countdown, flashbangs as a flash.
///
/// The whole point is glanceable, not decorative, so the rules are ordered by
/// how much they matter and the loudest one wins outright rather than blending:
/// being flashed is more urgent than a bomb, and a bomb is more urgent than
/// your health bar. Blending them would produce a colour that means nothing.
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

    long _plantedAtTicks;                // when we first saw it planted, 0 when not

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        var server = Server;
        var state = server?.State ?? GameState.Empty;
        bool live = server is { Connected: true };

        if (!live)
        {
            _plantedAtTicks = 0;
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

        Fill(buf, ColorFor(state, t));
    }

    /// <summary>One colour for the whole device. Deliberately not a per-LED
    /// pattern: this runs on fans, RAM and a mouse as readily as a keyboard,
    /// and a shape that only reads on one of them is worse than a colour that
    /// reads on all of them.</summary>
    Rgb ColorFor(GameState s, double t)
    {
        // Flashed: the screen is white, so the rig is too. Loudest thing there
        // is, and it passes in under a second.
        if (s.Flashed > 20)
        {
            double amount = Math.Clamp(s.Flashed / 255.0, 0, 1);
            return ColorUtil.Scale(new Rgb(255, 255, 255), amount);
        }

        // Round over: the winner's colour, held rather than pulsed, because the
        // round is done and there is nothing left to react to.
        if (s.Phase == RoundPhase.Over && s.WinTeam != Team.None)
            return TeamColor(s.WinTeam);

        // Bomb down: red, tightening as the fuse runs out. This is the one
        // thing worth interrupting everything else for.
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

        // Freezetime: the team colour, so buy time reads as calm.
        if (s.Phase == RoundPhase.FreezeTime)
            return ColorUtil.Scale(TeamColor(s.Team), 0.55);

        // On fire: amber, fast. Molotov damage is fast enough that the health
        // gradient alone lags behind it.
        if (s.Burning > 20)
        {
            double flicker = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(t * 14));
            return ColorUtil.Scale(new Rgb(255, 120, 0), flicker);
        }

        // The default: health as green through amber to red. Breathing when it
        // is low, so a bad state is visible from the corner of an eye.
        double health = Math.Clamp(s.Health / 100.0, 0, 1);
        double hue = 120.0 * health;
        var color = ColorUtil.HsvToRgb(hue, 1.0, 1.0);

        double brightness = 1.0;
        if (health < 0.35)
        {
            double urgency = 1.0 - health / 0.35;
            brightness = 1.0 - (0.35 * urgency) * (0.5 + 0.5 * Math.Sin(t * (2.0 + 3.0 * urgency) * Math.PI));
        }
        else if (s.AmmoFraction <= 0.2)
        {
            // Low magazine: a shallow dip, well under the low-health pulse so
            // the two are never confused.
            brightness = 0.82 + 0.18 * (0.5 + 0.5 * Math.Sin(t * 6));
        }
        return ColorUtil.Scale(color, brightness);
    }

    /// <summary>Not playing: a slow, dim blue breath. Unmistakably "no game",
    /// rather than a colour that could be mistaken for a game state.</summary>
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

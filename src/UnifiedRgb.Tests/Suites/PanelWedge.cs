using UnifiedRgb.App;
using V = UnifiedRgb.App.PanelWedgeGuard.Verdict;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| When the app restarts the pump LCD's USB device. A restart   |
| is a real hardware action (the panel drops off the bus and   |
| re-enumerates, and every lighting device is rescanned), so   |
| the policy deciding it is pinned here:                       |
|                                                              |
|  - an ordinary loss (frames were landing: unplug, resume)    |
|    never restarts anything;                                  |
|  - the wedge (reopens, then refuses every frame before one   |
|    lands) restarts on the SECOND dead reopen, not the first; |
|  - restarts are spaced and capped per rolling hour - and a   |
|    landed frame does NOT reset that cap, or a panel that     |
|    recovers and re-wedges is restarted every 2 min forever;  |
|  - hitting the cap gives up once (the log says it once) and  |
|    stays given up until a frame lands.                       |
|                                                              |
| The 2026-10-14 incident replayed: lost after streaming, then |
| two dead reopens ~10 s apart -> restart at the second.       |
\*-----------------------------------------------------------*/
static class PanelWedgeSuite
{
    const long S = 1000, Min = 60 * S;

    public static void Run(Harness t)
    {
        const long t0 = 5_000_000;   // an arbitrary monotonic-clock reading

        t.Section("The 10/14 incident");
        {
            var g = new PanelWedgeGuard();
            t.Equal(V.None, g.OnLost(deliveredAny: true, t0), "streaming then lost: an ordinary loss, just look again");
            t.Equal(V.None, g.OnLost(false, t0 + 9 * S), "first dead reopen: could be a fluke, wait");
            t.Equal(V.Restart, g.OnLost(false, t0 + 19 * S), "second dead reopen in a row: wedged, restart");
        }

        t.Section("Unplug and resume never restart");
        {
            var g = new PanelWedgeGuard();
            for (int i = 0; i < 10; i++)
                t.Check(g.OnLost(true, t0 + i * Min) == V.None, $"loss after frames #{i + 1}: no restart");
        }

        t.Section("Spacing, hourly cap and give-up");
        {
            var g = new PanelWedgeGuard();
            long now = t0;
            g.OnLost(false, now);
            t.Equal(V.Restart, g.OnLost(false, now += 10 * S), "restart 1");
            g.OnLost(false, now += 10 * S);
            t.Equal(V.None, g.OnLost(false, now += 10 * S), "too soon after restart 1: hold off");
            t.Equal(V.Restart, g.OnLost(false, now += PanelWedgeGuard.SpacingMs), "spacing elapsed: restart 2");
            g.OnLost(false, now += 10 * S);
            t.Equal(V.Restart, g.OnLost(false, now += PanelWedgeGuard.SpacingMs), "restart 3");
            g.OnLost(false, now += 10 * S);
            t.Equal(V.GiveUp, g.OnLost(false, now += PanelWedgeGuard.SpacingMs), "three this hour: give up, once");
            t.Check(g.Exhausted, "given up: the caller backs its finder off");
            for (int i = 0; i < 5; i++)
                t.Check(g.OnLost(false, now += PanelWedgeGuard.SpacingMs) == V.None, $"after giving up #{i + 1}: silent, no restart");
            t.Check(g.OnLost(false, now += 3 * 60 * Min) == V.None, "still wedged hours later: still given up (no frame landed)");

            // The user power-cycles it; it streams, and later wedges again.
            t.Equal(V.None, g.OnLost(true, now += 24 * 60 * Min), "a landed frame lifts the give-up");
            t.Check(!g.Exhausted, "and the finder goes back to its normal pace");
            g.OnLost(false, now += 10 * S);
            t.Equal(V.Restart, g.OnLost(false, now += 10 * S), "a day later the hour's cap has emptied: restart again");
        }

        t.Section("A panel that recovers and re-wedges is not restarted forever");
        {
            var g = new PanelWedgeGuard();
            long now = t0;
            int restarts = 0;
            for (int cycle = 0; cycle < 6; cycle++)
            {
                g.OnLost(false, now += 10 * S);
                var v = g.OnLost(false, now += 10 * S);
                if (v == V.Restart) restarts++;
                // ...the restart works, frames land for a few minutes, then it wedges again.
                g.OnLost(true, now += 3 * Min);
            }
            t.Equal(PanelWedgeGuard.MaxRestartsPerHour, restarts, "landed frames between wedges do not reset the hourly cap");
        }

        t.Section("Dead reopens must be consecutive");
        {
            var g = new PanelWedgeGuard();
            g.OnLost(false, t0);
            g.OnLost(true, t0 + 10 * S);
            t.Equal(V.None, g.OnLost(false, t0 + 20 * S), "a landed frame between two dead reopens resets the count");
        }
    }
}

namespace UnifiedRgb.App;

/// <summary>When to restart the pump LCD's USB device, decided from the
/// sequence of "panel lost" events alone, so the policy can be driven by the
/// harness without a panel or a USB stack.
///
/// A loss after frames were landing is an ordinary loss (unplug, resume): it
/// clears the dead-reopen count and any give-up. A loss of a controller that
/// never landed a frame is a "dead reopen": the panel is on the bus but
/// refuses everything. Two in a row is a wedged firmware -> Restart.
///
/// Restarts are spaced out and capped at <see cref="MaxRestartsPerHour"/> in
/// any rolling hour. The history is deliberately NOT cleared by a landed
/// frame: a panel that recovers after each restart and wedges again (another
/// program streaming to it) would otherwise be restarted every two minutes
/// forever, and every restart re-enumerates it, which rescans every lighting
/// device. Hitting the cap gives up - once, so the caller can say so - and
/// stays given up until a frame lands. Times are a monotonic millisecond
/// clock (Environment.TickCount64), so a wall-clock step cannot stall it.</summary>
internal sealed class PanelWedgeGuard
{
    public enum Verdict { None, Restart, GiveUp }

    public const int DeadReopensToRestart = 2;
    public const int MaxRestartsPerHour = 3;
    public const long SpacingMs = 2 * 60_000;
    const long WindowMs = 60 * 60_000;

    int _deadReopens;
    readonly Queue<long> _restarts = new();   // when each restart in the last hour was attempted
    long? _lastRestart;
    bool _gaveUp;

    /// <summary>Given up and no frame has landed since: the caller should stop
    /// probing the panel every few seconds.</summary>
    public bool Exhausted => _gaveUp;

    public Verdict OnLost(bool deliveredAny, long nowMs)
    {
        while (_restarts.Count > 0 && nowMs - _restarts.Peek() >= WindowMs) _restarts.Dequeue();
        if (deliveredAny)
        {
            _deadReopens = 0;
            _gaveUp = false;
            return Verdict.None;
        }
        if (++_deadReopens < DeadReopensToRestart || _gaveUp) return Verdict.None;
        if (_restarts.Count >= MaxRestartsPerHour)
        {
            _gaveUp = true;
            return Verdict.GiveUp;
        }
        if (_lastRestart is long last && nowMs - last < SpacingMs) return Verdict.None;
        _deadReopens = 0;
        _restarts.Enqueue(nowMs);
        _lastRestart = nowMs;
        return Verdict.Restart;
    }
}

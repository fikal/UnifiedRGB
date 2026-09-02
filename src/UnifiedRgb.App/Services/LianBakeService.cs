using System.Collections.Concurrent;
using System.Windows.Threading;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------*\
| Lian Li animation baking: the wireless fans loop a     |
| multi-frame animation in HARDWARE. Streaming single    |
| frames over RF is capped at ~8 fps (the lag). Instead  |
| we bake one loop of all the device's bakeable effects  |
| into N frames and upload ONCE; the receiver plays it    |
| smoothly. Live effects (audio/temp/wallpaper) can't be |
| baked, so those fall back to streaming.                |
\*-----------------------------------------------------*/
public sealed class LianBakeService
{
    readonly LightingController _lighting;
    readonly Func<IEnumerable<LianLiWireless>> _devices;
    DispatcherTimer? _timer;
    readonly Dictionary<LianLiWireless, string> _lastSig = new();   // skip redundant re-bakes
    readonly ConcurrentDictionary<LianLiWireless, int> _gen = new();

    public LianBakeService(LightingController lighting, Func<IEnumerable<LianLiWireless>> devices)
    {
        _lighting = lighting;
        _devices = devices;
    }

    /// <summary>Debounced (150 ms): every wireless device re-bakes once the
    /// burst of edits settles.</summary>
    public void Request()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (_, _) => { _timer.Stop(); foreach (var d in _devices()) Rebake(d); };
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Forget the last uploaded signatures so the next Request re-uploads
    /// even an unchanged effect (speed calibration changed; an explicit "All
    /// devices" sync that must re-align the fans to the clock).</summary>
    public void ForgetSignatures() => _lastSig.Clear();

    public void Stop() => _timer?.Stop();

    void Rebake(LianLiWireless dev)
    {
        var engine = _lighting.Engine;
        var channels = engine.ChannelsFor(dev);
        if (channels.Count == 0) { dev.SuppressStreaming = false; Log.Info("LianBake", "no channels - streaming/static"); return; }
        if (!channels.All(c => c.Effect.Bakeable)) { dev.SuppressStreaming = false; Log.Info("LianBake", $"live effect present ({channels.First(c => !c.Effect.Bakeable).Effect.Name}) - streaming"); return; }

        // Skip the re-bake if nothing about the effect actually changed. Phase
        // alignment re-samples the clock every bake, so without this a redundant
        // re-apply (pressing All devices again) would re-upload a phase-shifted
        // copy and visibly reset the fans mid-loop while streamed devices flow on.
        string sig = string.Join("|", channels.OrderBy(c => c.Offset).Select(c =>
            $"{c.Offset}:{c.Count}:{c.Effect.Name}:{c.Speed}:{c.BaseColor}:" +
            (c.Effect is IPaletteEffect pe ? string.Join(",", pe.Palette) : "")));
        if (dev.SuppressStreaming && _lastSig.TryGetValue(dev, out var prev) && prev == sig) return;
        _lastSig[dev] = sig;

        dev.SuppressStreaming = true;
        // Up to 12s so full-hue-turn effects (Rainbow Cycle 9s, Color Cycle 12s)
        // bake a complete loop instead of being clipped into a seam.
        double T = Math.Clamp(channels.Max(c => c.Effect.LoopSeconds(c.Speed)), 1.5, 12.0);
        // Frame count is chosen so the per-frame interval lands in the hardware's
        // honored range (L-Connect never exceeds ~77ms = SpeedType 7 x 11). A
        // large interval (e.g. 64 frames over 9s = 140ms) gets clamped by the
        // receiver and plays too fast, out of sync with the streamed devices. So
        // pick N to target ~60ms/frame: more frames, smaller interval, same loop.
        int N = (int)Math.Clamp(Math.Round(T * 1000.0 / 60.0), 32, 160);
        Log.Info("LianBake", $"baking {channels.Count} channel(s) [{string.Join(",", channels.Select(c => c.Effect.Name))}], T={T:0.0}s, N={N}");
        // Snapshot the statics under the UI thread; the render loop itself runs
        // on a WORKER — 28k+ LED evaluations per device was a visible dispatcher
        // hitch. A generation stamp makes a superseded bake's upload a no-op
        // (a slower older bake can otherwise finish after a newer one).
        var baseFrame = (Rgb[])_lighting.FrameFor(dev).Clone();
        int myGen = _gen.AddOrUpdate(dev, 1, (_, g) => g + 1);
        // Bake from the clock's current phase so the fans' frame 0 is the same
        // point in the cycle the streamed devices are on (red right after an All-
        // devices restart). No look-ahead: on a restart we want them to START on
        // that color, not where the keyboard will have drifted to by upload time.
        double baseTime = engine.ClockSeconds;
        _ = Task.Run(() =>
        {
            var frames = new Rgb[N][];
            // One scratch buffer per channel, reused across all N frames (the
            // old per-frame allocation threw away ~half the bake's memory).
            var bufs = new Rgb[channels.Count][];
            for (int c = 0; c < channels.Count; c++) bufs[c] = new Rgb[channels[c].Count];
            for (int f = 0; f < N; f++)
            {
                var frame = (Rgb[])baseFrame.Clone();
                double time = baseTime + T * f / N;
                for (int c = 0; c < channels.Count; c++)
                {
                    var ch = channels[c];
                    var buf = bufs[c];
                    if (engine.RenderChannelAt(ch, buf, time))
                    {
                        Master.Scale(buf);
                        for (int i = 0; i < ch.Count && ch.Offset + i < frame.Length; i++)
                            frame[ch.Offset + i] = buf[i];
                    }
                }
                frames[f] = frame;
            }
            // No seam crossfade: effects bake exactly one period (their real
            // LoopSeconds), so frame N-1 -> frame 0 is already continuous.
            // The upload is paced (many RF packets, sleeps between them) - run it
            // on the device lane. Keyed so a fresh bake supersedes a still-queued
            // one; the generation check drops an out-of-date bake entirely.
            double frameMs = T * 1000.0 / N;
            _lighting.Applier.Post(LightingController.LaneOf(dev), (dev, "anim"), () =>
            {
                if (_gen.TryGetValue(dev, out int cur) && cur != myGen) return;   // superseded
                dev.UploadAnimation(frames, frameMs);
            });
        });
    }
}

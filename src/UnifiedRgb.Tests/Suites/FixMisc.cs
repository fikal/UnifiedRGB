using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.App;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Regressions for the 2026-10-14 review's servers, the pump    |
| LCD background and a few window-side fixes.                  |
|                                                              |
| The Chroma sections stand up the real REST listener on a     |
| spare port and a real pipe server under a name of their own: |
| the running app holds :54235 and UnifiedRgbChroma, and these |
| must never touch either. The LCD sections run on an STA,     |
| against a controller that holds a design and a fake panel    |
| and is never started, so nothing streams anywhere.           |
\*-----------------------------------------------------------*/
static class FixMiscSuite
{
    public static void Run(Harness t)
    {
        ChromaRestRefusesBrowsers(t);
        SilentPipeClientIsDropped(t);
        NoSourceFileIsBinary(t);
        OnlyThePinnedOpenRgbInstalls(t);

        Exception? failure = null;
        var sta = new Thread(() =>
        {
            try { LcdStillIsBounded(t); LcdFailedBackgroundIsNotRetried(t); LcdGifIsBounded(t); LianLayoutSaveFailure(t); }
            catch (Exception ex) { failure = ex; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (failure != null) throw new Exception("FixMisc STA section failed", failure);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>C2: a web page could drive the lights through :54235 with a
    /// text/plain POST, which a browser sends cross-origin without a preflight.
    /// Browsers always say where they came from (Origin); native Chroma clients
    /// never do, so that header is what is refused.</summary>
    static void ChromaRestRefusesBrowsers(Harness t)
    {
        t.Section("Chroma REST: a request from a web page is refused, a native client is served");
        int port = FreePort();
        if (!ChromaRestServer.Start(port))
        {
            t.Check(false, $"the REST server could not listen on spare port {port}");
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string root = $"http://localhost:{port}/razer/chromasdk";
            static StringContent Static(int colorref) =>
                new($"{{\"effect\":\"CHROMA_STATIC\",\"param\":{{\"color\":{colorref}}}}}", System.Text.Encoding.UTF8, "text/plain");

            // The native shape: no Origin, as HttpClient, libcurl and the SDK's
            // own wrappers send it.
            var init = http.PostAsync(root, new StringContent("{\"title\":\"harness\"}")).GetAwaiter().GetResult();
            string initBody = init.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            t.Equal(200, (int)init.StatusCode, "a native init is answered");
            t.Check(initBody.Contains($":{port}/razer/chromasdk/"), "...with a session uri on the port actually listened on");

            var native = http.PutAsync($"{root}/1/keyboard", Static(0x0000FF)).GetAwaiter().GetResult();
            t.Equal(200, (int)native.StatusCode, "a native keyboard frame is answered");
            t.Equal(new Rgb(255, 0, 0), ChromaFeed.Sample(0.5f, 0.5f), "...and reaches the feed");

            // The browser shape: the same frame with an Origin, as a page's
            // fetch() or form post carries it.
            var page = new HttpRequestMessage(HttpMethod.Post, $"{root}/1/keyboard") { Content = Static(0x00FF00) };
            page.Headers.TryAddWithoutValidation("Origin", "https://example.invalid");
            var refused = http.SendAsync(page).GetAwaiter().GetResult();
            t.Equal(403, (int)refused.StatusCode, "a cross-origin post from a web page gets 403");
            t.Equal(new Rgb(255, 0, 0), ChromaFeed.Sample(0.5f, 0.5f), "...and its colour never reaches the lights (was: applied, body and all)");

            var opaque = new HttpRequestMessage(HttpMethod.Post, root) { Content = new StringContent("{}") };
            opaque.Headers.TryAddWithoutValidation("Origin", "null");   // sandboxed frames and file:// pages
            t.Equal(403, (int)http.SendAsync(opaque).GetAwaiter().GetResult().StatusCode, "an opaque 'null' origin is refused too");

            var preflight = new HttpRequestMessage(HttpMethod.Options, $"{root}/1/keyboard");
            preflight.Headers.TryAddWithoutValidation("Origin", "https://example.invalid");
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "PUT");
            t.Equal(403, (int)http.SendAsync(preflight).GetAwaiter().GetResult().StatusCode, "a CORS preflight is refused");

            var after = http.PutAsync($"{root}/1/keyboard", Static(0xFF0000)).GetAwaiter().GetResult();
            t.Check((int)after.StatusCode == 200 && ChromaFeed.Sample(0.5f, 0.5f) == new Rgb(0, 0, 255),
                "a native client after the refusals is still served");
        }
        finally { ChromaRestServer.Stop(); }
    }

    /// <summary>D8: a pipe client that connects and never writes used to keep
    /// its slot for ever. The reaper disposed its pipe, but a synchronous read
    /// cannot be interrupted that way, so the reader never let go; sixteen such
    /// clients locked every real shim host out.</summary>
    static void SilentPipeClientIsDropped(Harness t)
    {
        t.Section("Chroma pipe: a client that connects and never writes is dropped and its slot freed");
        string name = "UnifiedRgbChromaTest-" + Guid.NewGuid().ToString("N");
        var frames = new System.Collections.Concurrent.ConcurrentQueue<(int Type, Rgb[] Grid, int Rows, int Cols)>();
        const int deadline = 500;
        var server = new ChromaPipeServer(name, deadline, maxClients: 2, (ty, g, r, c) => frames.Enqueue((ty, g, r, c)));
        server.Start();
        var clients = new List<NamedPipeClientStream>();
        NamedPipeClientStream Connect()
        {
            var c = new NamedPipeClientStream(".", name, PipeDirection.Out);
            c.Connect(3000);
            clients.Add(c);
            return c;
        }
        static byte[] Frame(byte type, params Rgb[] cells)
        {
            var b = new byte[5 + cells.Length * 4];
            b[0] = type; b[1] = 1; b[2] = 0; b[3] = (byte)cells.Length; b[4] = 0;
            for (int i = 0; i < cells.Length; i++) { b[5 + i * 4] = cells[i].R; b[6 + i * 4] = cells[i].G; b[7 + i * 4] = cells[i].B; }
            return b;
        }
        static bool Broken(NamedPipeClientStream c)
        {
            try { c.Write(new byte[5]); c.Flush(); return false; }
            catch (IOException) { return true; }
            catch (ObjectDisposedException) { return true; }
        }
        try
        {
            var silent = Connect();
            t.Check(WaitUntil(() => server.ClientCount == 1, 2000), "a silent client takes a slot");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            t.Check(WaitUntil(() => server.ClientCount == 0, deadline * 6), "...and is dropped once the first-frame deadline passes");
            t.Check(sw.ElapsedMilliseconds < deadline * 6, $"...within the deadline, not never ({sw.ElapsedMilliseconds} ms for a {deadline} ms deadline)");
            t.Check(Broken(silent), "its end of the pipe is closed, not left waiting");

            // Both slots taken by silent clients: a real host is refused until
            // they are reaped, and gets in afterwards.
            Connect(); Connect();
            t.Check(WaitUntil(() => server.ClientCount == 2, 2000), "two silent clients fill both slots");
            var refused = Connect();
            t.Check(WaitUntil(() => Broken(refused), 2000), "a third connection is refused while they hold them");
            t.Check(WaitUntil(() => server.ClientCount == 0, deadline * 6), "both silent clients are reaped, freeing their slots");

            var host = Connect();
            host.Write(Frame(1, new Rgb(10, 20, 30), new Rgb(40, 50, 60))); host.Flush();
            t.Check(WaitUntil(() => frames.Count == 1, 2000), "a real host connecting afterwards is served");
            t.Check(frames.TryPeek(out var f) && f is { Type: 1, Rows: 1, Cols: 2 } && f.Grid[1] == new Rgb(40, 50, 60),
                "...and its frame arrives as sent");
            Thread.Sleep(deadline * 3);
            t.Equal(1, server.ClientCount, "a host that has sent a frame keeps its slot however quiet it goes");
            host.Write(Frame(2, new Rgb(1, 2, 3))); host.Flush();
            t.Check(WaitUntil(() => frames.Count == 2, 2000), "...and is still served after the deadline");
        }
        finally
        {
            server.Dispose();
            foreach (var c in clients) c.Dispose();
        }
        t.Check(WaitUntil(() => server.ClientCount == 0, 3000), "disposing the server lets every connection go");
    }

    /// <summary>F10: two raw NUL bytes in MediaService.cs made git treat the
    /// file as binary, so it had no diff and no blame. The escape is "\0".</summary>
    static void NoSourceFileIsBinary(Harness t)
    {
        t.Section("no source file carries a raw NUL byte");
        string? root = RepoRoot();
        if (root == null) { t.Skip("needs the source tree, which a published binary does not carry"); return; }
        var binary = new List<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = path.Substring(root.Length).Replace('\\', '/').TrimStart('/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            if (Array.IndexOf(File.ReadAllBytes(path), (byte)0) >= 0) binary.Add(rel);
        }
        t.Check(binary.Count == 0, binary.Count == 0
            ? "every .cs file is text to git (a NUL would make it binary: no diff, no blame)"
            : $"raw NUL bytes in: {string.Join(", ", binary)} - write \\0 instead");
    }

    /// <summary>C1: the bundled OpenRGB is launched elevated, and was installed
    /// from whatever the pinned URL, or failing that the moving master
    /// artifact, returned. Now the bytes must hash to the pin before anything
    /// on disk changes. Driven through InstallVerified against a scratch root:
    /// no network, and nothing near the real ProgramData folder.</summary>
    static void OnlyThePinnedOpenRgbInstalls(Harness t)
    {
        t.Section("bundled OpenRGB: only the pinned build is installed");
        t.Check(System.Text.RegularExpressions.Regex.IsMatch(OpenRgbManager.BundleSha256, "^[0-9a-f]{64}$"),
            "the pin is a full SHA-256");
        // A release asset, not a CI job artifact: GitLab deletes job artifacts
        // after 30 days, so every master pin expired and left new machines with
        // no way to install the bridge.
        t.Check(OpenRgbManager.PinnedUrl.StartsWith("https://codeberg.org/OpenRGB/OpenRGB/releases/download/", StringComparison.Ordinal),
            "the pin is a permanent OpenRGB release asset, not an expiring CI artifact");
        var m = System.Text.RegularExpressions.Regex.Match(OpenRgbManager.PinnedUrl, @"/OpenRGB_([0-9.]+)_Windows_64_([0-9a-f]+)\.zip$");
        t.Check(m.Success && OpenRgbManager.BundleVersion == $"release-{m.Groups[1].Value}-{m.Groups[2].Value}",
            $"the pinned URL and the version stamp name the same release and commit (a re-pin updates them together): {OpenRgbManager.BundleVersion}");

        byte[] archive;
        using (var ms = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var w = new StreamWriter(zip.CreateEntry("OpenRGB Windows 64-bit/OpenRGB.exe").Open())) w.Write("not really an exe");
                using (var w = new StreamWriter(zip.CreateEntry("OpenRGB Windows 64-bit/platforms/qwindows.dll").Open())) w.Write("dll");
            }
            archive = ms.ToArray();
        }
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)).ToLowerInvariant();

        string root = TempDir();
        try
        {
            // What an earlier install leaves: a build, its stamp, and the
            // detector config that must survive every swap.
            Directory.CreateDirectory(Path.Combine(root, "old build"));
            File.WriteAllText(Path.Combine(root, "old build", "OpenRGB.exe"), "old");
            File.WriteAllText(Path.Combine(root, "bundle-version.txt"), "master-old");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "OpenRGB.json"), "{}");
            string exe = Path.Combine(root, "OpenRGB Windows 64-bit", "OpenRGB.exe");

            bool Refused(byte[] bytes, string pin)
            {
                try { OpenRgbManager.InstallVerified(bytes, pin, root, "master-test"); return false; }
                catch (OpenRgbManager.UnverifiedBuildException) { return true; }
            }
            bool Untouched() =>
                !File.Exists(exe)
                && File.ReadAllText(Path.Combine(root, "old build", "OpenRGB.exe")) == "old"
                && File.ReadAllText(Path.Combine(root, "bundle-version.txt")) == "master-old";

            t.Check(Refused(archive, new string('0', 64)), "an archive that is not the pinned build is refused");
            t.Check(Untouched(), "...before anything is touched: nothing unpacked, the old build and its stamp as they were");

            var tampered = (byte[])archive.Clone();
            tampered[tampered.Length / 2] ^= 0x01;
            t.Check(Refused(tampered, sha), "a single flipped byte in the right-sized archive is refused");
            t.Check(Untouched(), "...and changes nothing either");

            t.Check(!Refused(archive, sha.ToUpperInvariant()), "the pinned bytes install (the hex compare ignores case)");
            t.Check(File.Exists(exe) && File.ReadAllText(exe) == "not really an exe", "...unpacked from the bytes that were hashed");
            t.Check(!Directory.Exists(Path.Combine(root, "old build")), "...in place of the previous build");
            t.Equal("{}", File.ReadAllText(Path.Combine(root, "config", "OpenRGB.json")), "...keeping the detector config");
            t.Equal("master-test", File.ReadAllText(Path.Combine(root, "bundle-version.txt")), "...and only then stamped with the version");
            t.Check(!Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories).Any(), "no archive is left on disk");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }


    /*-------------------- pump LCD background (F3) --------------------*/

    static LcdController Panel() =>
        new(new UnifiedRgb.Core.Devices.ThermalrightLcd(new FakeHid())) { On = false, BackgroundStatEvery = TimeSpan.Zero };

    static string WritePng(string dir, string name, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 40; px[i + 1] = 120; px[i + 2] = 200; px[i + 3] = 255; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        string path = Path.Combine(dir, name);
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    /// <summary>A still used to be held fully decoded for the life of the
    /// design (four bytes a pixel), and a header claiming an absurd size was
    /// decoded anyway, on the UI thread.</summary>
    static void LcdStillIsBounded(Harness t)
    {
        t.Section("pump LCD: a large still is kept scaled, and reports its natural size");
        string dir = TempDir();
        using var lcd = Panel();
        try
        {
            lcd.Design.BackgroundImagePath = WritePng(dir, "big.png", 3000, 2250);
            var bg = lcd.Background;
            t.Check(bg != null, "a 3000x2250 still loads");
            t.Equal(2000, bg?.PixelWidth ?? 0, "...decoded to the designer's largest zoom, not its own 3000 (and not twice the panel, which zoomed blurry)");
            t.Equal(1500, bg?.PixelHeight ?? 0, "...at its own aspect");
            t.Equal((3000.0, 2250.0), lcd.BackgroundSize, "the natural size still comes back for the designer's Fill and Fit");
            t.Check(lcd.BackgroundProblem == null, "no problem reported");

            lcd.Design.BackgroundImagePath = WritePng(dir, "small.png", 100, 80);
            t.Equal(100, lcd.Background?.PixelWidth ?? 0, "a small still is not scaled up");

            lcd.Design.BackgroundImagePath = WritePng(dir, "pano.png", 8000, 200);
            var pano = lcd.Background;
            t.Check(pano != null && (long)pano.PixelWidth * pano.PixelHeight <= 4L * 1024 * 1024 + 8000,
                $"a long panorama is bounded by pixel count ({pano?.PixelWidth}x{pano?.PixelHeight})");

            // A JPEG whose frame header (SOF0) claims 60000x60000: a few hundred
            // bytes on disk, over ten gigabytes decoded.
            var jpeg = new JpegBitmapEncoder();
            jpeg.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, new byte[8 * 8 * 4], 8 * 4)));
            byte[] bomb;
            using (var ms = new MemoryStream()) { jpeg.Save(ms); bomb = ms.ToArray(); }
            int sof = -1;
            for (int i = 0; i + 1 < bomb.Length && sof < 0; i++) if (bomb[i] == 0xFF && bomb[i + 1] == 0xC0) sof = i;
            t.Check(sof > 0, "the encoded JPEG has a baseline frame header to forge");
            if (sof < 0) return;
            // FF C0, length (2), precision (1), then height and width, big-endian.
            bomb[sof + 5] = 60000 >> 8; bomb[sof + 6] = 60000 & 0xFF;
            bomb[sof + 7] = 60000 >> 8; bomb[sof + 8] = 60000 & 0xFF;
            string bombPath = Path.Combine(dir, "bomb.jpg");
            File.WriteAllBytes(bombPath, bomb);
            lcd.Design.BackgroundImagePath = bombPath;
            t.Check(lcd.Background == null, "a still whose header claims 60000x60000 is not shown");
            t.Check(lcd.BackgroundProblem?.Contains("60000x60000") == true,
                $"...because it was refused for its size before decoding, not because a decode failed ({lcd.BackgroundProblem})");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>A failed decode used to be retried every 30 s for as long as
    /// the design pointed at the file: an out-of-memory decode, twice a minute,
    /// on the UI thread, forever.</summary>
    static void LcdFailedBackgroundIsNotRetried(Harness t)
    {
        t.Section("pump LCD: a background that failed is not decoded again until the file changes");
        string dir = TempDir();
        using var lcd = Panel();
        try
        {
            string path = Path.Combine(dir, "broken.png");
            File.WriteAllBytes(path, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6, 7, 8 });
            lcd.Design.BackgroundImagePath = path;
            t.Check(lcd.Background == null, "an unreadable file shows no background");
            int tried = lcd.BackgroundDecodes;
            t.Equal(1, tried, "...after one decode");
            t.Check(lcd.BackgroundProblem != null, "...and says why");
            for (int i = 0; i < 5; i++) _ = lcd.Background;
            t.Equal(tried, lcd.BackgroundDecodes, "asking again does not decode it again");

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            _ = lcd.Background;
            t.Equal(tried + 1, lcd.BackgroundDecodes, "a file that changed is tried again");

            // Re-saved as a good image: it shows, and the problem clears.
            File.Copy(WritePng(dir, "good.png", 64, 48), path, overwrite: true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
            t.Check(lcd.Background != null && lcd.BackgroundProblem == null, "re-saved as a good image, it shows and the problem clears");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    static byte[] EncodeGif(int frames, int w, int h)
    {
        var enc = new GifBitmapEncoder();
        for (int f = 0; f < frames; f++)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 0; px[i + 1] = 0; px[i + 2] = (byte)(f * 2); px[i + 3] = 255; }
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        }
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static int GifFrames(LcdController lcd) =>
        (typeof(LcdController).GetField("_gif", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lcd)
            as System.Collections.ICollection)?.Count ?? 0;

    /// <summary>The GIF decoder decoded every frame, full size, before the
    /// first was used; a long or large animation was the whole thing resident
    /// at once, retried every 30 s after it ran out of memory.</summary>
    static void LcdGifIsBounded(Harness t)
    {
        t.Section("pump LCD: a GIF decodes only what it plays, and an absurd one is refused");
        string dir = TempDir();
        using var lcd = Panel();
        try
        {
            string path = Path.Combine(dir, "long.gif");
            File.WriteAllBytes(path, EncodeGif(110, 16, 12));
            lcd.Design.BackgroundImagePath = path;
            t.Check(lcd.Background != null, "a 110-frame GIF loads");
            t.Equal(96, GifFrames(lcd), "...as its first 96 frames, the rest never decoded");
            t.Equal((16.0, 12.0), lcd.BackgroundSize, "...at its natural size");
            var first = lcd.Background!;
            var px = new byte[4];
            first.CopyPixels(new Int32Rect(first.PixelWidth / 2, first.PixelHeight / 2, 1, 1), px, 4, 0);
            t.Check(px[3] > 200, "the first frame was actually decoded and drawn, not left transparent");

            // The logical screen descriptor forged to 65535x65535.
            var forged = EncodeGif(2, 16, 12);
            forged[6] = forged[7] = forged[8] = forged[9] = 0xFF;
            string forgedPath = Path.Combine(dir, "forged.gif");
            File.WriteAllBytes(forgedPath, forged);
            lcd.Design.BackgroundImagePath = forgedPath;
            t.Check(lcd.Background == null && lcd.BackgroundProblem?.Contains("65535x65535") == true,
                $"a GIF whose header claims 65535x65535 is refused before any frame is decoded ({lcd.BackgroundProblem})");

            // Bigger than an import would accept: refused on its length alone.
            string hugePath = Path.Combine(dir, "huge.gif");
            using (var fs = File.Create(hugePath))
            {
                fs.Write(EncodeGif(1, 16, 12));
                fs.SetLength(25L * 1024 * 1024);
            }
            lcd.Design.BackgroundImagePath = hugePath;
            t.Check(lcd.Background == null && lcd.BackgroundProblem?.Contains("MB") == true,
                $"a 25 MB GIF is refused on its size, not read into memory ({lcd.BackgroundProblem})");
            int tried = lcd.BackgroundDecodes;
            for (int i = 0; i < 3; i++) _ = lcd.Background;
            t.Equal(tried, lcd.BackgroundDecodes, "a refused file is not looked at again while it is unchanged");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /*-------------------- Lian Li fan layout (F12) --------------------*/

    /// <summary>A failed layout save was logged, then the window closed and
    /// rescanned on the OLD order, which looked like the save being ignored.
    /// The write now hands its reason back for the window to show.</summary>
    static void LianLayoutSaveFailure(Harness t)
    {
        t.Section("Lian Li layout: a failed save hands back why");
        string dir = TempDir();
        try
        {
            string good = Path.Combine(dir, "lianli-layout.json");
            t.Check(LianLayoutWindow.WriteLayout(good, new[] { 2, 0, 1 }, new[] { 1 }) == null, "a writable path saves");
            string json = File.ReadAllText(good);
            t.Check(json.Contains("\"order\":[2,0,1]") && json.Contains("\"breaks\":[1]"), "...the order and group breaks as dragged");

            // A file standing where the folder should be: the write cannot happen.
            string blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "");
            string? why = LianLayoutWindow.WriteLayout(Path.Combine(blocker, "lianli-layout.json"), new[] { 0 }, Array.Empty<int>());
            t.Check(!string.IsNullOrWhiteSpace(why), $"a save that cannot be written returns the reason to show ({why})");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

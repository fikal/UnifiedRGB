using UnifiedRgb.Core.Games;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Counter-Strike 2 game-state integration: the payload parser, |
| the config the game reads, and the listener itself.          |
|                                                              |
| The parser gets the long section because CS2 sends only what |
| CHANGED. A partial payload, a field arriving as the wrong    |
| type and a weapon table keyed by unstable slot numbers are   |
| all normal traffic, and every one of them lands on the       |
| render path, so none may throw and none may read as a real   |
| value. The knife case is the one users notice: a weapon with |
| no magazine must not read as out of ammo, or every knife     |
| round pulses a low-ammo warning.                             |
|                                                              |
| The token is the only thing stopping another local program   |
| posting fake states, so a wrong one is a rejection rather    |
| than a warning, and the end-to-end section proves that on a  |
| real socket by forging a post at the live listener.          |
|                                                              |
| That second section stands up an actual listener on a        |
| loopback port and talks to it over HTTP, which is why the    |
| runner puts this suite near the end with the other tests     |
| that open sockets rather than with the pure-logic ones.      |
\*-----------------------------------------------------------*/
static class GamesSuite
{
    public static void Run(Harness t)
    {
        t.Section("CS2 game state (#f8)");
        {
            // A payload shaped like the real thing: keys taken from a maintained CS2
            // GSI library, not guessed.
            string Live(string bomb = "", string phase = "live", int health = 100, int flashed = 0,
                        string activity = "playing", string weapons = "") =>
                "{" +
                "\"provider\":{\"name\":\"Counter-Strike: Global Offensive\",\"appid\":730}," +
                "\"map\":{\"mode\":\"competitive\",\"name\":\"de_mirage\",\"phase\":\"live\",\"round\":7}," +
                "\"round\":{\"phase\":\"" + phase + "\"" + (bomb.Length > 0 ? ",\"bomb\":\"" + bomb + "\"" : "") + "}," +
                "\"player\":{\"steamid\":\"7656119\",\"name\":\"ryan\",\"team\":\"CT\",\"activity\":\"" + activity + "\"," +
                "\"state\":{\"health\":" + health + ",\"armor\":95,\"helmet\":true,\"flashed\":" + flashed +
                ",\"smoked\":0,\"burning\":0,\"money\":3200,\"round_kills\":2,\"round_killhs\":1,\"equip_value\":4700}" +
                (weapons.Length > 0 ? ",\"weapons\":" + weapons : "") + "}," +
                "\"auth\":{\"token\":\"secret123\"}}";

            var s1 = GsiParser.Parse(Live(), "secret123");
            t.Check(s1 != null, "cs2: a live payload parses");
            t.Equal(100, s1!.Health, "cs2: health");
            t.Equal(95, s1.Armor, "cs2: armor");
            t.Equal(3200, s1.Money, "cs2: money");
            t.Equal(2, s1.RoundKills, "cs2: round kills");
            t.Check(s1.Playing, "cs2: playing");
            t.Check(s1.Team == Team.CT, "cs2: team");
            t.Check(s1.Phase == RoundPhase.Live, "cs2: round phase");
            t.Check(s1.Bomb == BombState.None, "cs2: no bomb");

            // The token is the only thing stopping another local program posting fake
            // states, so a wrong one is a rejection, not a warning.
            t.Check(GsiParser.Parse(Live(), "wrong") == null, "cs2: a bad token is rejected");
            t.Check(GsiParser.Parse(Live(), "") != null, "cs2: no expected token means no check");
            t.Check(GsiParser.Parse("not json at all", "secret123") == null, "cs2: junk is rejected");
            t.Check(GsiParser.Parse("[1,2,3]", "") == null, "cs2: a non-object is rejected");

            // Partial payloads are normal: the game sends only what changed.
            var partial = GsiParser.Parse("{\"round\":{\"phase\":\"freezetime\"}}", "");
            t.Check(partial != null, "cs2: a partial payload parses");
            t.Check(partial!.Phase == RoundPhase.FreezeTime, "cs2: with what it does carry");
            t.Equal(0, partial.Health, "cs2: and defaults for what it does not");

            // A field arriving as the wrong type must not throw on the render path.
            var wrongType = GsiParser.Parse("{\"player\":{\"state\":{\"health\":\"100\"}}}", "");
            t.Check(wrongType != null, "cs2: a wrongly typed field does not throw");
            t.Equal(0, wrongType!.Health, "cs2: it just reads as missing");

            t.Check(GsiParser.Parse(Live(bomb: "planted"), "")!.Bomb == BombState.Planted, "cs2: bomb planted");
            t.Check(GsiParser.Parse(Live(bomb: "defused"), "")!.Bomb == BombState.Defused, "cs2: bomb defused");
            t.Check(GsiParser.Parse(Live(bomb: "exploded"), "")!.Bomb == BombState.Exploded, "cs2: bomb exploded");
            t.Check(GsiParser.Parse(Live(phase: "over"), "")!.Phase == RoundPhase.Over, "cs2: round over");
            t.Check(!GsiParser.Parse(Live(activity: "menu"), "")!.Playing, "cs2: in the menu is not playing");

            var dead = GsiParser.Parse(Live(health: 0), "");
            t.Equal(0, dead!.Health, "cs2: a dead player reads zero health");

            // Weapons arrive as an object keyed weapon_0.., and the slot numbers are
            // not stable, so the active one is found by state.
            string rifle = "{\"weapon_0\":{\"name\":\"weapon_knife\",\"type\":\"Knife\",\"state\":\"holstered\"}," +
                           "\"weapon_1\":{\"name\":\"weapon_ak47\",\"type\":\"Rifle\",\"ammo_clip\":7," +
                           "\"ammo_clip_max\":30,\"ammo_reserve\":60,\"state\":\"active\"}}";
            var armed = GsiParser.Parse(Live(weapons: rifle), "");
            t.Equal(7, armed!.AmmoClip, "cs2: the active weapon's clip");
            t.Equal(30, armed.AmmoClipMax, "cs2: and its capacity");
            t.Check(Math.Abs(armed.AmmoFraction - 7.0 / 30.0) < 1e-9, "cs2: ammo fraction");

            // A knife has no magazine: it must not read as out of ammo, or every knife
            // round would sit there pulsing a low-ammo warning.
            string knifeOnly = "{\"weapon_0\":{\"name\":\"weapon_knife\",\"type\":\"Knife\",\"state\":\"active\"}}";
            var knife = GsiParser.Parse(Live(weapons: knifeOnly), "");
            t.Equal(-1, knife!.AmmoClip, "cs2: a knife reports no magazine");
            t.Equal(1.0, knife.AmmoFraction, "cs2: so nothing warns about it");

            var noWeapons = GsiParser.Parse(Live(), "");
            t.Equal(1.0, noWeapons!.AmmoFraction, "cs2: no weapon section is not low ammo");

            t.Equal(255, GsiParser.Parse(Live(flashed: 255), "")!.Flashed, "cs2: flash amount");

            // The config the game reads.
            string cfg = GsiConfig.Build("http://localhost:27180", "abc123");
            t.Check(cfg.Contains("http://localhost:27180"), "cs2 cfg: the uri");
            t.Check(cfg.Contains("abc123"), "cs2 cfg: the token");
            t.Check(cfg.Contains("\"player_state\""), "cs2 cfg: asks for player state");
            t.Check(cfg.Contains("\"player_weapons\""), "cs2 cfg: asks for weapons");
            t.Check(cfg.Contains("\"round\""), "cs2 cfg: asks for the round");
            t.Check(cfg.Contains("\"heartbeat\""), "cs2 cfg: asks for a heartbeat, which is what detects the game closing");
            t.Equal("gamestate_integration_unifiedrgb.cfg", GsiConfig.FileName, "cs2 cfg: the file name the game looks for");

            // libraryfolders.vdf escapes its backslashes.
            t.Equal(@"E:\SteamLibrary", GsiConfig.PathFromVdfLine("\t\t\"path\"\t\t\"E:\\\\SteamLibrary\""),
                  "cs2: a library path is unescaped");
            t.Check(GsiConfig.PathFromVdfLine("\t\t\"apps\"") == null, "cs2: other lines are ignored");
            t.Check(GsiConfig.PathFromVdfLine("") == null, "cs2: an empty line is ignored");

            // Tokens should differ per machine.
            t.Check(GsiServer.NewToken() != GsiServer.NewToken(), "cs2: tokens are not shared between installs");
            t.Equal(16, GsiServer.NewToken().Length, "cs2: token length");
        }

        t.Section("CS2 listener end to end (#f8)");
        {
            // POST to the real listener the way the game does.
            using var gsi = new GsiServer();
            int port = gsi.Start("tok-e2e", preferredPort: 27581);
            t.Check(port > 0, "cs2 e2e: the listener bound a port");
            t.Check(!gsi.Connected, "cs2 e2e: nothing has posted yet");

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string payload = "{\"round\":{\"phase\":\"live\",\"bomb\":\"planted\"}," +
                             "\"player\":{\"activity\":\"playing\",\"team\":\"T\",\"state\":{\"health\":42}}," +
                             "\"auth\":{\"token\":\"tok-e2e\"}}";
            var reply = http.PostAsync($"http://localhost:{port}/",
                            new System.Net.Http.StringContent(payload)).GetAwaiter().GetResult();
            t.Equal(200, (int)reply.StatusCode, "cs2 e2e: the game gets its 200 back");

            bool arrived = false;
            for (int i = 0; i < 200 && !arrived; i++) { arrived = gsi.State.Health == 42; Thread.Sleep(10); }
            t.Check(arrived, "cs2 e2e: the state arrives");
            t.Check(gsi.Connected, "cs2 e2e: and the game counts as connected");
            t.Check(gsi.State.Bomb == BombState.Planted, "cs2 e2e: with the bomb state");
            t.Check(gsi.State.Team == Team.T, "cs2 e2e: and the team");

            // A post signed with the wrong token changes nothing.
            string forged = "{\"player\":{\"state\":{\"health\":1}},\"auth\":{\"token\":\"nope\"}}";
            http.PostAsync($"http://localhost:{port}/", new System.Net.Http.StringContent(forged))
                .GetAwaiter().GetResult();
            Thread.Sleep(150);
            t.Equal(42, gsi.State.Health, "cs2 e2e: a forged post is ignored");

            // Bodies the game would never send, from a sender that might: an
            // oversize body with an honest length, the same without one
            // (chunked), and one that is not JSON. None may change the state,
            // hang the listener, or stop the next honest post from landing.
            string big = "{\"auth\":{\"token\":\"tok-e2e\"},\"pad\":\"" + new string('x', 600 * 1024) + "\"}";
            try { http.PostAsync($"http://localhost:{port}/", new System.Net.Http.StringContent(big)).GetAwaiter().GetResult(); }
            catch (System.Net.Http.HttpRequestException) { /* a refusal mid-upload is an acceptable answer too */ }
            var chunked = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://localhost:{port}/")
            { Content = new System.Net.Http.StringContent(big) };
            chunked.Headers.TransferEncodingChunked = true;
            try { http.SendAsync(chunked).GetAwaiter().GetResult(); }
            catch (System.Net.Http.HttpRequestException) { }
            http.PostAsync($"http://localhost:{port}/", new System.Net.Http.StringContent("{garbage")).GetAwaiter().GetResult();
            Thread.Sleep(150);
            t.Equal(42, gsi.State.Health, "cs2 e2e: oversize, chunked and unreadable posts change nothing");
            t.Check(gsi.Running, "cs2 e2e: and the listener is still up");

            string again = "{\"player\":{\"state\":{\"health\":7}},\"auth\":{\"token\":\"tok-e2e\"}}";
            var ok = http.PostAsync($"http://localhost:{port}/", new System.Net.Http.StringContent(again)).GetAwaiter().GetResult();
            t.Equal(200, (int)ok.StatusCode, "cs2 e2e: the next honest post is answered");
            bool landed = false;
            for (int i = 0; i < 200 && !landed; i++) { landed = gsi.State.Health == 7; Thread.Sleep(10); }
            t.Check(landed, "cs2 e2e: ...and lands");
        }
    }
}

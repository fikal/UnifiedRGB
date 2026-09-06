using System.Text.Json;

namespace UnifiedRgb.Core.Games;

public enum RoundPhase { Unknown, FreezeTime, Live, Over }

public enum BombState { None, Planted, Defused, Exploded }

public enum Team { None, CT, T }

/// <summary>One snapshot of what the game says is happening. A record so the
/// render path reads one reference and never sees a half-updated state.</summary>
public sealed record GameState
{
    public bool Playing { get; init; }          // in a round, alive, not spectating
    public int Health { get; init; }
    public int Armor { get; init; }
    public int Money { get; init; }
    public int Flashed { get; init; }           // 0..255
    public int Burning { get; init; }           // 0..255
    public int Smoked { get; init; }            // 0..255
    public int RoundKills { get; init; }
    public Team Team { get; init; }
    public RoundPhase Phase { get; init; }
    public BombState Bomb { get; init; }
    public Team WinTeam { get; init; }
    public string MapPhase { get; init; } = "";

    /// <summary>The weapon in hand, if any: ammo drives the low-ammo warning.</summary>
    public int AmmoClip { get; init; } = -1;
    public int AmmoClipMax { get; init; } = -1;
    public int AmmoReserve { get; init; } = -1;

    public static readonly GameState Empty = new();

    /// <summary>Fraction of the magazine left, or 1 when the held item has no
    /// magazine (a knife, a grenade) so nothing pulses for it.</summary>
    public double AmmoFraction =>
        AmmoClip >= 0 && AmmoClipMax > 0 ? Math.Clamp((double)AmmoClip / AmmoClipMax, 0, 1) : 1;
}

/// <summary>Turns a Game State Integration POST body into a snapshot.
///
/// Every field is optional. The game sends only the sections the config asked
/// for, sends partial updates as things change, and adds keys between builds,
/// so this reads defensively throughout: a missing or wrongly typed field is
/// the default, never an exception. A parser that throws on the render path
/// would take the lights down over a cosmetic schema change.</summary>
public static class GsiParser
{
    /// <summary>Null when the JSON is unreadable or the token does not match.
    /// An empty expected token means the caller is not checking.</summary>
    public static GameState? Parse(string json, string? expectedToken)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!string.IsNullOrEmpty(expectedToken))
            {
                string? token = Obj(root, "auth") is JsonElement auth ? Str(auth, "token") : null;
                // Ordinal, not culture-aware: this is a shared secret, not text.
                if (!string.Equals(token, expectedToken, StringComparison.Ordinal)) return null;
            }

            var round = Obj(root, "round");
            var map = Obj(root, "map");
            var player = Obj(root, "player");
            var state = player is JsonElement p ? Obj(p, "state") : null;

            var phase = ParsePhase(round is JsonElement r ? Str(r, "phase") : null);
            var bomb = ParseBomb(round is JsonElement r2 ? Str(r2, "bomb") : null);
            int health = state is JsonElement s ? Int(s, "health") : 0;

            // "playing" is the difference between our own state and a spectated
            // player's: watching a dead teammate must not read as being dead.
            // activity is "playing", "menu" or "textinput".
            string activity = player is JsonElement p2 ? Str(p2, "activity") ?? "" : "";
            bool playing = activity == "playing";

            var (clip, clipMax, reserve) = ActiveWeapon(player);

            return new GameState
            {
                Playing = playing,
                Health = health,
                Armor = state is JsonElement s2 ? Int(s2, "armor") : 0,
                Money = state is JsonElement s3 ? Int(s3, "money") : 0,
                Flashed = state is JsonElement s4 ? Int(s4, "flashed") : 0,
                Burning = state is JsonElement s5 ? Int(s5, "burning") : 0,
                Smoked = state is JsonElement s6 ? Int(s6, "smoked") : 0,
                RoundKills = state is JsonElement s7 ? Int(s7, "round_kills") : 0,
                Team = ParseTeam(player is JsonElement p3 ? Str(p3, "team") : null),
                Phase = phase,
                Bomb = bomb,
                WinTeam = ParseTeam(round is JsonElement r3 ? Str(r3, "win_team") : null),
                MapPhase = (map is JsonElement m ? Str(m, "phase") : null) ?? "",
                AmmoClip = clip,
                AmmoClipMax = clipMax,
                AmmoReserve = reserve,
            };
        }
    }

    /// <summary>The weapon whose state is "active". Weapons arrive as an object
    /// keyed weapon_0, weapon_1, ... rather than an array, and the slot numbers
    /// are not stable between updates, so it is found by state and not by
    /// index.</summary>
    static (int Clip, int ClipMax, int Reserve) ActiveWeapon(JsonElement? player)
    {
        if (player is not JsonElement p || Obj(p, "weapons") is not JsonElement weapons)
            return (-1, -1, -1);

        foreach (var slot in weapons.EnumerateObject())
        {
            if (slot.Value.ValueKind != JsonValueKind.Object) continue;
            if (Str(slot.Value, "state") != "active") continue;
            // A knife or grenade has no magazine: report none rather than zero,
            // or every knife round would read as out of ammo.
            if (!slot.Value.TryGetProperty("ammo_clip", out _)) return (-1, -1, -1);
            return (Int(slot.Value, "ammo_clip"), Int(slot.Value, "ammo_clip_max"),
                    Int(slot.Value, "ammo_reserve"));
        }
        return (-1, -1, -1);
    }

    public static RoundPhase ParsePhase(string? s) => s switch
    {
        "freezetime" => RoundPhase.FreezeTime,
        "live" => RoundPhase.Live,
        "over" => RoundPhase.Over,
        _ => RoundPhase.Unknown,
    };

    public static BombState ParseBomb(string? s) => s switch
    {
        "planted" => BombState.Planted,
        "defused" => BombState.Defused,
        "exploded" => BombState.Exploded,
        _ => BombState.None,
    };

    public static Team ParseTeam(string? s) => s switch
    {
        "CT" => Team.CT,
        "T" => Team.T,
        _ => Team.None,
    };

    static JsonElement? Obj(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    static string? Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int i) ? i : 0;
}

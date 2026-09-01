namespace UnifiedRgb.Core.Devices;

/// <summary>Self-contained, dependency-free tinyuz codec for the Lian Li SLV3
/// wireless protocol. The encoder is a real LZ compressor (hash-chain matcher
/// over a 4 KB window) that emits the exact byte/bit sequence the tinyuz
/// decompressor (sisong/tinyuz decompress/tuz_dec.c) pulls from the stream, by
/// simulating the decoder's own consumption order.
///
/// Why compression matters: a baked fan animation is many near-identical frames
/// (frame N ~= frame N-1) plus large flat color runs inside each frame. Real
/// tinyuz collapses that to a few KB - L-Connect never uploads an uncompressed
/// blob. A literals-only encoder instead GROWS the data (~+12%), producing a
/// ~38 KB / 170-packet upload that overflows the receiver's effect buffer, so
/// the fans silently ignore it. Matching frames back-reference each other
/// (dist = ledNum*3) and flat runs back-reference the prior LED (dist = 3,
/// overlap/RLE copy), so multi-frame loops shrink dramatically.
///
/// Format facts (decompress/tuz_types_private.h + tuz_dec.c):
///   header      : dictSize, 4-byte little-endian
///   type bit 1  : literal data byte follows
///   type bit 0  : dict-match / control path
///                 unpack_len(2) = savedLen; if the prior op produced data an
///                 extra bit selects reuse-last-pos vs. full unpack_dict_pos;
///                 dictPos>kBigPosForLen adds 1 to len; dictPos==0 is a control
///                 code (1=literalLine, 2=clipEnd, 3=streamEnd)
///   type bits pack LSB-first into "types" bytes, pulled on demand; data bytes
///   (literals, low byte of dict pos) are pulled inline from the same stream.
/// Round-trip-validated against the Decode port below (see Tests).</summary>
public static class LianLiTinyuz
{
    const int DictSize = 4096;            // -c-4k, matches L-Connect
    const int MinMatch = 3;               // hash covers 3 bytes; 2-byte matches rarely pay
    const int MaxDist = DictSize;         // receiver keeps the last DictSize bytes
    const int MaxChain = 256;             // cap chain walk per position (one-time bake, but bounded)
    const int MinDictMatchLen = 2;        // kMinDictMatchLen
    const int MinLiteralLen = 15;         // kMinLiteralLen
    const int BigPosForLen = (1 << 11) + (1 << 9) + (1 << 7) - 1;   // kBigPosForLen = 2687

    struct Ev { public bool IsBit; public int Bit; public byte Data; }

    // Per-thread scratch, reused across calls. Encode runs on every changed
    // frame (~22/s under a live effect); the old per-call `new int[1<<15]`
    // was a 128 KB Large-Object-Heap allocation per frame (~2.9 MB/s). The
    // hash table is 1<<13 buckets (32 KB, under the LOH threshold) — plenty
    // for a 528-byte frame and fine for multi-frame bakes since MaxChain
    // already caps chain walks. `prev` grows to the largest input seen.
    // Stale `prev` entries are unreachable: chains only traverse positions
    // inserted THIS call, starting from a freshly reset `head`.
    const int HSIZE = 1 << 13, HMASK = HSIZE - 1;
    [ThreadStatic] static int[]? _headCache;
    [ThreadStatic] static int[]? _prevCache;
    [ThreadStatic] static List<Ev>? _evCache;
    [ThreadStatic] static List<byte>? _outCache;

    public static byte[] Encode(byte[] raw)
    {
        int n = raw.Length;
        var ev = _evCache ??= new List<Ev>(n + n / 4 + 16);
        ev.Clear();
        bool haveDataBack = false;

        void Bit(int b) => ev.Add(new Ev { IsBit = true, Bit = b });
        void Data(byte d) => ev.Add(new Ev { IsBit = false, Data = d });

        // Variable-length integer used by both len and far dict-pos fields.
        // Decoder: v=(v<<(k-1))+low(k-1 bits); if top bit set, v++ and continue.
        // Inverse: a bijective base-(1<<(k-1)) code, least-significant chunk last.
        void UnpackLen(int value, int readBit)
        {
            int baseb = 1 << (readBit - 1);
            // Bounded chunk count (log_base(value) <= 32) — stack scratch, the
            // old per-call List was allocated once per emitted match.
            Span<int> vals = stackalloc int[34];
            Span<bool> conts = stackalloc bool[34];
            int count = 0;
            vals[count] = value % baseb; conts[count] = false; count++;
            int rest = value / baseb;
            while (rest > 0)
            {
                int digit = ((rest - 1) % baseb) + 1;   // bijective digit in [1..baseb]
                rest = (rest - 1) / baseb;
                vals[count] = digit - 1; conts[count] = true; count++;
            }
            for (int c = count - 1; c >= 0; c--)         // most-significant chunk first
            {
                for (int b = 0; b < readBit - 1; b++) Bit((vals[c] >> b) & 1);   // value bits, LSB first
                Bit(conts[c] ? 1 : 0);                                          // continue bit
            }
        }

        void DictPos(int pos)
        {
            if (pos < 128) { Data((byte)pos); return; }
            int x = pos - 128;
            Data((byte)((x & 127) | 128));
            UnpackLen(x >> 7, 3);
        }

        void EmitMatch(int dist, int len)
        {
            int savedLen = len - MinDictMatchLen - (dist > BigPosForLen ? 1 : 0);
            Bit(0);                                  // dict/control path
            UnpackLen(savedLen, 2);
            if (haveDataBack) Bit(0);                // decoder reads this bit only after data: 0 = full pos
            DictPos(dist);
            haveDataBack = false;
        }

        void EmitLiteral(byte b)
        {
            Bit(1);
            Data(b);
            haveDataBack = true;
        }

        // ---- LZ pass: hash-chain longest-match search over a DictSize window.
        int[] head = _headCache ??= new int[HSIZE];
        if (_prevCache == null || _prevCache.Length < n) _prevCache = new int[Math.Max(n, 1024)];
        int[] prev = _prevCache;
        Array.Fill(head, -1);
        int Hash(int i) => ((raw[i] << 10) ^ (raw[i + 1] << 5) ^ raw[i + 2]) & HMASK;
        void Insert(int i) { if (i + MinMatch <= n) { int h = Hash(i); prev[i] = head[h]; head[h] = i; } }

        int p = 0;
        while (p < n)
        {
            int bestLen = 0, bestDist = 0;
            if (p + MinMatch <= n)
            {
                int cand = head[Hash(p)], chain = MaxChain, maxLen = n - p;
                while (cand >= 0 && p - cand <= MaxDist && chain-- > 0)
                {
                    int len = 0;
                    // Comparing against raw (not the reconstructed output) is valid
                    // even for overlap/RLE copies: out[..p) == raw[..p), so a match
                    // that reads ahead of p reproduces raw exactly where raw repeats.
                    while (len < maxLen && raw[cand + len] == raw[p + len]) len++;
                    if (len > bestLen) { bestLen = len; bestDist = p - cand; }
                    cand = prev[cand];
                }
            }
            // A far dict pos steals one from len; a 2-byte far match can't encode.
            if (bestLen >= MinMatch && !(bestDist > BigPosForLen && bestLen < 3))
            {
                EmitMatch(bestDist, bestLen);
                int end = p + bestLen;
                for (; p < end; p++) Insert(p);
            }
            else
            {
                EmitLiteral(raw[p]);
                Insert(p);
                p++;
            }
        }

        // Stream-end control: bit 0, unpack_len(2)=3, (data-back bit), dict_pos 0.
        Bit(0);
        UnpackLen(3, 2);
        if (haveDataBack) Bit(0);
        Data(0x00);

        // ---- Serialize events: pack type bits LSB-first into on-demand "types"
        // bytes, emit data bytes inline, exactly mirroring the decoder's reads.
        var outp = _outCache ??= new List<byte>(n + n / 8 + 16);
        outp.Clear();
        outp.Add((byte)(DictSize & 0xFF)); outp.Add((byte)((DictSize >> 8) & 0xFF));
        outp.Add((byte)((DictSize >> 16) & 0xFF)); outp.Add((byte)((DictSize >> 24) & 0xFF));
        int cacheBits = 0;
        for (int i = 0; i < ev.Count; i++)
        {
            if (ev[i].IsBit)
            {
                if (cacheBits == 0)
                {
                    byte types = 0; int placed = 0;
                    for (int j = i; j < ev.Count && placed < 8; j++)
                        if (ev[j].IsBit) { if (ev[j].Bit != 0) types |= (byte)(1 << placed); placed++; }
                    outp.Add(types);
                    cacheBits = placed;
                }
                cacheBits--;
            }
            else outp.Add(ev[i].Data);
        }
        return outp.ToArray();
    }

    /// <summary>Reference decoder - mirrors tuz_dec.c. Used only to round-trip
    /// validate the encoder (the hardware runs the same algorithm), never on the
    /// hot path.</summary>
    public static byte[] Decode(byte[] comp)
    {
        int pos = 4;                       // skip 4-byte dictSize header
        int typesCache = 0, typeCount = 0;
        int ReadBit()
        {
            if (typeCount == 0) { typesCache = comp[pos++]; typeCount = 8; }
            int b = typesCache & 1; typesCache >>= 1; typeCount--; return b;
        }
        int ReadLowBits(int nb) { int r = 0; for (int k = 0; k < nb; k++) r |= ReadBit() << k; return r; }
        byte ReadData() => comp[pos++];
        int UnpackLen(int readBit)
        {
            int half = 1 << (readBit - 1), mask = half - 1, v = 0;
            while (true)
            {
                int c = ReadLowBits(readBit);
                v = (v << (readBit - 1)) + (c & mask);
                if ((c & half) == 0) return v;
                v += 1;
            }
        }
        int UnpackDictPos()
        {
            int r = ReadData();
            if (r >= 128) r = ((r & 127) | (UnpackLen(3) << 7)) + 128;
            return r;
        }

        var outp = new List<byte>(comp.Length * 4);
        bool haveDataBack = false; int dictPosBack = 1;
        while (true)
        {
            if (ReadBit() == 0)
            {
                int savedLen = UnpackLen(2), savedDictPos;
                if (haveDataBack && ReadBit() == 1) savedDictPos = dictPosBack;
                else { savedDictPos = UnpackDictPos(); savedLen += savedDictPos > BigPosForLen ? 1 : 0; }
                haveDataBack = false;
                if (savedDictPos != 0)
                {
                    int len = savedLen + MinDictMatchLen;
                    dictPosBack = savedDictPos;
                    for (int k = 0; k < len; k++) outp.Add(outp[outp.Count - savedDictPos]);
                }
                else if (savedLen == 1)
                {
                    int len = UnpackLen(3) + MinLiteralLen;
                    for (int k = 0; k < len; k++) outp.Add(ReadData());
                    haveDataBack = true;
                }
                else { dictPosBack = 1; typeCount = 0; if (savedLen == 3) break; }   // 2=clipEnd, 3=streamEnd
            }
            else { haveDataBack = true; outp.Add(ReadData()); }
        }
        return outp.ToArray();
    }
}

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace D2RMapEditorLauncher;

// Makes the editor's game data from YOUR Diablo II: Resurrected install (a port of
// tools/extract-game-files.py, same output). None of it is part of the editor: it's Blizzard's
// data, made on each user's PC from their own copy of the game (don't publish it).
//   gamedata.js            the game tables the editor checks maps against
//   game/maps.js           list of the game's preset maps (lvlprest.txt)
//   game/maps/<path>.js    each map (.ds1)
//   game/tiles.js          index of every tile of every .dt1 the level types use
//   game/tiles/<name>.png  the tiles' pictures (one sheet per .dt1, the act's colors)
//   game/hd/<path>.js      each map's HD file (hd/env/preset/<path>.json), gzip-compressed
// The game's packed CASC storage is read with CascLib.dll (Ladislav Zezula, MIT license).
public sealed class Extractor : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] delegate bool OpenStorageFn(string path, uint locale, out IntPtr storage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] delegate bool OpenFileFn(IntPtr storage, string name, uint locale, uint flags, out IntPtr file);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool ReadFileFn(IntPtr file, byte[] buffer, uint size, out uint read);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool CloseFn(IntPtr handle);

    const string Header = "// made by D2R Map Editor.exe from your D2R install; don't publish\n";
    readonly string root, outDir;
    readonly Action<string> log;
    IntPtr lib, storage;
    OpenFileFn openFile;
    ReadFileFn readFile;
    CloseFn closeFile, closeStorage;

    public Extractor(string gameDir, string cascLib, string root, Action<string> log)
    {
        this.root = root;
        outDir = Path.Combine(root, "game");
        this.log = log;
        lib = NativeLibrary.Load(cascLib);
        T Fn<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        var openStorage = Fn<OpenStorageFn>("CascOpenStorage");
        openFile = Fn<OpenFileFn>("CascOpenFile");
        readFile = Fn<ReadFileFn>("CascReadFile");
        closeFile = Fn<CloseFn>("CascCloseFile");
        closeStorage = Fn<CloseFn>("CascCloseStorage");
        // CascLib takes an ANSI path (a wide one fails)
        if (!openStorage(gameDir, 0, out storage)) throw new Exception("Could not open the game's files in " + gameDir + " (pick the Diablo II: Resurrected folder).");
    }

    public void Dispose()
    {
        if (storage != IntPtr.Zero) closeStorage(storage);
        storage = IntPtr.Zero;
    }

    byte[] Read(string path)
    {
        if (!openFile(storage, "data:data/" + path, 0, 0, out var f)) return null;
        try
        {
            var ms = new MemoryStream();
            var buf = new byte[1 << 20];
            while (readFile(f, buf, (uint)buf.Length, out uint n) && n > 0) ms.Write(buf, 0, (int)n);
            return ms.ToArray();
        }
        finally { closeFile(f); }
    }

    static string Norm(string p) => p.Replace('\\', '/').ToLowerInvariant();

    static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    static string Utf8(byte[] b) => Encoding.UTF8.GetString(b).TrimStart('﻿');

    // a .txt table: one dictionary per line (column -> value), like Python's dict(zip(head, cells))
    List<Dictionary<string, string>> Tsv(string name)
    {
        var data = Read("global/excel/" + name) ?? throw new Exception("The game has no " + name + ".");
        var lines = Utf8(data).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var head = lines[0].Split('\t');
        var rows = new List<Dictionary<string, string>>();
        foreach (var l in lines.Skip(1))
        {
            if (l.Length == 0) continue;
            var cells = l.Split('\t');
            var r = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(head.Length, cells.Length); i++) r[head[i]] = cells[i];
            rows.Add(r);
        }
        return rows;
    }

    static string Get(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : null;
    static int Int(string s, int dflt) => string.IsNullOrEmpty(s) ? dflt : int.Parse(s.Trim());

    public void Run(bool withHd = true)
    {
        // ------------------------------------------------------------ game tables (gamedata.js)
        var levelsRows = Tsv("levels.txt").Where(r => !string.IsNullOrEmpty(Get(r, "Id")) && Get(r, "Name") != "Expansion").ToList();
        var lvltypes = Tsv("lvltypes.txt").Where(r => !string.IsNullOrEmpty(Get(r, "Name")) && Get(r, "Name") != "Expansion" && !string.IsNullOrEmpty(Get(r, "Id"))).ToList();
        var lvlprest = Tsv("lvlprest.txt").Where(r => !string.IsNullOrEmpty(Get(r, "Name")) && (Get(r, "Def") ?? "") != "").ToList();
        var monpreset = Enumerable.Range(0, 5).Select(_ => new List<object>()).ToArray();
        foreach (var r in Tsv("monpreset.txt"))
        {
            int a = Int(Get(r, "Act"), 0);
            if (a >= 1 && a <= 5) monpreset[a - 1].Add(Get(r, "Place") ?? "");
        }
        var objpreset = Enumerable.Range(0, 5).Select(_ => new JObj()).ToArray();
        foreach (var r in Tsv("objpreset.txt"))
        {
            int a = Int(Get(r, "Act"), 0);
            if (a >= 1 && a <= 5 && (Get(r, "Index") ?? "") != "") objpreset[a - 1].Set(r["Index"], Get(r, "ObjectClass") ?? "");
        }
        var gamedata = new JObj
        {
            { "monpreset", monpreset },
            { "objpreset", objpreset },
            { "levelTypes", lvltypes.Select(r => new JObj {
                { "id", Int(r["Id"], 0) }, { "name", r["Name"] }, { "act", Int(Get(r, "Act"), 1) },
                { "files", Enumerable.Range(1, 32).Select(i => { var f = Get(r, "File " + i); return (object)(f != "0" && f != null ? f : ""); }).ToList() } }).ToList() },
            { "warps", Tsv("lvlwarp.txt").Where(r => !string.IsNullOrEmpty(Get(r, "Id")) && Get(r, "Name") != "Expansion").Select(r => new JObj {
                { "id", Int(r["Id"], 0) }, { "name", r["Name"] }, { "dir", Get(r, "Direction") ?? "" } }).ToList() },
            { "levels", levelsRows.Select(r => new JObj {
                { "id", Int(r["Id"], 0) }, { "name", r["Name"] }, { "type", Int(Get(r, "LevelType"), 0) }, { "pal", Int(Get(r, "Pal"), 0) }, { "act", Int(Get(r, "Act"), 0) } }).ToList() },
            { "presets", lvlprest.Select(r => {
                var flags = new JObj();
                foreach (var k in new[] { "Populate", "Logicals", "Outdoors", "Animate", "KillEdge", "FillBlanks", "PopPad" }) flags.Add(k, Int(Get(r, k), 0));
                return new JObj { { "name", r["Name"] }, { "level", Int(Get(r, "LevelId"), 0) }, { "mask", Int(Get(r, "Dt1Mask"), 0) }, { "flags", flags },
                    { "files", Enumerable.Range(1, 6).Select(i => Get(r, "File" + i)).Where(f => f != null && f != "" && f != "0").Cast<object>().ToList() } };
            }).ToList() },
        };
        WriteText(Path.Combine(root, "gamedata.js"), Header + "window.GAMEDATA = " + Json.Write(gamedata, false) + ";\n");
        log("Game tables: written.");

        // ------------------------------------------------------------ maps
        var levels = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in levelsRows) levels[r["Id"]] = r;
        var maps = new List<object>();
        var done = new HashSet<string>();
        int n = 0, withHdFiles = 0;
        foreach (var r in lvlprest)
        {
            levels.TryGetValue(Get(r, "LevelId") ?? "0", out var lvl);
            bool useLvl = lvl != null && Get(r, "LevelId") != "0";
            for (int i = 1; i <= 6; i++)
            {
                var f = Get(r, "File" + i) ?? "0";
                if (f == "" || f == "0") continue;
                var key = Norm(f);
                var file2 = Get(r, "File2") ?? "0";
                var entry = new JObj
                {
                    { "name", r["Name"] + (i == 1 && (file2 == "0" || file2 == "") ? "" : " #" + i) }, { "file", key },
                    { "level", useLvl ? lvl["Name"] : "" }, { "type", useLvl ? Int(lvl["LevelType"], 0) : 0 },
                    { "mask", Int(Get(r, "Dt1Mask"), 0) }, { "pal", useLvl ? Int(lvl["Pal"], 0) : -1 },
                };
                if (done.Contains(key)) continue;
                var data = Read("global/tiles/" + f.Replace('\\', '/'));
                if (data == null || data.Length == 0) continue;
                done.Add(key);
                WriteText(Path.Combine(outDir, "maps", key + ".js"), "D2MAPLOAD(" + Json.Str(key) + ", " + Json.Str(Convert.ToBase64String(data)) + ");\n");
                entry.Add("size", data.Length);
                if (data.Length >= 12)
                {
                    entry.Add("w", BitConverter.ToInt32(data, 4) + 1);
                    entry.Add("h", BitConverter.ToInt32(data, 8) + 1);
                }
                if (withHd)
                {
                    var hd = Read("hd/env/preset/" + key.Replace(".ds1", ".json"));
                    if (hd != null && hd.Length > 0)
                    {
                        WriteText(Path.Combine(outDir, "hd", key.Replace(".ds1", ".js")),
                            "D2HDLOADZ(" + Json.Str(key) + ", " + Json.Str(Convert.ToBase64String(Gzip(CompactJson(hd)))) + ");\n");
                        entry.Add("hd", 1);
                        withHdFiles++;
                    }
                }
                maps.Add(entry);
                if (++n % 200 == 0) log($"Maps: {n}…");
            }
        }
        WriteText(Path.Combine(outDir, "maps.js"), Header + "window.GAME_MAPS = " + Json.Write(maps, false) + ";\n");
        log($"Maps: {maps.Count}, with HD files: {withHdFiles}.");

        // ------------------------------------------------------------ tiles
        var dt1s = new Dictionary<string, (string file, int act)>();
        foreach (var r in lvltypes)
        {
            int act = Int(Get(r, "Act"), 1);
            for (int i = 1; i <= 32; i++)
            {
                var f = Get(r, "File " + i) ?? "0";
                if (f != "" && f != "0" && !dt1s.ContainsKey(Norm(f))) dt1s[Norm(f)] = (f, act);
            }
        }
        var index = new JObj();
        Directory.CreateDirectory(Path.Combine(outDir, "tiles"));
        const int SheetW = 1024;
        int total = 0, files = 0;
        var palettes = new Dictionary<int, byte[]>();
        foreach (var key in dt1s.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var (f, act) = dt1s[key];
            var data = Read("global/tiles/" + f.Replace('\\', '/'));
            if (data == null || data.Length == 0) continue;
            var tiles = DecodeDt1(data);
            if (tiles.Count == 0) continue;
            // shelf packing, tallest first (a stable sort, like Python's sorted)
            int x = 0, y = 0, shelf = 0;
            var placed = new List<(int t, int x, int y)>();
            foreach (var t in Enumerable.Range(0, tiles.Count).OrderBy(i => -tiles[i].H))
            {
                int w = tiles[t].W, h = tiles[t].H;
                if (x + w > SheetW) { x = 0; y += shelf; shelf = 0; }
                placed.Add((t, x, y)); x += w; shelf = Math.Max(shelf, h);
            }
            int sheetH = y + shelf;
            var sheet = new byte[SheetW * sheetH];
            var entries = new List<object>();
            foreach (var (t, px, py) in placed)
            {
                var ti = tiles[t];
                for (int row = 0; row < ti.H; row++) Array.Copy(ti.Pix, row * ti.W, sheet, (py + row) * SheetW + px, ti.W);
                entries.Add(new List<object> { ti.Orient, ti.Main, ti.Sub, ti.Rarity, px, py, ti.W, ti.H, ti.X0, ti.Y0, ti.RoofY, ti.Walk });
            }
            var name = key.Replace("/", "_").Replace(".dt1", ".png");
            if (!palettes.TryGetValue(act, out var pal)) palettes[act] = pal = Read($"global/palette/act{act}/pal.dat");
            File.WriteAllBytes(Path.Combine(outDir, "tiles", name), Png(SheetW, sheetH, sheet, pal));
            index.Add(key, new JObj { { "img", "game/tiles/" + name }, { "tiles", entries } });
            total += entries.Count;
            if (++files % 40 == 0) log($"Tile files: {files}…");
        }
        WriteText(Path.Combine(outDir, "tiles.js"), Header + "window.GAME_TILES = " + Json.Write(index, true) + ";\n");
        log($"Tile files: {index.Count}, tiles: {total}.");
    }

    // the HD file made compact (no spaces), as the Python extractor does
    static byte[] CompactJson(byte[] raw)
    {
        using var doc = JsonDocument.Parse(Utf8(raw), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) doc.RootElement.WriteTo(w);
        return ms.ToArray();
    }

    static byte[] Gzip(byte[] data)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    sealed class Tile { public int Orient, Main, Sub, Rarity, RoofY, X0, Y0, W, H, Walk; public byte[] Pix; }
    static readonly int[] IsoX = { 14, 12, 10, 8, 6, 4, 2, 0, 2, 4, 6, 8, 10, 12, 14 };
    static readonly int[] IsoN = { 4, 8, 12, 16, 20, 24, 28, 32, 28, 24, 20, 16, 12, 8, 4 };

    // copies like a Python slice assignment of equal length, clipped to both arrays
    static void Put(byte[] dst, int at, byte[] src, int from, int count)
    {
        count = Math.Min(count, Math.Min(dst.Length - at, src.Length - from));
        if (count > 0 && at >= 0 && from >= 0) Array.Copy(src, from, dst, at, count);
    }

    static List<Tile> DecodeDt1(byte[] d)
    {
        var list = new List<Tile>();
        int n = BitConverter.ToInt32(d, 268), ptr = BitConverter.ToInt32(d, 272);
        for (int t = 0; t < n; t++)
        {
            int o = ptr + t * 96;
            short roofY = BitConverter.ToInt16(d, o + 4);
            int orient = BitConverter.ToInt32(d, o + 20), main = BitConverter.ToInt32(d, o + 24), sub = BitConverter.ToInt32(d, o + 28), rarity = BitConverter.ToInt32(d, o + 32);
            // the 5x5 sub-tiles' flags (bit 0: can't walk there): a 25-bit mask of the blocked ones
            int walk = 0;
            for (int k = 0; k < 25; k++) if ((d[o + 40 + k] & 1) != 0) walk |= 1 << k;
            int bptr = BitConverter.ToInt32(d, o + 72), nblocks = BitConverter.ToInt32(d, o + 80);
            var blocks = new List<(int bx, int by, int fmt, int len, int p)>();
            for (int b = 0; b < nblocks; b++)
            {
                int q = bptr + b * 20;
                blocks.Add((BitConverter.ToInt16(d, q), BitConverter.ToInt16(d, q + 2), BitConverter.ToInt16(d, q + 8), BitConverter.ToInt32(d, q + 10), bptr + BitConverter.ToInt32(d, q + 16)));
            }
            if (blocks.Count == 0) continue;
            int x0 = blocks.Min(b => b.bx), y0 = blocks.Min(b => b.by);
            int x1 = blocks.Max(b => b.bx + 32), y1 = blocks.Max(b => b.by + (b.fmt == 1 ? 15 : 32));
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0 || w > 4000 || h > 4000) continue;
            var pix = new byte[w * h];
            foreach (var (bx, by, fmt, len, p0) in blocks)
            {
                int p = p0;
                if (fmt == 1) // isometric 32x15
                {
                    for (int yy = 0; yy < 15; yy++)
                    {
                        Put(pix, (by - y0 + yy) * w + (bx - x0) + IsoX[yy], d, p, IsoN[yy]);
                        p += IsoN[yy];
                    }
                }
                else // RLE 32x32
                {
                    int x = 0, y = 0, end = p + len;
                    while (p < end && p + 1 < d.Length)
                    {
                        int jump = d[p], count = d[p + 1]; p += 2;
                        if (jump != 0 || count != 0)
                        {
                            x += jump;
                            Put(pix, (by - y0 + y) * w + (bx - x0) + x, d, p, count);
                            p += count; x += count;
                        }
                        else { x = 0; y++; }
                    }
                }
            }
            list.Add(new Tile { Orient = orient, Main = main, Sub = sub, Rarity = rarity, RoofY = roofY, X0 = x0, Y0 = y0, W = w, H = h, Pix = pix, Walk = walk });
        }
        return list;
    }

    // an 8-bit palette PNG; index 0 is transparent; pal.dat is BGR
    static byte[] Png(int w, int h, byte[] pix, byte[] pal)
    {
        var raw = new byte[(w + 1) * h];
        for (int y = 0; y < h; y++) Array.Copy(pix, y * w, raw, y * (w + 1) + 1, w);
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++) { rgb[i * 3] = pal[i * 3 + 2]; rgb[i * 3 + 1] = pal[i * 3 + 1]; rgb[i * 3 + 2] = pal[i * 3]; }
        var trns = new byte[256];
        for (int i = 1; i < 256; i++) trns[i] = 255;
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize)) zs.Write(raw, 0, raw.Length);
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 3;
        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(png, "IHDR", ihdr); Chunk(png, "PLTE", rgb); Chunk(png, "tRNS", trns); Chunk(png, "IDAT", z.ToArray()); Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length);
        var td = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(td, 0); data.CopyTo(td, 4);
        var crc = new byte[4]; BE(crc, 0, Crc32(td));
        s.Write(len); s.Write(td); s.Write(crc);
    }

    static uint[] crcTable;
    static uint Crc32(byte[] data)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; crcTable[i] = c; }
        }
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}

// an ordered JSON object (keys keep their insertion order, like a Python dict)
public sealed class JObj : List<KeyValuePair<string, object>>
{
    public void Add(string k, object v) => Add(new KeyValuePair<string, object>(k, v));
    public void Set(string k, object v)
    {
        int i = FindIndex(p => p.Key == k);
        if (i >= 0) this[i] = new KeyValuePair<string, object>(k, v); else Add(k, v);
    }
}

// JSON text the way Python's json.dumps writes it (ASCII only; ", " ": " or compact)
public static class Json
{
    public static string Write(object v, bool compact) { var sb = new StringBuilder(); W(sb, v, compact); return sb.ToString(); }

    static void W(StringBuilder sb, object v, bool c)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: sb.Append(Str(s)); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case int i: sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case long l: sb.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case JObj o:
                sb.Append('{');
                for (int k = 0; k < o.Count; k++)
                {
                    if (k > 0) sb.Append(c ? "," : ", ");
                    sb.Append(Str(o[k].Key)).Append(c ? ":" : ": ");
                    W(sb, o[k].Value, c);
                }
                sb.Append('}');
                break;
            case System.Collections.IEnumerable e:
                sb.Append('[');
                bool first = true;
                foreach (var x in e) { if (!first) sb.Append(c ? "," : ", "); first = false; W(sb, x, c); }
                sb.Append(']');
                break;
            default: throw new ArgumentException("JSON: " + v.GetType());
        }
    }

    public static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch >= 0x80) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

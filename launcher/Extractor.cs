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

        // ------------------------------------------------------------ unit pictures
        Units(monpreset, objpreset, SheetW);
    }

    // ---------------------------------------------------------------- unit pictures (game/units.png, game/units.js)
    // One standing picture (first frame, direction 0) of every monster and object the editor can place,
    // from the game's classic 2D sprites: a .cof lists the layers (body parts), each part is a .dcc
    // (or the older .dc6) sprite. Same output as tools/extract-game-files.py.
    sealed class Pic { public int L, T, W, H; public byte[] Px; }
    static readonly string[] Parts = { "hd", "tr", "lg", "ra", "la", "rh", "lh", "sh", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8" };
    static readonly string[] Ubers = { "ubermephisto", "uberdiablo", "uberbaal", "uberandariel", "uberduriel", "uberizual", "diabloclone" };

    void Units(List<object>[] monpreset, JObj[] objpreset, int sheetW)
    {
        var monstats = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in Tsv("monstats.txt")) if (!string.IsNullOrEmpty(Get(r, "Id"))) monstats[r["Id"]] = r;
        var monstats2 = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in Tsv("monstats2.txt")) if (!string.IsNullOrEmpty(Get(r, "Id"))) monstats2[r["Id"]] = r;
        var supers = new Dictionary<string, string>();
        foreach (var r in Tsv("superuniques.txt")) if (!string.IsNullOrEmpty(Get(r, "Superunique"))) supers[r["Superunique"]] = Get(r, "Class") ?? "";
        var objects = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in Tsv("objects.txt")) { var c = Get(r, "Class"); if (!string.IsNullOrEmpty(c) && !objects.ContainsKey(c)) objects[c] = r; }

        Pic MonsterPic(string mid)
        {
            if (!monstats.TryGetValue(mid, out var m) || string.IsNullOrEmpty(Get(m, "Code"))) return null;
            var exKey = Get(m, "MonStatsEx"); if (string.IsNullOrEmpty(exKey)) exKey = mid;
            monstats2.TryGetValue(exKey, out var m2); m2 ??= new Dictionary<string, string>();
            List<string> Variants(string part)
            {
                var col = part == "ra" ? "Rav" : part == "la" ? "Lav" : part.ToUpperInvariant() + "v";
                var own = (Get(m2, col) ?? "").Split(',').Select(v => v.Trim().ToLowerInvariant()).Where(v => v != "" && v != "nil").ToList();
                return own.Concat(new[] { "lit", "med", "hvy" }.Where(v => !own.Contains(v))).ToList();
            }
            var bw = Get(m2, "BaseW"); if (string.IsNullOrEmpty(bw)) bw = "hth";
            return UnitPicture("monsters", m["Code"], new[] { "nu", "wl" }, bw.ToLowerInvariant(), Variants);
        }
        Pic ObjectPic(string cls)
        {
            if (!objects.TryGetValue(cls, out var o) || string.IsNullOrEmpty(Get(o, "Token"))) return null;
            return UnitPicture("objects", o["Token"], new[] { "nu", "on", "op" }, "hth", _ => new List<string> { "lit", "med", "hvy" });
        }

        var pics = new List<(string kind, string name, Pic pic)>();
        var monNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var act in monpreset) foreach (var n in act) if (n is string s && s != "") monNames.Add(s);
        foreach (var u in Ubers) monNames.Add(u);
        foreach (var name in monNames)
        {
            var mid = monstats.ContainsKey(name) ? name : supers.TryGetValue(name, out var c) ? c : null;
            var pic = !string.IsNullOrEmpty(mid) ? MonsterPic(mid) : null;
            if (pic != null) pics.Add(("m", name, pic));
        }
        var objNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var act in objpreset) foreach (var kv in act) if (kv.Value is string s && s != "") objNames.Add(s);
        foreach (var name in objNames) { var pic = ObjectPic(name); if (pic != null) pics.Add(("o", name, pic)); }

        // shelf packing into one sheet, tallest first (stable, like Python's sorted)
        int x = 0, y = 0, shelf = 0;
        var placed = new List<(int i, int x, int y)>();
        foreach (var i in Enumerable.Range(0, pics.Count).OrderBy(i => -pics[i].pic.H))
        {
            int w = pics[i].pic.W, h = pics[i].pic.H;
            if (x + w > sheetW) { x = 0; y += shelf; shelf = 0; }
            placed.Add((i, x, y)); x += w; shelf = Math.Max(shelf, h);
        }
        int sheetH = Math.Max(1, y + shelf);
        var sheet = new byte[sheetW * sheetH];
        var mObj = new JObj(); var oObj = new JObj();
        foreach (var (i, px, py) in placed.OrderBy(p => pics[p.i].kind, StringComparer.Ordinal).ThenBy(p => pics[p.i].name, StringComparer.Ordinal))
        {
            var (kind, name, pic) = pics[i];
            for (int row = 0; row < pic.H; row++) Array.Copy(pic.Px, row * pic.W, sheet, (py + row) * sheetW + px, pic.W);
            (kind == "m" ? mObj : oObj).Add(name, new List<object> { px, py, pic.W, pic.H, pic.L, pic.T });
        }
        File.WriteAllBytes(Path.Combine(outDir, "units.png"), Png(sheetW, sheetH, sheet, Read("global/palette/act1/pal.dat")));
        var units = new JObj { { "img", "game/units.png" }, { "m", mObj }, { "o", oObj } };
        WriteText(Path.Combine(outDir, "units.js"), Header + "window.GAME_UNITS = " + Json.Write(units, true) + ";\n");
        log($"Unit pictures: {mObj.Count} monsters, {oObj.Count} objects.");
    }

    Pic UnitPicture(string kind, string tok, string[] modes, string wclass, Func<string, List<string>> variants)
    {
        tok = tok.ToLowerInvariant();
        byte[] cof = null; string mode = null;
        foreach (var md in modes)
        {
            cof = Read($"global/{kind}/{tok}/cof/{tok}{md}{wclass}.cof");
            if (cof != null && cof.Length >= 28) { mode = md; break; }
        }
        if (mode == null) return null;
        int nl = cof[0], fpd = cof[1], nd = cof[2];
        if (nl == 0 || fpd == 0 || nd == 0 || cof.Length < 28 + 9 * nl + fpd + nd * fpd * nl) return null;
        var layers = new Dictionary<int, string>();
        for (int i = 0; i < nl; i++)
        {
            int p = 28 + 9 * i;
            if (!layers.ContainsKey(cof[p]))
            {
                int len = 0; while (len < 4 && cof[p + 5 + len] != 0) len++;
                layers[cof[p]] = System.Text.Encoding.Latin1.GetString(cof, p + 5, len).ToLowerInvariant();
            }
        }
        int prio = 28 + 9 * nl + fpd;
        var pieces = new List<Pic>();
        for (int k = 0; k < nl; k++)
        {
            int comp = cof[prio + k];
            if (!layers.ContainsKey(comp) || comp >= Parts.Length) continue;
            var part = Parts[comp];
            byte[] data = null; bool isDcc = true;
            foreach (var v in variants(part))
            {
                data = Read($"global/{kind}/{tok}/{part}/{tok}{part}{v}{mode}{layers[comp]}.dcc"); isDcc = true;
                if (data == null || data.Length == 0) { data = Read($"global/{kind}/{tok}/{part}/{tok}{part}{v}{mode}{layers[comp]}.dc6"); isDcc = false; }
                if (data != null && data.Length > 0) break;
                data = null;
            }
            if (data == null) continue;
            Pic fr;
            try { fr = isDcc ? DccFrame(data, 0) : Dc6Frame(data, 0); } catch (Exception) { fr = null; }
            if (fr != null && fr.W > 0 && fr.H > 0) pieces.Add(fr);
        }
        if (pieces.Count == 0) return null;
        int L = pieces.Min(p => p.L), T = pieces.Min(p => p.T);
        int W = pieces.Max(p => p.L + p.W) - L, H = pieces.Max(p => p.T + p.H) - T;
        if (W > 1024 || H > 1024) return null;
        var outPx = new byte[W * H];
        foreach (var p in pieces)
            for (int yy = 0; yy < p.H; yy++)
                for (int xx = 0; xx < p.W; xx++)
                {
                    byte v = p.Px[yy * p.W + xx];
                    if (v != 0) outPx[(p.T - T + yy) * W + (p.L - L + xx)] = v;
                }
        return new Pic { L = L, T = T, W = W, H = H, Px = outPx };
    }

    // the older .dc6 format (e.g. Mephisto's parts): frame `want` of a direction
    static Pic Dc6Frame(byte[] d, int direction, int want = 0)
    {
        if (d.Length < 24) return null;
        int version = BitConverter.ToInt32(d, 0), ndirs = BitConverter.ToInt32(d, 16), fpd = BitConverter.ToInt32(d, 20);
        if (version != 6 || ndirs <= 0 || fpd <= 0) return null;
        int ptr = (int)BitConverter.ToUInt32(d, 24 + 4 * (Math.Min(direction, ndirs - 1) * fpd + want));
        int flip = BitConverter.ToInt32(d, ptr), w = BitConverter.ToInt32(d, ptr + 4), h = BitConverter.ToInt32(d, ptr + 8);
        int ox = BitConverter.ToInt32(d, ptr + 12), oy = BitConverter.ToInt32(d, ptr + 16), length = BitConverter.ToInt32(d, ptr + 28);
        if (w <= 0 || h <= 0 || (long)w * h > 4000000) return null;
        var pix = new byte[w * h];
        int p = ptr + 32, end = ptr + 32 + length, x = 0, y = flip != 0 ? 0 : h - 1;
        while (p < end && y >= 0 && y < h)
        {
            int c = d[p++];
            if (c == 0x80) { x = 0; y += flip != 0 ? 1 : -1; }
            else if ((c & 0x80) != 0) x += c & 0x7F;
            else
            {
                int n = Math.Min(c, w - x);
                if (n > 0) Array.Copy(d, p, pix, y * w + x, Math.Min(n, d.Length - p));
                p += c; x += c;
            }
        }
        return new Pic { L = ox, T = oy - h, W = w, H = h, Px = pix };
    }

    // LSB-first bit reader (DCC streams)
    sealed class Bits
    {
        readonly byte[] d; public long Pos;
        public Bits(byte[] data, long pos) { d = data; Pos = pos; }
        public int Get(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) { v |= ((d[Pos >> 3] >> (int)(Pos & 7)) & 1) << i; Pos++; }
            return v;
        }
        public int SGet(int n) { int v = Get(n); return n > 0 && (v & (1 << (n - 1))) != 0 ? v - (1 << n) : v; }
    }
    static readonly int[] Crazy = { 0, 1, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 26, 28, 30, 32 };

    static List<int> DccCells(int start, int size)
    {
        int w = 4 - (start % 4), n;
        if (size - w <= 1) n = 1;
        else { int tmp = size - w - 1; n = 2 + tmp / 4; if (tmp % 4 == 0) n--; }
        if (n == 1) return new List<int> { size };
        var l = new List<int> { w };
        for (int i = 0; i < n - 2; i++) l.Add(4);
        l.Add(size - w - 4 * (n - 2));
        return l;
    }

    // frame `want` of a .dcc direction (after Paul Siramy's DCC format notes)
    static Pic DccFrame(byte[] data, int direction, int want = 0)
    {
        if (data.Length < 15 || data[0] != 0x74) return null;
        int ndirs = data[2], fpd = (int)BitConverter.ToUInt32(data, 3);
        int off = (int)BitConverter.ToUInt32(data, 15 + 4 * Math.Min(direction, ndirs - 1));
        var b = new Bits(data, off * 8L);
        b.Get(32); int flags = b.Get(2);
        int v0 = Crazy[b.Get(4)], wb = Crazy[b.Get(4)], hb = Crazy[b.Get(4)], xb = Crazy[b.Get(4)], yb = Crazy[b.Get(4)], ob = Crazy[b.Get(4)], cb = Crazy[b.Get(4)];
        var fr = new List<int[]>();
        for (int f = 0; f < fpd; f++)
        {
            b.Get(v0); int w = b.Get(wb), h = b.Get(hb), x = b.SGet(xb), y = b.SGet(yb); b.Get(ob); b.Get(cb);
            int up = b.Get(1);
            fr.Add(new[] { x, up != 0 ? y : y - h + 1, w, h });
        }
        if (ob != 0) return null;
        int eqSize = (flags & 2) != 0 ? b.Get(20) : 0, pmSize = b.Get(20), encSize = 0, rawSize = 0;
        if ((flags & 1) != 0) { encSize = b.Get(20); rawSize = b.Get(20); }
        var pal = new List<int>();
        for (int i = 0; i < 256; i++) if (b.Get(1) != 0) pal.Add(i);
        long p = b.Pos;
        var eq = (flags & 2) != 0 ? new Bits(data, p) : null; p += eqSize;
        var pm = new Bits(data, p); p += pmSize;
        var enc = (flags & 1) != 0 ? new Bits(data, p) : null; p += encSize;
        var raw = (flags & 1) != 0 ? new Bits(data, p) : null; p += rawSize;
        var pcd = new Bits(data, p);
        int dl = fr.Min(f => f[0]), dt = fr.Min(f => f[1]);
        int dw = fr.Max(f => f[0] + f[2]) - dl, dh = fr.Max(f => f[1] + f[3]) - dt;
        if (dw <= 0 || dh <= 0 || (long)dw * dh > 4000000) return null;
        int ncw = 1 + (dw - 1) / 4, nch = 1 + (dh - 1) / 4;
        var geo = fr.Select(f => (cw: DccCells(f[0] - dl, f[2]), ch: DccCells(f[1] - dt, f[3]), ocx: (f[0] - dl) / 4, ocy: (f[1] - dt) / 4)).ToList();
        // stage 1: the pixel buffer
        var cellbuf = new int[ncw * nch]; Array.Fill(cellbuf, -1);
        var pval = new List<int[]>(); var pframe = new List<int>(); var pcell = new List<int>();
        for (int fi = 0; fi < geo.Count; fi++)
        {
            var (cws, chs, ocx, ocy) = geo[fi]; int nw = cws.Count;
            for (int cy = 0; cy < chs.Count; cy++)
                for (int cx = 0; cx < nw; cx++)
                {
                    int cur = ocx + cx + (ocy + cy) * ncw, mask;
                    if (cellbuf[cur] >= 0)
                    {
                        if ((eq != null ? eq.Get(1) : 0) != 0) continue;
                        mask = pm.Get(4);
                    }
                    else mask = 0x0F;
                    var stack = new int[4]; int last = 0;
                    int nbits = System.Numerics.BitOperations.PopCount((uint)mask);
                    int et = nbits != 0 && enc != null ? enc.Get(1) : 0, dec = 0;
                    for (int i = 0; i < nbits; i++)
                    {
                        if (et != 0) stack[i] = raw.Get(8);
                        else
                        {
                            stack[i] = last; int dd = pcd.Get(4); stack[i] += dd;
                            while (dd == 15) { dd = pcd.Get(4); stack[i] += dd; }
                        }
                        if (stack[i] == last) { stack[i] = 0; break; }
                        last = stack[i]; dec++;
                    }
                    var val = new int[4]; int ci = dec - 1;
                    for (int i = 0; i < 4; i++)
                    {
                        if ((mask & (1 << i)) != 0) { if (ci >= 0) { val[i] = stack[ci]; ci--; } }
                        else val[i] = pval[cellbuf[cur]][i];
                    }
                    pval.Add(val); pframe.Add(fi); pcell.Add(cx + cy * nw);
                    cellbuf[cur] = pval.Count - 1;
                }
        }
        foreach (var v in pval) for (int i = 0; i < 4; i++) v[i] = v[i] < pal.Count ? pal[v[i]] : 0;
        // stage 2: the frames, drawn one after another into the direction's bitmap
        var bmp = new byte[dw * dh];
        var lastcell = new int[ncw * nch][];
        int pi = 0;
        for (int fi = 0; fi < geo.Count; fi++)
        {
            var (cws, chs, ocx, ocy) = geo[fi]; int nw = cws.Count;
            int y0 = fr[fi][1] - dt;
            for (int cy = 0; cy < chs.Count; cy++)
            {
                int h = chs[cy], x0 = fr[fi][0] - dl;
                for (int cx = 0; cx < nw; cx++)
                {
                    int w = cws[cx], dc = ocx + cx + (ocy + cy) * ncw;
                    bool mine = pi < pval.Count && pframe[pi] == fi && pcell[pi] == cx + cy * nw;
                    if (!mine)
                    {
                        var lc = lastcell[dc];
                        if (lc == null || lc[0] != w || lc[1] != h)
                        {
                            for (int yy = 0; yy < h; yy++) Array.Clear(bmp, (y0 + yy) * dw + x0, w);
                        }
                        else if (lc[2] != x0 || lc[3] != y0)
                        {
                            var rows = new byte[h][];
                            for (int yy = 0; yy < h; yy++) { rows[yy] = new byte[w]; Array.Copy(bmp, (lc[3] + yy) * dw + lc[2], rows[yy], 0, w); }
                            for (int yy = 0; yy < h; yy++) Array.Copy(rows[yy], 0, bmp, (y0 + yy) * dw + x0, w);
                        }
                    }
                    else
                    {
                        var v = pval[pi];
                        if (v[0] == v[1])
                        {
                            for (int yy = 0; yy < h; yy++) Array.Fill(bmp, (byte)v[0], (y0 + yy) * dw + x0, w);
                        }
                        else
                        {
                            int nb = v[1] == v[2] ? 1 : 2;
                            for (int yy = 0; yy < h; yy++)
                            {
                                int row = (y0 + yy) * dw + x0;
                                for (int xx = 0; xx < w; xx++) bmp[row + xx] = (byte)v[pcd.Get(nb)];
                            }
                        }
                        pi++;
                    }
                    lastcell[dc] = new[] { w, h, x0, y0 };
                    x0 += w;
                }
                y0 += h;
            }
            if (fi == want)
            {
                int l = fr[fi][0], t = fr[fi][1], fw = fr[fi][2], fh = fr[fi][3], ox = l - dl, oy = t - dt;
                var outPx = new byte[fw * fh];
                for (int yy = 0; yy < fh; yy++) Array.Copy(bmp, (oy + yy) * dw + ox, outPx, yy * fw, fw);
                return new Pic { L = l, T = t, W = fw, H = fh, Px = outPx };
            }
        }
        return null;
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

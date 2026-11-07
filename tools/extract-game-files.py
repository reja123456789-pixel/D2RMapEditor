# python tools\extract-game-files.py ["<D2R folder>"] [--casclib <CascLib.dll>] [--no-hd]
#
# Reads the game's own files from YOUR Diablo II: Resurrected install and writes what the
# editor needs, next to index.html. None of it is part of the editor: it's Blizzard's data,
# made on each user's PC from their own copy of the game (don't publish it).
#   gamedata.js               the game tables the editor checks maps against (monpreset,
#                             objpreset, lvltypes, lvlprest, levels, lvlwarp)
#   game/maps.js              list of the game's preset maps (lvlprest.txt)
#   game/maps/<path>.js       each map (.ds1)
#   game/tiles.js             index of every tile of every .dt1 the level types use
#   game/tiles/<name>.png     the tiles' pictures (one sheet per .dt1, the act's colors)
#   game/hd/<path>.js         each map's HD file (hd/env/preset/<path>.json), gzip-compressed
#                             (skipped with --no-hd)
# Also:  --hd <map path>      save one map's HD file as plain .json (game/hd/<path>.json)
#
# The CASC storage is read with CascLib.dll (Ladislav Zezula's CascLib, MIT license). D2RMM ships
# it in its tools folder; it's searched for in the D2R folder's subfolders and next to this
# script, or give its path with --casclib.
import ctypes, sys, os, struct, zlib, json, base64, glob, gzip
from ctypes import wintypes as W

args = sys.argv[1:]
def opt(name):
    return args[args.index(name) + 1] if name in args else None
GAME = next((a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or not args[i - 1].startswith("--"))),
            r"C:\Program Files (x86)\Diablo II Resurrected")
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
OUT = os.path.join(ROOT, "game")

def find_casclib():
    if opt("--casclib"):
        return opt("--casclib")
    for pattern in (os.path.join(HERE, "CascLib.dll"), os.path.join(GAME, "*", "tools", "CascLib.dll"),
                    os.path.join(GAME, "*", "CascLib.dll"), os.path.join(GAME, "CascLib.dll")):
        hits = glob.glob(pattern)
        if hits:
            return hits[0]
    sys.exit("CascLib.dll not found: put it next to this script, or give --casclib <path> (D2RMM has one in its tools folder)")

dll = ctypes.WinDLL(find_casclib())
for fn in ("CascOpenStorage", "CascOpenFile", "CascReadFile", "CascCloseFile"):
    getattr(dll, fn).restype = ctypes.c_bool
storage = ctypes.c_void_p()
if not dll.CascOpenStorage(ctypes.c_char_p(GAME.encode("mbcs")), 0, ctypes.byref(storage)):
    sys.exit("could not open the game's files in " + GAME + " (give the D2R install folder)")

def read(path):
    f = ctypes.c_void_p()
    if not dll.CascOpenFile(storage, ctypes.c_char_p(("data:data/" + path).encode()), 0, 0, ctypes.byref(f)):
        return None
    out = bytearray(); buf = ctypes.create_string_buffer(1 << 20); n = W.DWORD()
    while dll.CascReadFile(f, buf, len(buf), ctypes.byref(n)) and n.value:
        out += buf.raw[:n.value]
    dll.CascCloseFile(f)
    return bytes(out)

def norm(p):
    return p.replace("\\", "/").lower()

def write_js(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)

# ---------------------------------------------------------------- one HD file as .json
if opt("--hd"):
    mapfile = norm(opt("--hd"))
    data = read("hd/env/preset/" + mapfile.replace(".ds1", ".json"))
    if not data:
        sys.exit("the game has no HD file for " + mapfile)
    out = os.path.join(OUT, "hd", mapfile.replace(".ds1", ".json"))
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        f.write(data)
    print("wrote", os.path.abspath(out))
    sys.exit(0)

def tsv(name):
    lines = read("global/excel/" + name).decode("utf-8", "replace").lstrip("\ufeff").splitlines()
    head = lines[0].split("\t")
    return [dict(zip(head, l.split("\t"))) for l in lines[1:] if l]

# ---------------------------------------------------------------- game tables (gamedata.js)
levels_rows = [r for r in tsv("levels.txt") if r.get("Id") not in (None, "") and r.get("Name") != "Expansion"]
lvltypes = [r for r in tsv("lvltypes.txt") if r.get("Name") and r.get("Name") != "Expansion" and r.get("Id") not in (None, "")]
lvlprest = [r for r in tsv("lvlprest.txt") if r.get("Name") and r.get("Def", "") != ""]
monpreset = [[], [], [], [], []]
for r in tsv("monpreset.txt"):
    a = int(r.get("Act") or 0)
    if 1 <= a <= 5:
        monpreset[a - 1].append(r.get("Place", ""))
objpreset = [{}, {}, {}, {}, {}]
for r in tsv("objpreset.txt"):
    a = int(r.get("Act") or 0)
    if 1 <= a <= 5 and r.get("Index", "") != "":
        objpreset[a - 1][r["Index"]] = r.get("ObjectClass", "")
gamedata = {
    "monpreset": monpreset, "objpreset": objpreset,
    "levelTypes": [{"id": int(r["Id"]), "name": r["Name"], "act": int(r.get("Act") or 1),
                    "files": [(r.get("File %d" % i) or "") if r.get("File %d" % i) not in ("0", None) else "" for i in range(1, 33)]} for r in lvltypes],
    "warps": [{"id": int(r["Id"]), "name": r["Name"], "dir": r.get("Direction", "")} for r in tsv("lvlwarp.txt") if r.get("Id") not in (None, "") and r.get("Name") != "Expansion"],
    "levels": [{"id": int(r["Id"]), "name": r["Name"], "type": int(r.get("LevelType") or 0), "pal": int(r.get("Pal") or 0), "act": int(r.get("Act") or 0)} for r in levels_rows],
    "presets": [{"name": r["Name"], "level": int(r.get("LevelId") or 0), "mask": int(r.get("Dt1Mask") or 0),
                 # how the game builds the room: Logicals = use the .ds1 cells' own blocked marks (lava)
                 "flags": {k: int(r.get(k) or 0) for k in ("Populate", "Logicals", "Outdoors", "Animate", "KillEdge", "FillBlanks", "PopPad")},
                 "files": [r["File%d" % i] for i in range(1, 7) if r.get("File%d" % i) not in (None, "", "0")]} for r in lvlprest],
}
write_js(os.path.join(ROOT, "gamedata.js"), "// made by tools/extract-game-files.py from your D2R install; don't publish\nwindow.GAMEDATA = " + json.dumps(gamedata) + ";\n")
print("game tables: written")

# ---------------------------------------------------------------- maps
levels = {r["Id"]: r for r in levels_rows}
maps, done = [], set()
for r in lvlprest:
    lvl = levels.get(r.get("LevelId", "0"))
    for i in range(1, 7):
        f = r.get("File%d" % i, "0")
        if not f or f == "0":
            continue
        key = norm(f)
        entry = {"name": r["Name"] + ("" if i == 1 and r.get("File2", "0") in ("0", "") else " #%d" % i), "file": key,
                 "level": lvl["Name"] if lvl and r.get("LevelId") != "0" else "",
                 "type": int(lvl["LevelType"]) if lvl and r.get("LevelId") != "0" else 0,
                 "mask": int(r.get("Dt1Mask") or 0), "pal": int(lvl["Pal"]) if lvl and r.get("LevelId") != "0" else -1}
        if key in done:
            continue
        data = read("global/tiles/" + f.replace("\\", "/"))
        if not data:
            continue
        done.add(key)
        write_js(os.path.join(OUT, "maps", key + ".js"), "D2MAPLOAD(%s, %s);\n" % (json.dumps(key), json.dumps(base64.b64encode(data).decode())))
        entry["size"] = len(data)
        if len(data) >= 12:  # the map's width and height (header: version, width-1, height-1)
            entry["w"] = struct.unpack_from("<i", data, 4)[0] + 1
            entry["h"] = struct.unpack_from("<i", data, 8)[0] + 1
        # the HD file (gzip + base64; the editor unpacks it)
        if "--no-hd" not in args:
            hd = read("hd/env/preset/" + key.replace(".ds1", ".json"))
            if hd:
                compact = json.dumps(json.loads(hd.decode("utf-8", "replace").lstrip("\ufeff")), separators=(",", ":")).encode()
                write_js(os.path.join(OUT, "hd", key.replace(".ds1", ".js")),
                         "D2HDLOADZ(%s, %s);\n" % (json.dumps(key), json.dumps(base64.b64encode(gzip.compress(compact, 9)).decode())))
                entry["hd"] = 1
        maps.append(entry)
write_js(os.path.join(OUT, "maps.js"), "// made by tools/extract-game-files.py from your D2R install; don't publish\nwindow.GAME_MAPS = " + json.dumps(maps) + ";\n")
print("maps:", len(maps), "with HD files:", sum(1 for m in maps if m.get("hd")))

# ---------------------------------------------------------------- tiles
ISO_X = [14, 12, 10, 8, 6, 4, 2, 0, 2, 4, 6, 8, 10, 12, 14]
ISO_N = [4, 8, 12, 16, 20, 24, 28, 32, 28, 24, 20, 16, 12, 8, 4]

def decode_dt1(data):
    """yields (orientation, main, sub, rarity, roofY, ox, oy, w, h, pixels[h][w]) per tile"""
    n, ptr = struct.unpack_from("<ii", data, 268)
    for t in range(n):
        o = ptr + t * 96
        direction, roof_y, sound, animated, size_y, size_x, z1, orient, main, sub, rarity = struct.unpack_from("<ihBBiiiiiii", data, o)
        # the 5x5 sub-tiles' flags (bit 0: can't walk there): a 25-bit mask of the blocked ones
        walk = 0
        for k, fl in enumerate(data[o + 40:o + 65]):
            if fl & 1:
                walk |= 1 << k
        bptr, blen, nblocks = struct.unpack_from("<iii", data, o + 72)
        blocks = []
        for b in range(nblocks):
            bx, by, _, gx, gy, fmt, length, _, off = struct.unpack_from("<hhhBBhihi", data, bptr + b * 20)
            blocks.append((bx, by, fmt, length, bptr + off))
        if not blocks:
            continue
        x0 = min(b[0] for b in blocks); y0 = min(b[1] for b in blocks)
        x1 = max(b[0] + 32 for b in blocks); y1 = max(b[1] + (15 if b[2] == 1 else 32) for b in blocks)
        w, h = x1 - x0, y1 - y0
        if w <= 0 or h <= 0 or w > 4000 or h > 4000:
            continue
        pix = bytearray(w * h)
        for bx, by, fmt, length, p in blocks:
            if fmt == 1:  # isometric 32x15
                for y in range(15):
                    row = (by - y0 + y) * w + (bx - x0) + ISO_X[y]
                    pix[row:row + ISO_N[y]] = data[p:p + ISO_N[y]]
                    p += ISO_N[y]
            else:  # RLE 32x32
                x = y = 0; end = p + length
                while p < end:
                    jump, count = data[p], data[p + 1]; p += 2
                    if jump or count:
                        x += jump
                        row = (by - y0 + y) * w + (bx - x0) + x
                        pix[row:row + count] = data[p:p + count]
                        p += count; x += count
                    else:
                        x = 0; y += 1
        yield orient, main, sub, rarity, roof_y, x0, y0, w, h, pix, walk

def png(path, w, h, pix, palette):
    raw = b"".join(b"\x00" + bytes(pix[y * w:(y + 1) * w]) for y in range(h))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    rgb = b"".join(bytes((palette[i * 3 + 2], palette[i * 3 + 1], palette[i * 3])) for i in range(256))  # pal.dat is BGR
    trns = b"\x00" + b"\xff" * 255  # index 0 is transparent
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 3, 0, 0, 0)) +
                chunk(b"PLTE", rgb) + chunk(b"tRNS", trns) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))

palettes = {}
def palette(act):
    if act not in palettes:
        palettes[act] = read("global/palette/act%d/pal.dat" % act)
    return palettes[act]

dt1s = {}
for r in lvltypes:
    act = int(r.get("Act") or 1)
    for i in range(1, 33):
        f = r.get("File %d" % i, "0")
        if f and f != "0":
            dt1s.setdefault(norm(f), (f, act))

index = {}
os.makedirs(os.path.join(OUT, "tiles"), exist_ok=True)
SHEET_W = 1024
total = 0
for key, (f, act) in sorted(dt1s.items()):
    data = read("global/tiles/" + f.replace("\\", "/"))
    if not data:
        continue
    tiles = list(decode_dt1(data))
    if not tiles:
        continue
    # shelf packing
    x = y = shelf = 0
    placed = []
    for t in sorted(range(len(tiles)), key=lambda i: -tiles[i][8]):
        w, h = tiles[t][7], tiles[t][8]
        if x + w > SHEET_W:
            x, y, shelf = 0, y + shelf, 0
        placed.append((t, x, y)); x += w; shelf = max(shelf, h)
    sheet_h = y + shelf
    sheet = bytearray(SHEET_W * sheet_h)
    entries = []
    for t, px, py in placed:
        orient, main, sub, rarity, roof_y, ox, oy, w, h, pix, walk = tiles[t]
        for row in range(h):
            sheet[(py + row) * SHEET_W + px:(py + row) * SHEET_W + px + w] = pix[row * w:(row + 1) * w]
        entries.append([orient, main, sub, rarity, px, py, w, h, ox, oy, roof_y, walk])
    name = key.replace("/", "_").replace(".dt1", ".png")
    png(os.path.join(OUT, "tiles", name), SHEET_W, sheet_h, sheet, palette(act))
    index[key] = {"img": "game/tiles/" + name, "tiles": entries}
    total += len(entries)
write_js(os.path.join(OUT, "tiles.js"), "// made by tools/extract-game-files.py from your D2R install; don't publish\nwindow.GAME_TILES = " + json.dumps(index, separators=(",", ":")) + ";\n")
print("tile files:", len(index), "tiles:", total)
print("done: open index.html")

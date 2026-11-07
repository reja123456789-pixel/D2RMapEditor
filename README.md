# D2R Map Editor

A map editor for Diablo II: Resurrected preset maps (`.ds1`) and their HD files (`.json`). It runs in the browser from your disk: no install, no server.

You make maps with it; a mod (yours, or anyone's) puts them in the game.

*A fan-made tool, not for profit. Not affiliated with or endorsed by Blizzard Entertainment.*

## Start
Double-click **`Start Map Editor.cmd`**. The first time it finds your D2R folder (or asks for it), makes the game data from your install (about a minute, see below), then opens the editor in Chrome or Edge. Afterwards it just opens the editor. It also redoes the game data by itself when an editor update needs more of it.

After a Diablo II: Resurrected patch, double-click **`Update game data.cmd`**.

In the editor, **📖 Manual** (top right, or F1) explains the steps from getting a map to putting it in the game with D2RMM, including a ready-to-copy example `mod.js`.

## Setup (what the start does; by hand if you prefer)
You need:
- Diablo II: Resurrected,
- Python 3 for Windows,
- `CascLib.dll`, the library that reads the game's file storage. D2RMM ships one in its `tools` folder, which the extractor finds by itself if D2RMM is inside the D2R folder. Otherwise put `CascLib.dll` next to `tools\extract-game-files.py`, or pass `--casclib <path>`.

Then run, from the editor's folder:

```
python tools\extract-game-files.py "C:\Program Files (x86)\Diablo II Resurrected"
```

It takes under a minute. It reads the game's own files from **your** install and writes, next to `index.html`:
- `gamedata.js`: the game tables the editor checks maps against,
- `game\` (~150 MB): the game's 2,044 preset maps, their HD files (compressed), and the tile pictures.

Then open `index.html` in Chrome or Edge.

## HD-safe: what the editor lets you do
The editor only allows changes that look right in D2R's HD graphics. Legacy graphics aren't considered; Reign of the Warlock has none. How HD draws a map:
- **The `.ds1` is gameplay:** where you can walk, monsters, objects, exits.
- **The look comes from the map's HD file** (`hd\env\preset\<path>.json`):
  - a **3D floor model** per room (`terrain_lod0.model`, Granny GR2 format) with the biome's textures, listed as a terrain entry,
  - 3D **pieces** for walls, pillars, decorations and effects.
- **`.ds1` floor tiles don't change the HD floor** in dungeons. Only outdoor road tiles have texture masks (`hd\env\tilemasks`).
- **No public tool can write a floor model.** Community tools can read GR2 but not write it. So floors come from the game's own rooms and maps, never from painted tiles.

So:

| | In HD |
|---|---|
| A game map, or one game room, with its own HD file | ✅ |
| Monsters, objects, spawn points, exits (+ red portal effect), act list | ✅ gameplay only; the HD look is unchanged |
| Painting or erasing floor/wall tiles, Resize, New blank map | 🔒 locked: they'd change collision but not the look |
| **Removing walls + floor stamps** (opening up an area) | ✅ how a published mod opened up a Travincal map (12 stamps of a Travincal room's floor over the removed council building); 🧪 not yet tested in game with this editor |
| Maps joined from rooms of the **River of Flame / Hell caverns** (lava) or the **pygmy jungle clearing** | ✅ tested in game (a 5×5 Hell cavern map, a jungle clearing map): every room keeps its own floor model as a terrain entry |
| The **Chaos Sanctuary** (Act 4): its 6 rooms joined in their fixed cross | 🧪 allowed with a warning: built like the River of Flame (each room keeps its own floor model) but not tested in game yet. The seals and Diablo's arrival are Act 4 quest code, so in a new level they may do nothing |
| Joined rooms of the **Arcane Sanctuary** | ⚠ allowed, but its platforms float over a void and floors were wrong in game with every layout tried so far |
| Joined rooms of every other set | 🧪 allowed with a warning: built like the River of Flame (each room keeps its own floor model), not tested in game yet. Check the HD floors in game |
| Maps over **121×121** tiles | 🔒 refused: a 193×193 fixed level crashed D2R (at the cube transmute, even with no objects); 121×121 (5×5 lava rooms) works |

**Bigger levels:** let your mod make the level a **random maze** of a room set (levels.txt + lvlmaze.txt). The game joins the rooms itself, each with its own floor, so HD is always right, and stairs between levels work. A fixed entrance (a game map or one room from this editor) can lead down into such maze levels.

## Making a map
- **📚 Game maps…**: any of the game's 2,044 preset maps, with their HD file. Big ones include Duriel's Lair, the River of Flame forge, the Durance, Worldstone Keep and the towns.
- **🧩 Compose…**: the game's maze room sets (River of Flame, Hell caverns, caves, crypts, the Jail, Catacombs, tombs, sewers, Arcane Sanctuary, jungle, Durance, Ice caves, Worldstone Keep, the Halls of Pain/Anguish/Vaught, the Spider Cavern, the Palace cellar and Harem…). The last four are 2×2 ring levels made of corner rooms only.
  - **Proven sets** (River of Flame, Hell caverns, pygmy jungle clearing): a grid of joined rooms. 🎲 Random maze, or click between rooms to open/close passages; each room gets the game piece with matching open sides, and layouts only use shapes the set has.
  - **Other sets:** pick one room.
  - **Act 4 - Chaos Sanctuary:** a fixed cross, not a maze. Diablo's heart room is in the middle, with the seal arms around it: Vizier W, De Seis N, Infector E. The S arm leads down to the entry. 73×97 tiles; right-click a room for its other variant.
  - Stairs rooms (🪜, with their exit number) are listed first: "Next" = way down, "Prev" = way back up.
  - At most 121×121 tiles (e.g. 5×5 lava rooms); bigger grids can't be built.
  - Monsters and objects are ids of the map's act (the first room's). A room from a map of another act gets the same presets in this act, or loses the ones this act doesn't have.
  - **Add spawns** puts spawn points on walkable floor (the game's own walk flags of each tile): up to 8 per room, at least 5 tiles apart.
  - Kinds: champion packs, monster groups, unique packs, or **the act's own packs** (Act 1 Fallen camps, Act 3 Fetishes, Act 5 imps, Minions of Destruction and Blood Lords; Acts 2 and 4 have none). Mixed uses them all.
- **Open map…**: a `.ds1` from disk; select its `.json` (HD) or `.pieces.json` at the same time if you have them. Otherwise the editor looks for the HD file itself: the game map with the same name and tiles, or the game room(s) it's made of.
- **👹 Monsters & objects:** a searchable list of the game's presets for the map's act (bosses, NPCs, spawn points, chests, shrines, waypoints, torches…). Click one, then click the map.
  - Objects the game doesn't know are marked: they crash D2R.
  - Act and quest bosses (Andariel, Duriel, Mephisto, Diablo, Baal…) are flagged: their deaths run quest code.
  - Waypoints are flagged: in a new level they can't take you anywhere (levels.txt Waypoint 255) unless the mod gives the level a waypoint slot.
  - **Main act bosses everywhere:** every act's Monsters list also shows the five act bosses (Andariel, Duriel, Mephisto, Diablo, Baal) under Bosses & super uniques. One from another act goes in as a new monster row, as below.
  - **Monsters from other acts:** pick another act in **Act list** and place its monsters. The ids belong to the map's act (its .ds1 header), so a monster that act doesn't have goes in as a new row at the end of the act's monster list (`monpreset.txt`), the way mods add their own bosses. Your mod must add those rows: the export lists them with the ids the map uses (counted from the game's own list; if another mod adds rows to that act first, they shift). Objects only come from the map's own act. Not tested in game yet.
- **🎲 Scatter spawn points** (same panel): N per room or per 12×12 tiles, on walkable floor; undo takes them back. Only spawn points the map's act has are used: Acts 3–4 have no unique packs, Act 5 only champion packs.
- **🪜 Exits** (same panel, Exits tab): place "Exit N" where a stairs/portal to another level should be. A mod links exit N with levels.txt `VisN` / `WarpN`.
  - **Every exit must be linked** by the mod (VisN = the level, WarpN = its stairs). D2R builds the linked levels when the cube portal opens, and an unlinked exit crashes it there. A level an exit leads into needs an exit back.
  - Placing an exit adds the warp tile file (`act1\barracks\warp.tg1`) to the map's file list, as the game's maps with exits have it.
  - Drag to move, renumber in the right panel, right-click or Del to remove.
  - Exits are invisible in game ("To <level>" shows on hover), so a red portal effect is added to the HD file on the spot.
  - The Map panel's "🪜 exits" list shows every exit, the game's own stairs included; green badges mark them on the map.
- **🧱 Remove walls** (tool): drag a rectangle. Its walls go from the `.ds1` (where you walk) and every HD piece in it goes from the HD file (the look); exits and stairs stay. The opened cells show red until a floor stamp covers them, and export is refused until they're covered.
- **🟫 Floor stamp** (tool): lays a game room's floor over the map. Its 3D floor model goes into the HD file (as an extra terrain entry, turned 0° or 180°) and its floor tiles into the `.ds1`, so the look and the walking change together.
  - Pick a room in the list (the map's own room set first; rooms of another look are refused, since HD floor textures come from the map's look), then click the map: the room's top corner goes there. Rooms with the most open floor work best.
  - Right-click a stamp to remove it; undo works too.
- **Tiles palette** (view only): the map's tiles with their real pictures and names, e.g. "Wall ↖ · Lava · Walls". The Cell panel names every layer of the cell under the mouse and the HD pieces on it.
- 🏰 **Game view** shows the map as in the game; 🗺 **Grid view** is a flat grid.
- **Walkable overlay** (Layers, on by default): green = players can walk there in game, red = blocked, down to the 5×5 sub-tiles of each cell (wall lines show as thin red strips). Collision only comes from the .ds1; the HD picture has none, so wherever HD draws lava or rocks over green, players walk on it. The Cell panel shows the same ("in game: ✓ walkable / ⛔ blocked / 12/25 sub-tiles walkable").
- **Lava:** the game's lava floor tiles block nothing by themselves. The .ds1 marks each open lava cell as unwalkable (bit 0x20000 of the floor cell), which the engine turns into wall collision on the whole cell. The overlay shows those cells red. The export also lists the source rooms' own lvlprest flags (Logicals, Outdoors, Animate, KillEdge, FillBlanks).
- **Cells with no floor** (black): in game they block only with lvlprest **FillBlanks 1**, which fills them with the game's Blank tile (blocks all 25 sub-tiles). With 0 they stay open void: monsters spawn there and players teleport in. The overlay shows them red, and the export sets FillBlanks 1 whenever a map has them.
- **Map edges** (Map panel): the game's maze rooms are open at their edges, because the next room carries on there. Used as a whole level, players walk through what HD draws at the edge and off the map. **Close the map edges** adds invisible blocking walls there, as published custom levels do for its levels: wall tiles of the map's own set with no HD piece, so HD shows nothing new but the game blocks. It's optional: Compose closes them by default (untick **Close the map edges** in the Compose dialog to skip it), and export only warns about open edges.
- **Tiles the level type doesn't load:** the checks list tiles whose tile file isn't in the LevelType + Dt1Mask you export with. They don't exist in game: no wall and no collision there.
- **The banner over the map** shows the HD state: green = HD file in place, red = none (export is refused).

## Export
**Export map + HD… → Save map files…** writes into a folder you pick:
- `<name>.ds1`,
- `<name>.json`, the HD file. In the game files it goes to `hd\env\preset\<the map's folder>\<name>.json`,
- `<name>.pieces.json`, for composed maps.

It also shows the settings a mod needs: levels.txt (DrlgType 2, LevelType, Pal, SizeX/SizeY = width-1/height-1) and lvlprest.txt (File1, Dt1Mask, and Logicals/Outdoors/Animate/KillEdge/FillBlanks as the source game rooms have them). Older `gamedata.js` files lack those flags: run `tools/extract-game-files.py` again.

It also gives a ready-to-paste **D2RMM `mod.js` block** for the lvlprest.txt row. Use it, or copy its trick: lvlprest.txt has Windows line ends, so D2RMM reads its last column as `Dt1Mask` plus a hidden `\r`. A row written with a plain `'Dt1Mask'` key silently loses its mask. The level then loads none of its tile files, every wall turns into a blank tile, and players walk through all walls and rocks; only the .ds1's own unwalkable cells (lava) still block. After installing, check that your row's last column in the mod's `lvlprest.txt` shows the mask.

## Tools
- `tools\extract-game-files.py`: the setup above. `--hd <map path>` also saves one map's HD file as plain `.json`.
- `tools\build-hd.js` (Node): builds a composed map's HD file outside the editor.
  ```
  node tools\build-hd.js <map.ds1> <out.json> --pieces <map.pieces.json>
  ```
  or, with `--recover <room folders>`, it first finds which rooms the map is made of.
- `tools\fix-hd.js <file.json>…` (Node): restores a joined map's HD file to the layout proven in game (every room's floor as a terrain entry). Use it on files from the short-lived "one terrain only" version (2026-11-06), whose floors went wrong. It only adds entries; the editor does the same on export.
- `test-roundtrip.js <files.ds1>`: checks that loading and saving keeps a `.ds1` byte-identical (it does on all 2,044 game maps).

## Publishing / sharing the editor
Share only the editor's own files:
- `index.html`
- `ds1.js`
- `analysis.js`
- `test-roundtrip.js`
- `tools\extract-game-files.py`
- `tools\build-hd.js`
- `tools\fix-hd.js`
- `README.md`

**Never share `gamedata.js` or the `game\` folder.** They're made from the game's files: Blizzard's maps, tile graphics, HD files and tables. Each user makes their own from their own copy of the game with the extractor. Don't bundle `CascLib.dll` either, unless you include its MIT license.

A fan-made tool, made for fun and **not for profit**. **Not affiliated with or endorsed by Blizzard Entertainment.**

Free to use, change and share. If your mod uses maps made with it, a mention of the D2R Map Editor would be appreciated.

Diablo and Diablo II: Resurrected are trademarks of Blizzard Entertainment, Inc.

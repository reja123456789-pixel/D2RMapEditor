// Builds the HD file (hd\env\preset\...json) of a map stitched from the game's maze rooms.
//
//   node tools/build-hd.js <map.ds1> <out.json> --pieces <map.pieces.json>
//   node tools/build-hd.js <map.ds1> <out.json> --recover <room folder>[+<room folder>...] [--room 24]
//
// --recover finds which game room sits in each grid cell by comparing the map's tiles with
// every room of the folders (for a map saved without its .pieces.json), and writes the
// .pieces.json next to the map. The rooms' HD files come from game/hd (made by
// python tools/extract-game-files.py). As the editor's "Build from pieces":
// every HD entity of a room is moved by its grid place (room * 10 units per tile, map x -> HD x,
// map y -> HD z); one terrain, the other rooms' terrain become plain entities.
const fs = require('fs');
const path = require('path');
const DS1 = require('../ds1.js');

const GAME = path.join(__dirname, '..', 'game');
const args = process.argv.slice(2);
const [mapFile, outFile] = args;
const opt = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : null; };

function wrapped(file, prefix) { // the editor's game/*.js wrappers: D2xxxLOAD(key, data);
    const src = fs.readFileSync(file, 'utf8');
    return JSON.parse(src.slice(src.indexOf(',') + 1, src.lastIndexOf(')')).trim());
}
const gameMap = file => DS1.parse(Buffer.from(wrapped(path.join(GAME, 'maps', file + '.js')), 'base64'));
const gameHd = file => { // D2HDLOADZ: gzip + base64; D2HDLOAD: plain json
    const data = wrapped(path.join(GAME, 'hd', file.replace(/\.ds1$/, '.js')));
    return typeof data === 'string' ? JSON.parse(require('zlib').gunzipSync(Buffer.from(data, 'base64')).toString('utf8')) : data;
};

const map = DS1.parse(fs.readFileSync(mapFile));
let spec;
if (opt('--pieces')) {
    spec = JSON.parse(fs.readFileSync(opt('--pieces'), 'utf8'));
} else if (opt('--recover')) {
    const folders = opt('--recover').split('+').map(f => f.toLowerCase().replace(/\\/g, '/').replace(/\/$/, ''));
    const src = fs.readFileSync(path.join(GAME, 'maps.js'), 'utf8');
    const all = JSON.parse(src.slice(src.indexOf('['), src.lastIndexOf(']') + 1));
    const rooms = all.filter(m => folders.some(f => m.file.startsWith(f + '/'))).map(m => ({ file: m.file, ds1: gameMap(m.file) }));
    const room = Number(opt('--room')) || 24;
    const cand = rooms.filter(r => r.ds1.width === room + 1 && r.ds1.height === room + 1);
    const gw = (map.width - 1) / room, gh = (map.height - 1) / room;
    if (!Number.isInteger(gw) || !Number.isInteger(gh)) throw new Error(`the map (${map.width}x${map.height}) isn't a grid of ${room}-tile rooms`);
    // a room's tiles compared with the map's block (main/sub/orientation of floors and walls)
    const key = v => (v >>> 8) & 0xFFFFFF;
    const score = (r, ox, oy) => {
        let same = 0, total = 0;
        for (let y = 1; y < room; y++) for (let x = 1; x < room; x++) { // inside, not the shared border
            const s = y * r.ds1.width + x, t = (oy + y) * map.width + (ox + x);
            const a = [], b = [];
            r.ds1.floors.forEach(l => a.push(key(l[s]))); map.floors.forEach(l => b.push(key(l[t])));
            r.ds1.walls.forEach((l, i) => l[s] && a.push(key(l[s]) + ':' + (r.ds1.orients[i][s] & 255)));
            map.walls.forEach((l, i) => l[t] && b.push(key(l[t]) + ':' + (map.orients[i][t] & 255)));
            total++;
            if (a.filter(Boolean).sort().join() === b.filter(Boolean).sort().join()) same++;
        }
        return same / total;
    };
    spec = { room, pieces: [] };
    for (let gy = 0; gy < gh; gy++) for (let gx = 0; gx < gw; gx++) {
        const ranked = cand.map(r => ({ file: r.file, s: score(r, gx * room, gy * room) })).sort((a, b) => b.s - a.s);
        const best = ranked[0];
        console.log(`room ${gx},${gy}: ${best.file}  ${(best.s * 100).toFixed(1)}% same  (next: ${ranked[1].file} ${(ranked[1].s * 100).toFixed(1)}%)`);
        if (best.s < 0.6) console.log(`  ! room ${gx},${gy} matches no room well: edited, or from another folder`);
        spec.pieces.push({ x: gx, y: gy, piece: best.file.replace(/\.ds1$/, '') });
    }
    const piecesOut = outFile.replace(/\.json$/i, '') + '.pieces.json';
    fs.writeFileSync(piecesOut, JSON.stringify(spec, null, 1));
    console.log('wrote', piecesOut);
} else {
    throw new Error('give --pieces <file> or --recover <folders>');
}

const KINDS = ['particles', 'models', 'skeletons', 'animations', 'textures', 'physics', 'json', 'variantdata', 'objecteffects', 'other'];
const deps = Object.fromEntries(KINDS.map(k => [k, []])), seen = Object.fromEntries(KINDS.map(k => [k, new Set()]));
const entities = [];
let terrain = null, biome = '', id = 1000000000;
const move = (e, dx, dz, suffix) => {
    const c = JSON.parse(JSON.stringify(e));
    c.id = id++; c.name = `${c.name}_${suffix}`;
    (c.components || []).forEach(k => { if (k.type === 'TransformDefinitionComponent' && k.position) { k.position.x += dx; k.position.z += dz; } });
    return c;
};
for (const p of spec.pieces) {
    const hd = gameHd(p.piece.toLowerCase() + '.ds1');
    const dx = p.x * (spec.roomW || spec.room) * 10, dz = p.y * (spec.roomH || spec.room) * 10, suffix = `p${p.x}_${p.y}`;
    KINDS.forEach(k => ((hd.dependencies || {})[k] || []).forEach(d => { if (!seen[k].has(d.path)) { seen[k].add(d.path); deps[k].push(d); } }));
    // every entity of the room, its own terrain entry included (the layout proven in game)
    (hd.entities || []).forEach(e => entities.push(move(e, dx, dz, suffix)));
    const ground = move(hd.terrain, dx, dz, suffix);
    if (!terrain) terrain = ground;
    else { ground.components = ground.components.filter(c => c.type !== 'TerrainDefinitionComponent'); entities.push(ground); }
    biome = biome || hd.biomeFilename || '';
}
const hd = { dependencies: deps, type: 'Preset', name: 'preset', entities, terrain, biomeFilename: biome, perTileBiomeOverrides: [], specialTiles: {} };
fs.writeFileSync(outFile, JSON.stringify(hd, null, 2));
console.log(`wrote ${outFile}: ${spec.pieces.length} rooms, ${entities.length} entities, ${deps.models.length} models`);

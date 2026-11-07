// Checks a map against the vanilla tables (gamedata.js):
//  - which LevelType / Dt1Mask loads the .dt1 tile files the map was made with
//  - objects that don't exist in monpreset.txt / objpreset.txt for the map's act (crash D2R)
//  - stairs / warp tiles (orientation 10/11 specials)
(function (root) {
    'use strict';

    // "\d2\data\global\tiles\act1\outdoors\towerb.tg1" and "Act1/Outdoors/TowerB.dt1" -> "act1/outdoors/towerb.dt1"
    function normTile(path) {
        return String(path || '').replace(/\\/g, '/').toLowerCase().replace(/^.*?tiles\//, '').replace(/\.tg1$/, '.dt1');
    }
    function mapTileFiles(map) {
        return map.files.filter(f => /\.(dt1|tg1)$/i.test(f)).map(normTile);
    }

    // all level types that have at least one of the map's tile files, best first
    function detectTileSet(map, data) {
        const wanted = [...new Set(mapTileFiles(map))];
        const out = [];
        for (const t of data.levelTypes) {
            let mask = 0, covered = 0;
            const missing = [];
            for (const f of wanted) {
                const i = t.files.findIndex(x => normTile(x) === f);
                if (i >= 0) { mask = (mask | (1 << i)) >>> 0; covered++; } else missing.push(f);
            }
            if (covered) out.push({ type: t.id, name: t.name, mask, pal: t.act - 1, covered, total: wanted.length, missing });
        }
        // more files covered first; then a type of the map's own act
        out.sort((a, b) => b.covered - a.covered || (b.pal === map.act) - (a.pal === map.act) || a.type - b.type);
        return { files: wanted, candidates: out };
    }

    function objectName(o, act, data) {
        if (o.type === 1) return data.monpreset[act] && data.monpreset[act][o.id];
        if (o.type === 2) return data.objpreset[act] && data.objpreset[act][o.id];
        return undefined;
    }
    function badObjects(map, data) {
        return map.objects.filter(o => !objectName(o, map.act, data));
    }

    function specialTiles(map, decode) {
        const out = [];
        map.walls.forEach((layer, i) => layer.forEach((v, c) => {
            const o = map.orients[i][c] & 0xFF;
            if ((o === 10 || o === 11) && v) {
                const d = decode(v);
                out.push({ layer: i, x: c % map.width, y: Math.floor(c / map.width), orient: o, main: d.main, sub: d.sub });
            }
        }));
        return out;
    }

    const api = { normTile, mapTileFiles, detectTileSet, objectName, badObjects, specialTiles };
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    else root.Analysis = api;
})(typeof window !== 'undefined' ? window : globalThis);

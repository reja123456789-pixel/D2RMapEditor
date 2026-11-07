// Restores the HD file (hd\env\preset\...json) of a map joined from the game's rooms to the
// layout proven in game (the 5x5 Hell cavern, the jungle clearings):
//   - the top-level terrain is room 0's;
//   - in entities, EVERY room's terrain entry WITH its TerrainDefinitionComponent (as the game's
//     own files list it), plus a plain copy of the other rooms' ground.
// A file from the short-lived "one terrain only" editor version (2026-11-06) has only the plain
// copies, and its floors went wrong in game: this adds the terrain entries back. Nothing is
// removed, so wall pieces, decorations, portal effects and edits stay as they are.
// (The Arcane Sanctuary's void rooms didn't work with any layout tried.)
//
//   node tools/fix-hd.js <file.json> [<file.json> ...]      (changes the files in place)
const fs = require('fs');

const isTerrain = e => (e.components || []).some(c => c.type === 'TerrainDefinitionComponent');
const isGroundModel = e => (e.components || []).some(c => c.type === 'ModelDefinitionComponent' && /terrain(_lod0)?\.model$/i.test(c.filename || ''));
const pos = e => {
    const t = (e.components || []).find(c => c.type === 'TransformDefinitionComponent');
    return t ? [t.position.x, t.position.y, t.position.z].join(',') : '';
};

for (const file of process.argv.slice(2)) {
    const hd = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
    const terr = new Set(hd.entities.filter(isTerrain).map(pos));
    let id = hd.entities.reduce((n, e) => Math.max(n, Number(e.id) || 0), Number(hd.terrain && hd.terrain.id) || 0) + 1;
    let added = 0;
    if (hd.terrain && !terr.has(pos(hd.terrain))) { hd.entities.unshift(JSON.parse(JSON.stringify(hd.terrain))); terr.add(pos(hd.terrain)); added++; }
    for (const e of [...hd.entities]) {
        if (isTerrain(e) || !isGroundModel(e) || terr.has(pos(e))) continue;
        const c = JSON.parse(JSON.stringify(e));
        c.id = id++;
        c.components.splice(c.components.findIndex(k => k.type === 'TransformDefinitionComponent') + 1, 0, { type: 'TerrainDefinitionComponent', name: 'terrain_TerrainDefinitionComponent' });
        hd.entities.push(c);
        terr.add(pos(e));
        added++;
    }
    fs.writeFileSync(file, JSON.stringify(hd, null, 2));
    console.log(`${file}: ${added} terrain entries added back; rooms with a terrain entry: ${terr.size}; entities: ${hd.entities.length}`);
}

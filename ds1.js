// DS1 (Diablo II preset map) reader and writer.
// Works in the browser (window.DS1) and in Node (module.exports).
//
// Layout (version 18, the one D2R ships; older versions are read too):
//   int32 version, width-1, height-1, [v>=8] act, [v>=10] tagType
//   [v>=3] int32 fileCount + zero-terminated strings
//   [9<=v<=13] 2 skipped int32
//   [v>=4] int32 wallLayers, [v>=16] int32 floorLayers
//   layers, each width*height uint32 (row-major):
//     v<4 : wall1, floor1, orient1, tag, shadow
//     v>=4: (wall i, orient i) per wall layer, floors, shadow, [tagType 1/2] tag
//   [v>=2] int32 objectCount, each: type, id, x, y, [v>5] flags   (x/y in sub-tiles: tile*5)
//   [v>=12, tagType 1/2] [v>=18 int32 unknown] int32 groupCount, each x, y, w, h, [v>=13] unknown
//   [v>=14] int32 npcCount, each: pointCount, x, y, points (x, y, [v>=15] action)
(function (root) {
    'use strict';

    // orientation byte of version < 7 maps
    const OLD_DIR = [
        0x00, 0x01, 0x02, 0x01, 0x02, 0x03, 0x03, 0x05, 0x05, 0x06, 0x06, 0x07, 0x07, 0x08, 0x09, 0x0A,
        0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x14,
    ];

    function hasTagLayer(tagType) { return tagType === 1 || tagType === 2; }

    function parse(buffer) {
        const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        let p = 0;
        const i32 = () => { const v = view.getInt32(p, true); p += 4; return v; };
        const u32 = () => { const v = view.getUint32(p, true); p += 4; return v; };
        const left = () => bytes.byteLength - p;

        const map = {};
        map.version = i32();
        map.width = i32() + 1;
        map.height = i32() + 1;
        // a few game maps store an act above 4 (Siege borders); kept as is when written back
        map.actRaw = map.version >= 8 ? i32() : 0;
        map.act = Math.max(0, Math.min(map.actRaw, 4));
        map.tagType = map.version >= 10 ? i32() : 0;
        map.files = [];
        if (map.version >= 3) {
            const n = i32();
            for (let f = 0; f < n; f++) {
                let s = '';
                while (bytes[p] !== 0) s += String.fromCharCode(bytes[p++]);
                p++;
                map.files.push(s);
            }
        }
        map.skipped = [];
        if (map.version >= 9 && map.version <= 13) map.skipped = [i32(), i32()];

        let wallLayers = 1, floorLayers = 1;
        if (map.version >= 4) {
            wallLayers = i32();
            if (map.version >= 16) floorLayers = i32();
        }
        const cells = map.width * map.height;
        const readLayer = () => { const a = new Uint32Array(cells); for (let c = 0; c < cells; c++) a[c] = u32(); return a; };

        map.walls = []; map.orients = []; map.floors = []; map.shadow = null; map.tag = null;
        if (map.version < 4) {
            map.walls.push(readLayer());
            map.floors.push(readLayer());
            const o = readLayer();
            for (let c = 0; c < cells; c++) o[c] = OLD_DIR[o[c] & 0xFF] !== undefined ? OLD_DIR[o[c] & 0xFF] : o[c];
            map.orients.push(o);
            map.tag = readLayer();
            map.shadow = readLayer();
            map.version = 18; // written back in the current format
            map.tagType = map.tagType || 0;
        } else {
            for (let w = 0; w < wallLayers; w++) {
                map.walls.push(readLayer());
                const o = readLayer();
                if (map.version < 7) for (let c = 0; c < cells; c++) o[c] = OLD_DIR[o[c] & 0xFF];
                map.orients.push(o);
            }
            for (let f = 0; f < floorLayers; f++) map.floors.push(readLayer());
            map.shadow = readLayer();
            if (hasTagLayer(map.tagType)) map.tag = readLayer();
        }

        map.objects = [];
        if (map.version >= 2 && left() >= 4) {
            const n = i32();
            for (let o = 0; o < n; o++) {
                const obj = { type: i32(), id: i32(), x: i32(), y: i32(), flags: map.version > 5 ? i32() : 0, paths: null };
                map.objects.push(obj);
            }
        }

        map.groups = null;
        map.groupUnknown = 0;
        if (map.version >= 12 && hasTagLayer(map.tagType) && left() >= 4) {
            if (map.version >= 18) map.groupUnknown = i32();
            const n = i32();
            map.groups = [];
            for (let g = 0; g < n; g++) {
                map.groups.push({ x: i32(), y: i32(), w: i32(), h: i32(), unknown: map.version >= 13 ? i32() : 0 });
            }
        }

        // NPC paths belong to the object standing on their x/y
        map.pathOrder = [];
        map.hasPathSection = false;
        if (map.version >= 14 && left() >= 4) {
            map.hasPathSection = true;
            const n = i32();
            for (let k = 0; k < n; k++) {
                const count = i32(), x = i32(), y = i32();
                const points = [];
                for (let q = 0; q < count; q++) {
                    points.push({ x: i32(), y: i32(), action: map.version >= 15 ? i32() : 1 });
                }
                const owner = map.objects.find(o => o.x === x && o.y === y && !o.paths);
                if (owner) { owner.paths = points; map.pathOrder.push(owner); }
            }
        }
        map.trailing = p < bytes.byteLength ? bytes.slice(p) : new Uint8Array(0);
        return map;
    }

    function write(map) {
        const out = [];
        const chunk = new DataView(new ArrayBuffer(4));
        const i32 = v => { chunk.setInt32(0, v | 0, true); out.push(...new Uint8Array(chunk.buffer.slice(0))); };
        const u32 = v => { chunk.setUint32(0, v >>> 0, true); out.push(...new Uint8Array(chunk.buffer.slice(0))); };
        const v = map.version;
        i32(v); i32(map.width - 1); i32(map.height - 1);
        if (v >= 8) i32(map.actRaw !== undefined && Math.max(0, Math.min(map.actRaw, 4)) === map.act ? map.actRaw : map.act);
        if (v >= 10) i32(map.tagType);
        if (v >= 3) {
            i32(map.files.length);
            for (const s of map.files) { for (let k = 0; k < s.length; k++) out.push(s.charCodeAt(k) & 0xFF); out.push(0); }
        }
        if (v >= 9 && v <= 13) { i32(map.skipped[0] || 0); i32(map.skipped[1] || 0); }
        i32(map.walls.length);
        if (v >= 16) i32(map.floors.length);
        const layer = a => { for (let c = 0; c < a.length; c++) u32(a[c]); };
        for (let w = 0; w < map.walls.length; w++) { layer(map.walls[w]); layer(map.orients[w]); }
        map.floors.forEach(layer);
        layer(map.shadow);
        if (hasTagLayer(map.tagType)) layer(map.tag);

        i32(map.objects.length);
        for (const o of map.objects) { i32(o.type); i32(o.id); i32(o.x); i32(o.y); if (v > 5) i32(o.flags); }

        if (v >= 12 && hasTagLayer(map.tagType) && map.groups) {
            if (v >= 18) i32(map.groupUnknown);
            i32(map.groups.length);
            for (const g of map.groups) { i32(g.x); i32(g.y); i32(g.w); i32(g.h); if (v >= 13) i32(g.unknown); }
        }

        if (v >= 14) {
            // keep the original path order, then any new ones
            const owners = map.pathOrder.filter(o => o.paths && map.objects.includes(o));
            for (const o of map.objects) if (o.paths && !owners.includes(o)) owners.push(o);
            if (map.hasPathSection || owners.length) {
                i32(owners.length);
                for (const o of owners) {
                    i32(o.paths.length); i32(o.x); i32(o.y);
                    for (const pt of o.paths) { i32(pt.x); i32(pt.y); if (v >= 15) i32(pt.action); }
                }
            }
        }
        for (const b of map.trailing || []) out.push(b);
        return new Uint8Array(out);
    }

    // a wall/floor cell, split into its fields
    function decodeCell(value) {
        return {
            prop1: value & 0xFF,
            sub: (value >>> 8) & 0x3F,      // sequence: sub index
            unknown1: (value >>> 14) & 0x3F,
            main: (value >>> 20) & 0x3F,     // style: main index
            unknown2: (value >>> 26) & 0x1F,
            hidden: (value >>> 31) & 1,
        };
    }
    function isEmpty(value) { return (value & 0xFF) === 0 && ((value >>> 8) & 0xFFFFFF) === 0; }

    // a new map the same shape (layer counts, version, act, files) as another, all cells empty
    function resize(map, width, height, offsetX = 0, offsetY = 0) {
        const move = a => {
            if (!a) return a;
            const b = new Uint32Array(width * height);
            for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
                const sx = x - offsetX, sy = y - offsetY;
                if (sx >= 0 && sy >= 0 && sx < map.width && sy < map.height) b[y * width + x] = a[sy * map.width + sx];
            }
            return b;
        };
        const sized = {
            ...map,
            width, height,
            walls: map.walls.map(move), orients: map.orients.map(move), floors: map.floors.map(move),
            shadow: move(map.shadow), tag: move(map.tag),
        };
        sized.objects = map.objects
            .map(o => ({ ...o, x: o.x + offsetX * 5, y: o.y + offsetY * 5, paths: o.paths && o.paths.map(pt => ({ ...pt, x: pt.x + offsetX * 5, y: pt.y + offsetY * 5 })) }))
            .filter(o => o.x >= 0 && o.y >= 0 && o.x < width * 5 && o.y < height * 5);
        sized.pathOrder = [];
        if (sized.groups) sized.groups = sized.groups.map(g => ({ ...g, x: g.x + offsetX, y: g.y + offsetY }));
        return sized;
    }

    const api = { parse, write, decodeCell, isEmpty, resize };
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    else root.DS1 = api;
})(typeof window !== 'undefined' ? window : globalThis);

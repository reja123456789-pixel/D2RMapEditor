// node test-roundtrip.js <file.ds1>... : parse + write must give back the same bytes
const fs = require('fs');
const DS1 = require('./ds1.js');
let bad = 0;
for (const f of process.argv.slice(2)) {
    const src = fs.readFileSync(f);
    const map = DS1.parse(src);
    const out = DS1.write(map);
    const same = Buffer.compare(Buffer.from(out), src) === 0;
    if (!same) bad++;
    const pathed = map.objects.filter(o => o.paths).length;
    console.log(same ? 'OK  ' : 'DIFF', f.split(/[\/]/).pop(), `v${map.version} ${map.width}x${map.height} act${map.act} tag${map.tagType} walls${map.walls.length} floors${map.floors.length} objs${map.objects.length} paths${pathed} groups${map.groups ? map.groups.length : '-'} trailing${map.trailing.length}`);
}
process.exit(bad ? 1 : 0);

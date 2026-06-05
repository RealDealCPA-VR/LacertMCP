const fs = require('fs');
const out = JSON.parse(fs.readFileSync('C:/Users/VR/AppData/Local/Temp/claude/C--Users-VR/c264b828-eb04-4d32-9ce0-c6d8d6164eaf/tasks/wh20i8nv4.output', 'utf8'));
const r = out.result;
console.log('=== FLAWS ===');
console.log('Total:', r.flaws.totalCandidates, '| Confirmed:', r.flaws.confirmedCount);
const flBySev = {}, flByLens = {};
for (const f of r.flaws.confirmed) {
  flBySev[f.severity] = (flBySev[f.severity] || 0) + 1;
  flByLens[f.lens] = (flByLens[f.lens] || 0) + 1;
}
console.log('By sev:', JSON.stringify(flBySev));
console.log('By lens:', JSON.stringify(flByLens));
console.log('\n--- FLAW TITLES ---');
r.flaws.confirmed.forEach((f, i) => {
  const file = (f.file || '').replace(/\\/g, '/').replace('C:/Users/VR/Projects/LacertMCP/', '').slice(0, 80);
  console.log((i + 1).toString().padStart(2) + '. [' + f.severity.toUpperCase().padEnd(6) + '] [' + (f.lens || '').padEnd(22) + '] ' + (f.title || '').slice(0, 80));
  console.log('     ' + file + ' (' + (f.line || '?').slice(0, 50) + ')');
});

console.log('\n=== FEATURES ===');
console.log('Total:', r.features.totalProposed, '| Confirmed:', r.features.confirmedCount);
const featByLens = {}, featByEffort = {};
for (const f of r.features.confirmed) {
  featByLens[f.lens] = (featByLens[f.lens] || 0) + 1;
  featByEffort[f.effort] = (featByEffort[f.effort] || 0) + 1;
}
console.log('By lens:', JSON.stringify(featByLens));
console.log('By effort:', JSON.stringify(featByEffort));
console.log('\n--- FEATURE TITLES ---');
r.features.confirmed.forEach((f, i) => {
  console.log((i + 1).toString().padStart(2) + '. [' + (f.effort || '').padEnd(7) + '] [' + (f.lens || '').padEnd(22) + '] ' + (f.title || '').slice(0, 90));
});
console.log('\nSaturated:', r.saturated);

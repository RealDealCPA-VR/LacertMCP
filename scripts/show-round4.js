const fs = require('fs');
const out = JSON.parse(fs.readFileSync('C:/Users/VR/AppData/Local/Temp/claude/C--Users-VR/c264b828-eb04-4d32-9ce0-c6d8d6164eaf/tasks/wo0a18m03.output', 'utf8'));
const r = out.result;
console.log('Total:', r.totalCandidates, '| Confirmed:', r.confirmedCount, '| Saturated:', r.saturated);
const byLens = {}, bySev = {};
for (const f of r.confirmed) { byLens[f.lens] = (byLens[f.lens] || 0) + 1; bySev[f.severity] = (bySev[f.severity] || 0) + 1; }
console.log('By lens:', JSON.stringify(byLens));
console.log('By sev:', JSON.stringify(bySev));
console.log('\n=== TITLES ===');
r.confirmed.forEach((f, i) => {
  const file = (f.file || '').replace(/\\/g, '/').replace('C:/Users/VR/Projects/LacertMCP/', '').slice(0, 90);
  console.log((i + 1).toString().padStart(2) + '. [' + f.severity.toUpperCase().padEnd(6) + '] [' + (f.lens || '').padEnd(20) + '] ' + (f.title || '').slice(0, 90));
  console.log('     file: ' + file + ' (' + (f.line || '?') + ')');
});

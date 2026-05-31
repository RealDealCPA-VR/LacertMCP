// Cluster Phase-15 findings by primary file owner
const fs = require('fs');
const out = JSON.parse(fs.readFileSync('C:/Users/VR/AppData/Local/Temp/claude/C--Users-VR/c264b828-eb04-4d32-9ce0-c6d8d6164eaf/tasks/wo0fa30k2.output', 'utf8'));
const findings = out.result.confirmed;

const sevOrder = { high: 0, medium: 1, low: 2 };
findings.sort((a, b) => (sevOrder[a.severity] - sevOrder[b.severity]) || a.lens.localeCompare(b.lens));
findings.forEach((f, i) => { f.taskId = 'P15-' + String(i + 1).padStart(3, '0'); });

function cluster(f) {
  const file = (f.file || '').toLowerCase().replace(/\\/g, '/');
  if (file.includes('wraptoolhandler') || file.includes('wirelog')) return 'A_logging';
  if (file.includes('lacertesessionmanager')) return 'B_session';
  if (file.includes('bridge/')) return 'C_bridge';
  if (file.includes('odbc/')) return 'D_odbc';
  if (file.includes('errors/')) return 'E_errors';
  if (file.includes('schema/')) return 'F_schema';
  if (file.includes('prompts/')) return 'G_prompts';
  if (file.includes('resilience/')) return 'H_resilience';
  if (file.includes('pagination/')) return 'I_pagination';
  if (file.includes('tools/details')) return 'J_details';
  if (file.includes('tools/health')) return 'J_health';
  if (file.includes('tools/clientstools') || file.includes('tools/clients.')) return 'J_clients';
  if (file.includes('tools/sessiontools')) return 'J_sessiontools';
  if (file.includes('tools/createclienttools')) return 'J_createtools';
  if (file.includes('tools/fieldwrite') || file.includes('tools/updatereturn')) return 'J_writetools';
  if (file.includes('tools/metadatatools')) return 'J_metatools';
  if (file.includes('tools/')) return 'J_tools_other';
  if (file.includes('cli/commands/') || file.includes('lacertmcp.cli/commands')) return 'K_cli';
  if (file.includes('cli/program') || file.includes('lacertmcp.cli/program')) return 'K_cli';
  if (file.includes('cli/storage') || file.includes('cli\\storage')) return 'K_cli';
  if (file.includes('server/program')) return 'L_program';
  if (file.includes('lacertmcp.tests/') || file.includes('tests/')) return 'M_tests';
  if (file.includes('readme.md')) return 'N_readme';
  if (file.includes('architecture.md')) return 'N_arch';
  if (file.includes('claude.md')) return 'N_claude';
  if (file.includes('decisions.md')) return 'N_decisions';
  if (file.includes('requirements.md')) return 'N_req';
  if (file.includes('regression_checklist.md')) return 'N_regression';
  if (file.includes('acceptance_criteria.md')) return 'N_acceptance';
  if (file.includes('todo.md')) return 'N_todo';
  if (file.includes('handoff.md')) return 'N_handoff';
  if (file.includes('.env.example')) return 'N_env';
  if (file.includes('lacerte_mcp_structure_spec') || file.includes('docs/')) return 'N_docs';
  if (file.includes('.github') || file.includes('scripts/')) return 'O_dx';
  if (file.includes('csproj') || file.includes('app.config') || file.includes('.editorconfig')) return 'O_dx';
  return 'Z_misc';
}
findings.forEach(f => { f.cluster = cluster(f); });
fs.writeFileSync('C:/Users/VR/Projects/LacertMCP/audit-findings-r2.json', JSON.stringify(findings, null, 2));

const cc = {};
findings.forEach(f => { (cc[f.cluster] ||= []).push(f.taskId); });
console.log('Wrote audit-findings-r2.json:', findings.length, 'findings');
console.log('\nCluster sizes:');
Object.entries(cc).sort().forEach(([k, v]) => {
  console.log(' ', k.padEnd(20), v.length.toString().padStart(2), v.slice(0, 10).join(',') + (v.length > 10 ? ',…' : ''));
});

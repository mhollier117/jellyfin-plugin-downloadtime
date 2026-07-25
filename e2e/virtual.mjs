// Virtual placeholder lifecycle E2E (frozen once first fix observed).
// Usage: node e2e/virtual.mjs enable | assert-placeholder | restore-and-assert-gone | reset-and-assert-zero
// Flow: detect.mjs plant  ->  enable  ->  assert-placeholder  ->  detect.mjs restore
//       -> restore-and-assert-gone  ->  reset-and-assert-zero
import fs from 'node:fs';
import process from 'node:process';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
const cfg = JSON.parse(fs.readFileSync(new URL('./config.local.json', import.meta.url)));
const H = { Authorization: `MediaBrowser Token="${cfg.token}"`, 'Content-Type': 'application/json' };
const get = async (p) => (await fetch(cfg.baseUrl + p, { headers: H })).json();
const post = async (p, body) => fetch(cfg.baseUrl + p, { method: 'POST', headers: H, body: body && JSON.stringify(body) });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const die = (msg) => { console.error('FAIL:', msg); process.exit(1); };

async function series() {
  const r = await get(`/Items?IncludeItemTypes=Series&Recursive=true&SearchTerm=${encodeURIComponent(cfg.target.seriesName)}`);
  return r.Items[0];
}
async function placeholders(sid) {
  // /Shows/{id}/Episodes hides virtual items without a user with
  // DisplayMissingEpisodes; the generic /Items query does not.
  const r = await get(`/Items?ParentId=${sid}&IncludeItemTypes=Episode&Recursive=true&IsVirtualItem=true&Fields=ProviderIds`);
  return (r.Items || []).filter((e) => e.ProviderIds && e.ProviderIds.DownloadTime);
}
async function runScanTask() {
  const res = await post('/DownloadTime/Scan?fullRefresh=true');
  if (res.status !== 202) die(`scan HTTP ${res.status}`);
  await sleep(90000);
}

const s = await series();
const mode = process.argv[2];

if (mode === 'enable') {
  const c = await get(`/Plugins/${cfg.pluginGuid}/Configuration`);
  c.CreateVirtualEpisodes = true;
  await post(`/Plugins/${cfg.pluginGuid}/Configuration`, c);
  // Placeholder application runs inside the SCHEDULED task; trigger it:
  const tasks = await get('/ScheduledTasks');
  const scan = tasks.find((t) => t.Key === 'DownloadTimeScan');
  await post(`/ScheduledTasks/Running/${scan.Id}`);
  await sleep(120000);
  console.log('ENABLE DONE');
} else if (mode === 'assert-placeholder') {
  const ph = await placeholders(s.Id);
  const hit = ph.find((e) => e.ParentIndexNumber === cfg.target.season && e.IndexNumber === cfg.target.episode);
  if (!hit) die(`no placeholder at S${cfg.target.season}E${cfg.target.episode}; found ${ph.length} total`);
  console.log('ASSERT-PLACEHOLDER PASS', hit.Name);
} else if (mode === 'restore-and-assert-gone') {
  // run AFTER detect.mjs restore (file back + library refresh + metadata refresh).
  // 12.0 RemoveObsoleteEpisodes fires on series refresh; force one:
  await post(`/Items/${s.Id}/Refresh?metadataRefreshMode=Default&recursive=true`);
  await sleep(60000);
  const ph = await placeholders(s.Id);
  const still = ph.find((e) => e.ParentIndexNumber === cfg.target.season && e.IndexNumber === cfg.target.episode);
  if (still) die('placeholder survived physical restore (twin-cleanup failed)');
  console.log('TWIN-CLEANUP PASS');
} else if (mode === 'reset-and-assert-zero') {
  const tasks = await get('/ScheduledTasks');
  const reset = tasks.find((t) => t.Key === 'DownloadTimeReset');
  await post(`/ScheduledTasks/Running/${reset.Id}`);
  await sleep(30000);
  const all = await get(`/Items?IncludeItemTypes=Episode&Recursive=true&IsVirtualItem=true&Fields=ProviderIds&Limit=2000`);
  const ours = (all.Items || []).filter((e) => e.ProviderIds && e.ProviderIds.DownloadTime);
  if (ours.length) die(`${ours.length} placeholders survived reset`);
  console.log('RESET PASS');
} else {
  die('usage: node e2e/virtual.mjs enable|assert-placeholder|restore-and-assert-gone|reset-and-assert-zero');
}

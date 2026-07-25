// Detection E2E (frozen once first fix observed - FREEZE RULE).
// Usage: node e2e/detect.mjs baseline | plant | assert-gap | restore
// baseline: with all files present, target episode must NOT be reported missing.
// plant:    move target episode file to holding dir + library refresh (the RED setup).
// assert-gap: full-refresh plugin scan; target must appear as exactly one Gap.
// restore:  put file back, refresh, rescan; series must report zero missing again.
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
const cfg = JSON.parse(fs.readFileSync(new URL('./config.local.json', import.meta.url)));
const H = { Authorization: `MediaBrowser Token="${cfg.token}"` };

const get = async (p) => (await fetch(cfg.baseUrl + p, { headers: H })).json();
const post = async (p) => fetch(cfg.baseUrl + p, { method: 'POST', headers: H });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const die = (msg) => { console.error('FAIL:', msg); process.exit(1); };

// renameSync fails with EXDEV across drives (library on D:, repo on C:).
function moveFile(src, dest) {
  try {
    fs.renameSync(src, dest);
  } catch (e) {
    if (e.code !== 'EXDEV') throw e;
    fs.copyFileSync(src, dest);
    fs.rmSync(src);
  }
}

async function findSeries() {
  const r = await get(`/Items?IncludeItemTypes=Series&Recursive=true&SearchTerm=${encodeURIComponent(cfg.target.seriesName)}&Fields=Path`);
  if (!r.Items?.length) die('series not found');
  return r.Items[0];
}

async function findEpisodeFile(series) {
  const eps = await get(`/Shows/${series.Id}/Episodes?Fields=Path`);
  const ep = eps.Items.find((e) => e.ParentIndexNumber === cfg.target.season && e.IndexNumber === cfg.target.episode && e.LocationType !== 'Virtual');
  return ep?.Path;
}

// Trigger a library scan and poll until the target episode reaches the wanted
// presence state (full refresh includes the 65k-item Collection 2 — can take minutes).
async function libraryRefreshUntil(series, wantPresent) {
  await post('/Library/Refresh');
  for (let i = 0; i < 32; i++) {
    await sleep(15000);
    const present = !!(await findEpisodeFile(series));
    if (present === wantPresent) return;
  }
  die(`library refresh did not converge (wantPresent=${wantPresent}) within 8 min`);
}

async function pluginScanAndReport() {
  const res = await post('/DownloadTime/Scan?fullRefresh=true');
  if (res.status !== 202) die(`scan trigger HTTP ${res.status}`);
  for (let i = 0; i < 60; i++) {
    await sleep(5000);
    const report = await get('/DownloadTime/Report');
    if (report.FinishedAt && Date.now() - new Date(report.FinishedAt).getTime() < 5 * 60 * 1000) return report;
  }
  die('scan did not finish in time');
}

function seriesEntry(report, name) {
  return (report.Series || []).find((s) => s.Name.startsWith(name));
}

const mode = process.argv[2];
const series = await findSeries();

if (mode === 'baseline') {
  const report = await pluginScanAndReport();
  const s = seriesEntry(report, cfg.target.seriesName);
  if (!s) die('series missing from report');
  if (s.Error) die(`series errored: ${s.Error}`);
  const hit = (s.Missing || []).find((m) => m.Season === cfg.target.season && m.Number === cfg.target.episode);
  if (hit) die('target episode reported missing while file present');
  console.log('BASELINE PASS');
} else if (mode === 'plant') {
  const originPath = path.join(cfg.holdingDir, 'origin.json');
  if (fs.existsSync(originPath) && !fs.existsSync(JSON.parse(fs.readFileSync(originPath)).file)) {
    console.log('file already in holding; resuming refresh wait');
  } else {
    const file = await findEpisodeFile(series);
    if (!file) die('target episode file not found');
    fs.mkdirSync(cfg.holdingDir, { recursive: true });
    const dest = path.join(cfg.holdingDir, path.basename(file));
    moveFile(file, dest);
    fs.writeFileSync(originPath, JSON.stringify({ file }));
    console.log('moved', file, '->', dest);
  }
  await libraryRefreshUntil(series, false);
  console.log('PLANT DONE');
} else if (mode === 'assert-gap') {
  const report = await pluginScanAndReport();
  const s = seriesEntry(report, cfg.target.seriesName);
  if (!s) die('series missing from report');
  if (s.Error) die(`series errored: ${s.Error}`);
  const hits = (s.Missing || []).filter((m) => m.Season === cfg.target.season && m.Number === cfg.target.episode);
  if (hits.length !== 1) die(`expected exactly 1 hit for target, got ${hits.length}`);
  if (hits[0].Kind !== 'Gap') die(`expected Gap, got ${hits[0].Kind}`);
  const others = (s.Missing || []).filter((m) => !(m.Season === cfg.target.season && m.Number === cfg.target.episode));
  if (others.length) die(`unexpected extra missing entries: ${JSON.stringify(others)}`);
  console.log('ASSERT-GAP PASS');
} else if (mode === 'restore') {
  const { file } = JSON.parse(fs.readFileSync(path.join(cfg.holdingDir, 'origin.json')));
  moveFile(path.join(cfg.holdingDir, path.basename(file)), file);
  await libraryRefreshUntil(series, true);
  const report = await pluginScanAndReport();
  const s = seriesEntry(report, cfg.target.seriesName);
  if ((s.Missing || []).length) die(`still missing after restore: ${JSON.stringify(s.Missing)}`);
  console.log('RESTORE PASS');
} else {
  die('usage: node e2e/detect.mjs baseline|plant|assert-gap|restore');
}

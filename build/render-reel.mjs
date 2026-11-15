import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, rmSync, writeFileSync, readdirSync } from 'node:fs';
import { resolve, join } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const themeArg = process.argv.includes('--theme')
  ? (process.argv[process.argv.indexOf('--theme') + 1] || 'dark')
  : 'dark';
if (themeArg !== 'dark' && themeArg !== 'light') throw new Error('unknown theme: ' + themeArg);
const theme = themeArg;
const pageUrl = 'file:///' + join(root, 'site', 'dev-reel.html').replace(/\\/g, '/') + '?capture=1&theme=' + theme;
const outDir = join(root, 'artifacts', theme === 'light' ? 'reel-frames-light' : 'reel-frames');
const mp4 = join(root, 'site', 'assets', theme === 'light' ? 'dev-reel-light.mp4' : 'dev-reel.mp4');
const poster = join(root, 'site', 'assets', theme === 'light' ? 'dev-reel-light-poster.jpg' : 'dev-reel-poster.jpg');
const fps = 24;
const duration = 46;
let port = 9400;
const FRAME_TIMEOUT = 25000;

const edgeCandidates = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe'
];
const edge = edgeCandidates.find(existsSync);
if (!edge) throw new Error('Edge not found');

const ffmpegCandidates = [
  'C:/Users/worldcopyist/AppData/Local/Microsoft/WinGet/Links/ffmpeg.exe',
  'ffmpeg'
];
const ffmpeg = ffmpegCandidates.find((p) => p === 'ffmpeg' || existsSync(p));

const probeOnly = process.argv.includes('--probe');
const probeTimes = probeOnly
  ? (process.argv[process.argv.indexOf('--probe') + 1] || '1.5,5,9').split(',').map(Number)
  : [];
const resume = process.argv.includes('--resume');
const encodeOnly = process.argv.includes('--encode');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let launchCount = 0;
let browser = null;
let ws = null;
let nextId = 1;
let pending = new Map();
let profileDir = '';

function handleMessage(ev) {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    const entry = pending.get(msg.id);
    pending.delete(msg.id);
    clearTimeout(entry.timer);
    if (msg.error) entry.reject(new Error(JSON.stringify(msg.error)));
    else entry.resolve(msg.result);
  }
}

function send(method, params = {}, timeout = 30000) {
  return new Promise((ok, fail) => {
    const id = nextId++;
    const timer = setTimeout(() => {
      pending.delete(id);
      fail(new Error('CDP timeout: ' + method));
    }, timeout);
    pending.set(id, { resolve: ok, reject: fail, timer });
    ws.send(JSON.stringify({ id, method, params }));
  });
}

async function waitForTarget() {
  for (let i = 0; i < 120; i++) {
    try {
      const res = await fetch('http://127.0.0.1:' + port + '/json/list');
      const list = await res.json();
      const page = list.find((t) => t.type === 'page' && t.url.includes('dev-reel.html'));
      if (page) return page;
    } catch {}
    await sleep(150);
  }
  throw new Error('CDP target not found');
}

async function launchOnce() {
  launchCount += 1;
  port = 9400 + launchCount;
  profileDir = join(root, 'artifacts', 'reel-profile-' + process.pid + '-' + launchCount);
  try { rmSync(profileDir, { recursive: true, force: true }); } catch {}
  mkdirSync(profileDir, { recursive: true });

  browser = spawn(edge, [
    '--headless=new',
    '--disable-gpu',
    '--hide-scrollbars',
    '--force-device-scale-factor=1',
    '--window-size=1440,810',
    '--remote-debugging-port=' + port,
    '--user-data-dir=' + profileDir.replace(/\\/g, '/'),
    '--no-first-run',
    '--no-default-browser-check',
    pageUrl
  ], { stdio: 'ignore' });

  const target = await waitForTarget();
  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  nextId = 1;
  pending = new Map();
  ws.onmessage = handleMessage;

  await send('Page.enable');
  await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', {
    width: 1440, height: 810, deviceScaleFactor: 1, mobile: false,
    screenWidth: 1440, screenHeight: 810
  });
  await send('Runtime.evaluate', {
    expression: `new Promise(r => {
      const go = () => requestAnimationFrame(() => requestAnimationFrame(() => r(true)));
      if (document.fonts && document.fonts.ready) document.fonts.ready.then(go); else go();
    })`,
    awaitPromise: true
  });
  const ready = await send('Runtime.evaluate', { expression: 'window.__reelReady === true', returnByValue: true });
  if (!ready.result.value) throw new Error('reel page not ready');
}

async function launch() {
  let lastErr;
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      await launchOnce();
      return;
    } catch (err) {
      lastErr = err;
      console.log('launch attempt', attempt, 'failed:', err.message);
      await shutdown();
      await sleep(1500);
    }
  }
  throw lastErr;
}

function killTree(pid) {
  if (!pid) return;
  try {
    spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { stdio: 'ignore' });
  } catch {}
}

async function shutdown() {
  try { await send('Browser.close', {}, 4000); } catch {}
  try { ws.close(); } catch {}
  killTree(browser && browser.pid);
  for (const entry of pending.values()) clearTimeout(entry.timer);
  pending.clear();
  browser = null;
  ws = null;
  await sleep(700);
  try { rmSync(profileDir, { recursive: true, force: true }); } catch {}
}

async function capture(t, file) {
  await send('Runtime.evaluate', { expression: `window.renderAt(${t})` }, FRAME_TIMEOUT);
  await send('Runtime.evaluate', {
    expression: 'new Promise(r => requestAnimationFrame(r))',
    awaitPromise: true
  }, FRAME_TIMEOUT);
  const shot = await send('Page.captureScreenshot', {
    format: 'png', fromSurface: true, optimizeForSpeed: true
  }, FRAME_TIMEOUT);
  writeFileSync(file, Buffer.from(shot.data, 'base64'));
}

if (!probeOnly && !resume && !encodeOnly) {
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });
} else if (probeOnly) {
  mkdirSync(join(root, 'artifacts', 'reel-probe'), { recursive: true });
} else if (!existsSync(outDir)) {
  mkdirSync(outDir, { recursive: true });
}
if (probeOnly) console.log('theme:', theme);

function encode() {
  const count = readdirSync(outDir).filter((f) => f.endsWith('.png')).length;
  console.log('frames ready:', count);
  if (count !== fps * duration) throw new Error('frame count mismatch: ' + count);

  const enc = spawnSync(ffmpeg, [
    '-y', '-framerate', String(fps),
    '-i', join(outDir, 'f-%05d.png'),
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '20',
    '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
    mp4
  ], { stdio: 'inherit' });
  if (enc.status !== 0) throw new Error('ffmpeg encode failed');

  const posterTime = 1.6;
  const posterFrame = join(outDir, 'f-' + String(Math.round(posterTime * fps)).padStart(5, '0') + '.png');
  const jpg = spawnSync(ffmpeg, ['-y', '-i', posterFrame, '-q:v', '4', poster], { stdio: 'inherit' });
  if (jpg.status !== 0) throw new Error('poster failed');
  console.log('mp4:', mp4);
  console.log('poster:', poster);
}

if (encodeOnly) {
  encode();
} else {
  await launch();

  try {
    if (probeOnly) {
      for (const t of probeTimes) {
        const file = join(root, 'artifacts', 'reel-probe', theme + '-t' + String(t).replace('.', '_') + '.png');
        await capture(t, file);
        console.log('probe', t, file);
      }
    } else {
      const total = fps * duration;
      const started = Date.now();
      for (let i = 0; i < total; i++) {
        const file = join(outDir, 'f-' + String(i).padStart(5, '0') + '.png');
        if (resume && existsSync(file)) continue;
        try {
          await capture(i / fps, file);
        } catch (err) {
          console.log('frame', i, 'failed:', err.message, '— relaunching');
          await shutdown();
          await launch();
          await capture(i / fps, file);
        }
        if (i % 24 === 0) {
          const rate = (i + 1) / ((Date.now() - started) / 1000);
          console.log('frame', i, '/', total, '·', rate.toFixed(1), 'fps');
        }
      }
      console.log('frames done:', readdirSync(outDir).length);

      encode();
    }
  } finally {
    await shutdown();
  }
}
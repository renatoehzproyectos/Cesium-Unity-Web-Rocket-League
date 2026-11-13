// Parallel, cached Brotli pass for a Unity WebGL build produced with compression DISABLED.
// Unity's own Brotli step is single-threaded at maximum quality (~9-14 min of a ~20 min CI run); this does the same
// job in ~1 min: all files at once on the libuv thread pool, quality 9, and results cached by the sha256 of the input
// (the huge .data file rarely changes between code-only commits, so it is usually a cache hit = 0 s).
//
// Usage: UV_THREADPOOL_SIZE=8 node tools/compress_build.js build/WebGL/WebGL [.brotli-cache]
// Renames  Build/<name>.{data,wasm,framework.js,worker.js}  ->  <name>.br  and rewrites index.html to point at them
// (same layout Unity produces with compressionFormat = Brotli, so deploy/vercel.json keeps working unchanged).
const fs = require('fs'), path = require('path'), zlib = require('zlib'), crypto = require('crypto');
const { promisify } = require('util');
const brotli = promisify(zlib.brotliCompress);

const root = process.argv[2];
const cacheDir = process.argv[3] || '.brotli-cache';
const Q = parseInt(process.env.BROTLI_QUALITY || '9', 10);
if (!root) { console.error('usage: node tools/compress_build.js <player dir> [cache dir]'); process.exit(2); }

const buildDir = path.join(root, 'Build');
const indexPath = path.join(root, 'index.html');
const RE = /\.(data|wasm|framework\.js|worker\.js)$/;
fs.mkdirSync(cacheDir, { recursive: true });

async function one(name) {
  const src = path.join(buildDir, name);
  const raw = fs.readFileSync(src);
  const key = crypto.createHash('sha256').update(raw).digest('hex') + '-q' + Q + '.br';
  const cached = path.join(cacheDir, key);
  let hit = fs.existsSync(cached);
  if (!hit) {
    const out = await brotli(raw, { params: {
      [zlib.constants.BROTLI_PARAM_QUALITY]: Q,
      [zlib.constants.BROTLI_PARAM_LGWIN]: 24,
      [zlib.constants.BROTLI_PARAM_SIZE_HINT]: raw.length,
    } });
    fs.writeFileSync(cached, out);
  }
  fs.copyFileSync(cached, src + '.br');
  fs.unlinkSync(src);
  console.log(`${hit ? 'cache hit ' : 'compressed '} ${name}  ${(raw.length / 1048576).toFixed(1)} MB -> ${(fs.statSync(src + '.br').size / 1048576).toFixed(1)} MB`);
  return { name, key };
}

(async () => {
  const t0 = Date.now();
  const names = fs.readdirSync(buildDir).filter(f => RE.test(f));
  if (!names.length) { console.error('no compressible files in ' + buildDir); process.exit(1); }
  const done = await Promise.all(names.map(one));

  let html = fs.readFileSync(indexPath, 'utf8');
  for (const { name } of done) {
    const from = '/' + name + '"';
    if (html.includes(from)) html = html.split(from).join('/' + name + '.br"');
    else if (/\.(data|wasm|framework\.js)$/.test(name)) { console.error(`index.html does not reference ${name}`); process.exit(1); }
  }
  fs.writeFileSync(indexPath, html);

  // keep the cache small: only entries used by this build survive (they become the next run's cache)
  const keep = new Set(done.map(d => d.key));
  for (const f of fs.readdirSync(cacheDir)) if (!keep.has(f)) fs.unlinkSync(path.join(cacheDir, f));
  console.log(`done in ${((Date.now() - t0) / 1000).toFixed(1)} s`);
})().catch(e => { console.error(e); process.exit(1); });

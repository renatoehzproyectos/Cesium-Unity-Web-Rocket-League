// Unity WebGL with threads: Unity Transport's WebSocket plugin runs inside the pthread web workers (job threads) and hands
// TextDecoder / WebSocket.send a view over the shared wasm heap, which browsers reject ("must not be shared").
// The page template patches the main thread; this script patches the worker script itself (Build/*.worker.js[.br|.gz]).
// Usage: node tools/patch_worker.js build/WebGL/WebGL/Build
const fs = require('fs'), path = require('path'), zlib = require('zlib');
const dir = process.argv[2];
const MARK = '/*rw-shared-fix*/';
const PATCH = MARK + '(function(){try{' +
  'var TD=self.TextDecoder&&TextDecoder.prototype,od=TD&&TD.decode;' +
  'if(od)TD.decode=function(d,o){try{if(typeof SharedArrayBuffer!=="undefined"){' +
  'if(d instanceof SharedArrayBuffer)d=new Uint8Array(d.slice(0));' +
  'else if(ArrayBuffer.isView(d)&&d.buffer instanceof SharedArrayBuffer){var c=new Uint8Array(d.byteLength);c.set(new Uint8Array(d.buffer,d.byteOffset,d.byteLength));d=c;}}}catch(e){}return od.call(this,d,o);};' +
  'var WS=self.WebSocket&&WebSocket.prototype,os=WS&&WS.send;' +
  'if(os)WS.send=function(d){try{if(typeof SharedArrayBuffer!=="undefined"){' +
  'if(d instanceof SharedArrayBuffer){var c=new Uint8Array(d.byteLength);c.set(new Uint8Array(d));d=c.buffer;}' +
  'else if(ArrayBuffer.isView(d)&&d.buffer instanceof SharedArrayBuffer){d=new Uint8Array(d);}}}catch(e){}return os.call(this,d);};' +
  '}catch(e){}})();\n';

let n = 0;
for (const f of fs.readdirSync(dir)) {
  const m = f.match(/\.worker\.js(\.br|\.gz)?$/);
  if (!m) continue;
  const p = path.join(dir, f), raw = fs.readFileSync(p);
  const enc = m[1];
  let text = (enc === '.br' ? zlib.brotliDecompressSync(raw) : enc === '.gz' ? zlib.gunzipSync(raw) : raw).toString('utf8');
  if (text.includes(MARK)) { console.log('already patched', f); continue; }
  const strict = text.match(/^\s*(["'])use strict\1;?/);          // keep a leading "use strict" directive first
  text = strict ? text.slice(0, strict[0].length) + '\n' + PATCH + text.slice(strict[0].length) : PATCH + text;
  const buf = Buffer.from(text, 'utf8');
  fs.writeFileSync(p, enc === '.br' ? zlib.brotliCompressSync(buf, { params: { [zlib.constants.BROTLI_PARAM_QUALITY]: 11 } }) : enc === '.gz' ? zlib.gzipSync(buf) : buf);
  console.log('patched', f); n++;
}
if (n === 0) console.log('::warning::no worker.js found in ' + dir + ' (build without threads?)');

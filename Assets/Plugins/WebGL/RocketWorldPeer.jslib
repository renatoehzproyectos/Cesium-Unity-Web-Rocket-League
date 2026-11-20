// Browser-to-browser transport for Netcode (WebRTC through PeerJS). Unity never gets callbacks: JS queues events, C# polls them.
// Event types returned by RW_PeerPoll: 0 none, 1 connect, 2 data, 3 disconnect. Client id 0 = the host.
var RW_ICE_CFG = function () { return { debug: 1, config: { iceCandidatePoolSize: 4, iceServers: [
      { urls: 'stun:stun.l.google.com:19302' }, { urls: 'stun:stun1.l.google.com:19302' }, { urls: 'stun:stun.cloudflare.com:3478' }, { urls: 'stun:global.stun.twilio.com:3478' },
      { urls: ['turn:openrelay.metered.ca:80', 'turn:openrelay.metered.ca:443', 'turn:openrelay.metered.ca:443?transport=tcp'], username: 'openrelayproject', credential: 'openrelayproject' } ] } }; };
mergeInto(LibraryManager.library, {

  RW_PeerHost: function (idPtr, maxVisitors) {
    var id = UTF8ToString(idPtr);
    (window.__rwpStop = window.__rwpStop || function () {
      var S = window.__rwp; if (!S) return;
      try { if (S.conns) for (var k in S.conns) { try { S.conns[k].c.close(); } catch (e) {} } } catch (e) {}
      try { if (S.srv && S.srv.c) S.srv.c.close(); } catch (e) {}
      try { if (S.peer) S.peer.destroy(); } catch (e) {}
      window.__rwp = null;
    })();
    var S = window.__rwp = { mode: 'host', status: 1, err: '', q: [], nextId: 1, conns: {}, peer: null, max: Math.max(1, Math.min(14, maxVisitors | 0)) };
    if (typeof Peer === 'undefined') { S.status = -1; S.err = 'PeerJS no se cargó (¿bloqueador o sin conexión?)'; return; }
    function drop(rec) { if (!rec || rec.gone) return; rec.gone = true; delete S.conns[rec.id]; if (rec.announced) S.q.push({ t: 3, id: rec.id }); try { rec.c.close(); } catch (e) {} }
    var p = S.peer = new Peer(id, RW_ICE_CFG());
    p.on('open', function () { S.status = 2; });
    p.on('error', function (e) {
      S.err = (e && e.type ? e.type + ': ' : '') + (e && e.message ? e.message : e);
      if (S.status === 1) S.status = (e && e.type === 'unavailable-id') ? -2 : -1;
    });
    p.on('disconnected', function () {                                           // lost the signalling server only: keep playing and keep retrying so the code stays joinable
      var tries = 0;
      (function again() { if (!S || window.__rwp !== S || p.destroyed || !p.disconnected) return; try { p.reconnect(); } catch (e) {} if (++tries < 60) setTimeout(again, Math.min(10000, 1000 * tries)); })();
    });
    p.on('connection', function (c) {
      var rec = { id: 0, c: c, u: null, gone: false, announced: false };
      var n = 0; for (var k in S.conns) n++;
      if (n >= S.max) {                                                              // room full: 1 host + max visitors (chosen when the match is created)
        c.on('open', function () { try { c.send(new Uint8Array([255, 70, 85, 76, 76, S.max + 1]).buffer); } catch (e) {} setTimeout(function () { try { c.close(); } catch (e) {} }, 400); });
        return;
      }
      rec.id = S.nextId++; S.conns[rec.id] = rec;
      c.on('open', function () {
        try {
          var dc = c.peerConnection.createDataChannel('u', { negotiated: true, id: 5, ordered: false, maxRetransmits: 0 });
          dc.binaryType = 'arraybuffer';
          dc.onmessage = function (ev) { S.q.push({ t: 2, id: rec.id, d: new Uint8Array(ev.data) }); };
          rec.u = dc;
        } catch (e) {}
        rec.announced = true; S.q.push({ t: 1, id: rec.id });
      });
      c.on('data', function (d) {
        var u8 = d instanceof ArrayBuffer ? new Uint8Array(d) : (d && d.buffer ? new Uint8Array(d.buffer, d.byteOffset, d.byteLength) : null);
        if (u8) S.q.push({ t: 2, id: rec.id, d: u8 });
      });
      c.on('close', function () { drop(rec); });
      c.on('error', function (e) { S.err = 'conexión: ' + (e && e.message ? e.message : e); drop(rec); });
    });
  },

  RW_PeerClient: function (hostPtr) {
    var hostId = UTF8ToString(hostPtr);
    (window.__rwpStop = window.__rwpStop || function () {
      var S = window.__rwp; if (!S) return;
      try { if (S.conns) for (var k in S.conns) { try { S.conns[k].c.close(); } catch (e) {} } } catch (e) {}
      try { if (S.srv && S.srv.c) S.srv.c.close(); } catch (e) {}
      try { if (S.peer) S.peer.destroy(); } catch (e) {}
      window.__rwp = null;
    })();
    var S = window.__rwp = { mode: 'client', status: 1, err: '', q: [], srv: null, peer: null, done: false };
    if (typeof Peer === 'undefined') { S.status = -1; S.err = 'PeerJS no se cargó (¿bloqueador o sin conexión?)'; S.q.push({ t: 3, id: 0 }); return; }
    function fail(msg) { if (S.done) return; S.done = true; if (!S.err || msg) S.err = msg || S.err; S.status = -1; S.q.push({ t: 3, id: 0 }); }
    var attempt = 0, MAXTRY = 4, timer = null;
    var p = S.peer = new Peer(RW_ICE_CFG());
    p.on('error', function (e) {
      var t = e && e.type;
      if (S.status === 2 || S.done) return;
      if (t === 'peer-unavailable' && attempt < MAXTRY) { setTimeout(function () { connect(); }, 1500); return; }     // the host may be re-registering with the signalling server: ask again
      if (t === 'network' || t === 'server-error' || t === 'socket-error' || t === 'socket-closed') { if (attempt < MAXTRY) return; }
      fail(t === 'peer-unavailable' ? 'No existe una partida con ese código (o el anfitrión perdió la conexión).' : ((t ? t + ': ' : '') + (e && e.message ? e.message : e)));
    });
    function connect() {
      if (S.done || S.status === 2) return;
      attempt++;
      if (S.srv) { try { S.srv.c.close(); } catch (e) {} S.srv = null; }
      clearTimeout(timer);
      timer = setTimeout(function () {
        if (S.status !== 1 || S.done) return;
        if (attempt < MAXTRY) connect();
        else fail('Tiempo agotado: no se pudo abrir la conexión WebRTC con el anfitrión (¿red móvil que bloquea P2P?).');
      }, 12000);
      var c = p.connect(hostId, { serialization: 'raw', reliable: true });
      var rec = S.srv = { id: 0, c: c, u: null };
      c.on('open', function () {
        clearTimeout(timer);
        try {
          var dc = c.peerConnection.createDataChannel('u', { negotiated: true, id: 5, ordered: false, maxRetransmits: 0 });
          dc.binaryType = 'arraybuffer';
          dc.onmessage = function (ev) { S.q.push({ t: 2, id: 0, d: new Uint8Array(ev.data) }); };
          rec.u = dc;
        } catch (e) {}
        S.status = 2; S.q.push({ t: 1, id: 0 });
      });
      c.on('data', function (d) {
        var u8 = d instanceof ArrayBuffer ? new Uint8Array(d) : (d && d.buffer ? new Uint8Array(d.buffer, d.byteOffset, d.byteLength) : null);
        if (!u8) return;
        if (u8.length >= 5 && u8.length <= 6 && u8[0] === 255 && u8[1] === 70 && u8[2] === 85) { S.err = 'La partida está llena' + (u8.length === 6 ? ' (máx. ' + u8[5] + ' jugadores).' : '.'); return; }
        S.q.push({ t: 2, id: 0, d: u8 });
      });
      c.on('close', function () { if (rec !== S.srv) return; if (S.status !== 2) { return; } if (S.done) return; S.done = true; S.status = -1; S.q.push({ t: 3, id: 0 }); });
      c.on('error', function (e) { if (S.status === 2) { fail('conexión: ' + (e && e.message ? e.message : e)); } });
    }
    p.on('open', function () { connect(); });
  },

  RW_PeerStatus: function () { var S = window.__rwp; return S ? S.status : 0; },

  RW_PeerError: function () {
    var S = window.__rwp, v = S ? (S.err || '') : '';
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },

  RW_PeerCount: function () { var S = window.__rwp, n = 0; if (S && S.conns) for (var k in S.conns) n++; return n; },

  RW_PeerSend: function (id, ptr, len, reliable) {
    var S = window.__rwp; if (!S) return 0;
    var rec = S.mode === 'host' ? S.conns[id] : S.srv;
    if (!rec || !rec.c || !rec.c.open) return 0;
    var buf = new Uint8Array(len); buf.set(HEAPU8.subarray(ptr, ptr + len));          // copy out of the (shared) wasm heap
    try {
      if (!reliable && len <= 1100 && rec.u && rec.u.readyState === 'open') { rec.u.send(buf); return 1; }
      rec.c.send(buf.buffer); return 1;
    } catch (e) { return 0; }
  },

  RW_PeerPoll: function (infoPtr, dataPtr, cap) {
    var S = window.__rwp; if (!S || !S.q.length) return 0;
    var e = S.q.shift(), len = 0;
    if (e.d) { len = Math.min(e.d.length, cap); HEAPU8.set(e.d.subarray(0, len), dataPtr); }
    HEAP32[infoPtr >> 2] = e.id; HEAP32[(infoPtr >> 2) + 1] = len;
    return e.t;
  },

  RW_PeerClose: function (id) {
    var S = window.__rwp; if (!S || !S.conns) return;
    var rec = S.conns[id]; if (rec) { try { rec.c.close(); } catch (e) {} }
  },

  RW_PeerShutdown: function () {
    (window.__rwpStop = window.__rwpStop || function () {
      var S = window.__rwp; if (!S) return;
      try { if (S.conns) for (var k in S.conns) { try { S.conns[k].c.close(); } catch (e) {} } } catch (e) {}
      try { if (S.srv && S.srv.c) S.srv.c.close(); } catch (e) {}
      try { if (S.peer) S.peer.destroy(); } catch (e) {}
      window.__rwp = null;
    })();
  }
});

// Browser-to-browser transport for Netcode (WebRTC through PeerJS). Unity never gets callbacks: JS queues events, C# polls them.
// Event types returned by RW_PeerPoll: 0 none, 1 connect, 2 data, 3 disconnect. Client id 0 = the host.
mergeInto(LibraryManager.library, {

  RW_PeerHost: function (idPtr) {
    var id = UTF8ToString(idPtr);
    (window.__rwpStop = window.__rwpStop || function () {
      var S = window.__rwp; if (!S) return;
      try { if (S.conns) for (var k in S.conns) { try { S.conns[k].c.close(); } catch (e) {} } } catch (e) {}
      try { if (S.srv && S.srv.c) S.srv.c.close(); } catch (e) {}
      try { if (S.peer) S.peer.destroy(); } catch (e) {}
      window.__rwp = null;
    })();
    var S = window.__rwp = { mode: 'host', status: 1, err: '', q: [], nextId: 1, conns: {}, peer: null };
    if (typeof Peer === 'undefined') { S.status = -1; S.err = 'PeerJS no se cargó (¿bloqueador o sin conexión?)'; return; }
    function drop(rec) { if (!rec || rec.gone) return; rec.gone = true; delete S.conns[rec.id]; if (rec.announced) S.q.push({ t: 3, id: rec.id }); try { rec.c.close(); } catch (e) {} }
    var p = S.peer = new Peer(id, { debug: 1 });
    p.on('open', function () { S.status = 2; });
    p.on('error', function (e) {
      S.err = (e && e.type ? e.type + ': ' : '') + (e && e.message ? e.message : e);
      if (S.status === 1) S.status = (e && e.type === 'unavailable-id') ? -2 : -1;
    });
    p.on('disconnected', function () { try { p.reconnect(); } catch (e) {} });     // lost the signalling server only: keep playing
    p.on('connection', function (c) {
      var rec = { id: 0, c: c, u: null, gone: false, announced: false };
      var n = 0; for (var k in S.conns) n++;
      if (n >= 3) {                                                                  // 1 host + 3 visitors
        c.on('open', function () { try { c.send(new Uint8Array([255, 70, 85, 76, 76]).buffer); } catch (e) {} setTimeout(function () { try { c.close(); } catch (e) {} }, 400); });
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
    var p = S.peer = new Peer({ debug: 1 });
    var timer = setTimeout(function () { if (S.status === 1) fail('Tiempo agotado: no se pudo abrir la conexión WebRTC con el anfitrión (¿red móvil que bloquea P2P?).'); }, 25000);
    p.on('error', function (e) {
      var t = e && e.type;
      fail(t === 'peer-unavailable' ? 'No existe una partida con ese código.' : ((t ? t + ': ' : '') + (e && e.message ? e.message : e)));
    });
    p.on('open', function () {
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
        if (u8.length === 5 && u8[0] === 255 && u8[1] === 70 && u8[2] === 85) { S.err = 'La partida está llena (máx. 4 jugadores).'; return; }
        S.q.push({ t: 2, id: 0, d: u8 });
      });
      c.on('close', function () { if (S.done) return; S.done = true; S.status = -1; S.q.push({ t: 3, id: 0 }); });
      c.on('error', function (e) { fail('conexión: ' + (e && e.message ? e.message : e)); });
    });
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

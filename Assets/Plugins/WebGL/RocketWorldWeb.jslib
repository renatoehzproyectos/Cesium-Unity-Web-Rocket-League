mergeInto(LibraryManager.library, {
  RW_GetQueryParam: function (namePtr) {
    var name = UTF8ToString(namePtr), v = '';
    try { v = new URLSearchParams(window.location.search).get(name) || ''; } catch (e) {}
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },
  RW_Prompt: function (msgPtr, defPtr) {
    var v = '';
    try { var r = window.prompt(UTF8ToString(msgPtr), UTF8ToString(defPtr)); v = (r === null || r === undefined) ? UTF8ToString(defPtr) : r; } catch (e) { v = UTF8ToString(defPtr); }
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },
  RW_GetBaseUrl: function () {
    var v = '';
    try { v = window.location.origin + window.location.pathname.replace(/(lite|max)\/(index\.html)?$/, ''); } catch (e) {}   // shared links always point at the main build
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },
  // "deviceMemoryGB;cores;isMobile(0/1);devicePixelRatio;crossOriginIsolated(0/1)"
  RW_GetDeviceInfo: function () {
    var v = '8;4;0;1;1';
    try {
      var mem = navigator.deviceMemory || 8, cores = navigator.hardwareConcurrency || 4;
      var mobile = (/Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent) || (navigator.maxTouchPoints > 1 && /Macintosh/.test(navigator.userAgent))) ? 1 : 0;
      v = mem + ';' + cores + ';' + mobile + ';' + (window.devicePixelRatio || 1) + ';' + (window.crossOriginIsolated ? 1 : 0);
    } catch (e) {}
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },
  // Anti crash flag shared with index.html (which has to decide which build to load before Unity exists). localStorage is the source of
  // truth across the main and /lite/ builds (their PlayerPrefs are separate); the PlayerPrefs value only seeds it the first time.
  RW_AntiCrashSync: function (pref) {
    var v = pref ? 1 : 0;
    try { var s = localStorage.getItem('rw_anticrash'); if (s === '0' || s === '1') v = +s; else localStorage.setItem('rw_anticrash', String(v)); } catch (e) {}
    return v;
  },
  RW_SetAntiCrash: function (on) { try { localStorage.setItem('rw_anticrash', on ? '1' : '0'); } catch (e) {} },
  RW_FlushFS: function () { try { if (typeof FS !== 'undefined' && FS.syncfs) FS.syncfs(false, function (err) { if (err) console.warn('[RocketWorld] syncfs', err); }); } catch (e) {} },
  RW_IsLiteBuild: function () { return window.rwLite ? 1 : 0; },
  RW_IsMaxBuild: function () { return window.rwMax ? 1 : 0; },
  // Size in MB of the wasm heap right now (it only grows). Used by MemoryGuard to back off before the browser refuses to grow it
  // (an allocation that fails shows up as "RuntimeError: memory access out of bounds").
  RW_HeapMB: function () { try { return (HEAPU8.length / 1048576) | 0; } catch (e) { return 0; } },
  RW_HideLoading: function () {
    try { localStorage.setItem('rw_boot', '0'); } catch (e) {}   // first frame reached: this load did not crash
    try { var l = document.getElementById('rw-loading'); if (l) { l.style.opacity = '0'; clearTimeout(window.__rwHideT); window.__rwHideT = setTimeout(function () { l.style.display = 'none'; }, 500); } } catch (e) {}
    try { if (window.rwOnGameReady) window.rwOnGameReady(); } catch (e) {}   // the page starts the first-visit tutorial from here
  },
  // Brings the loading screen (looping video) back, e.g. while the map reloads after a teleport: the jump out of the bus stays locked meanwhile.
  RW_ShowLoading: function () {
    try {
      var l = document.getElementById('rw-loading'); if (!l) return;
      clearTimeout(window.__rwHideT);
      l.style.display = ''; l.style.opacity = '1';
      var v = l.querySelector('video'); if (v) { v.muted = true; var p = v.play(); if (p && p.catch) p.catch(function () {}); }
    } catch (e) {}
  },
  // The page owns the "switch build" dialog (it also asks before an automatic switch): toLite = 1 asks to go to /lite/, 0 asks to go back to the full build.
  RW_AskSwitchBuild: function (toLite) { try { if (window.rwAskSwitch) window.rwAskSwitch(!!toLite); } catch (e) {} },
  RW_ShowTutorial: function () { try { if (window.rwTutorial) window.rwTutorial.open(true); } catch (e) {} },
  // Baked by BuildScript from the ION_TOKEN GitHub secret (window.RW_DEFAULT_ION_TOKEN).
  RW_GetDefaultIonToken: function () {
    var v = '';
    try { v = (typeof window.RW_DEFAULT_ION_TOKEN === 'string') ? window.RW_DEFAULT_ION_TOKEN : ''; } catch (e) {}
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },
  // Reads the first connected gamepad straight from the browser Gamepad API, independent of Unity's input backend.
  // out[0]=connected, out[1..16]=buttons in "standard" order (south,east,west,north,L1,R1,L2,R2,back,start,L3,R3,up,down,left,right), out[17..20]=axes lx,ly,rx,ry (y down+), out[21]=standard mapping
  RW_PollGamepad: function (outPtr, len) {
    var o = outPtr >> 2, i;
    for (i = 0; i < len; i++) HEAPF32[o + i] = 0;
    try {
      var pads = navigator.getGamepads ? navigator.getGamepads() : [], gp = null;
      for (i = 0; i < pads.length; i++) if (pads[i] && pads[i].connected) { gp = pads[i]; break; }
      if (!gp || len < 22) return 0;
      var b = gp.buttons, a = gp.axes, std = gp.mapping === 'standard';
      function bv(k) { return b[k] ? (typeof b[k] === 'object' ? (b[k].value !== undefined && b[k].value > 0 ? b[k].value : (b[k].pressed ? 1 : 0)) : (b[k] ? 1 : 0)) : 0; }
      var idl = (gp.id || '').toLowerCase();
      var ps = !std && (idl.indexOf('054c') >= 0 || idl.indexOf('wireless controller') >= 0 || idl.indexOf('dualshock') >= 0 || idl.indexOf('playstation') >= 0);
      var order = std ? [0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15] : (ps ? [1,2,0,3,4,5,6,7,8,9,10,11,-1,-1,-1,-1] : [0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15]);
      HEAPF32[o] = 1;
      for (i = 0; i < 16; i++) HEAPF32[o + 1 + i] = order[i] < 0 ? 0 : bv(order[i]);
      HEAPF32[o + 17] = a[0] || 0; HEAPF32[o + 18] = a[1] || 0;
      if (std || !ps || a.length < 6) { HEAPF32[o + 19] = a[2] || 0; HEAPF32[o + 20] = a[3] || 0; }
      else { HEAPF32[o + 19] = a[2] || 0; HEAPF32[o + 20] = a[5] || 0; }
      HEAPF32[o + 21] = std ? 1 : 0;
      return 1;
    } catch (e) { return 0; }
  },
  RW_GamepadName: function () {
    var v = '';
    try {
      var pads = navigator.getGamepads ? navigator.getGamepads() : [];
      for (var i = 0; i < pads.length; i++) if (pads[i] && pads[i].connected) { v = pads[i].id + ' [' + (pads[i].mapping || 'non-standard') + ']'; break; }
    } catch (e) {}
    var size = lengthBytesUTF8(v) + 1, buf = _malloc(size);
    stringToUTF8(v, buf, size);
    return buf;
  },

  // Procedural boom made directly with WebAudio. AudioClip.SetData fails in the threaded WebGL build ("Float32Array value must not be shared"
  // inside jsAudioCreateUncompressedSoundClipFromPCM and kills the player), so the explosion sound never goes through a Unity AudioClip.
  RW_PlayBoom: function (full, volume) {
    try {
      var AC = window.AudioContext || window.webkitAudioContext; if (!AC) return;
      var ctx = window.__rwBoomCtx || (window.__rwBoomCtx = new AC());
      if (ctx.state === 'suspended') ctx.resume();
      var rate = ctx.sampleRate, len = full ? 7 : 1.5, n = Math.floor(rate * len);
      var buf = ctx.createBuffer(1, n, rate), d = new Float32Array(n);   // plain (non shared) array
      var seed = 12345, lp = 0, tau = full ? 2.2 : 0.35;
      for (var i = 0; i < n; i++) {
        var t = i / rate, env = Math.exp(-t / tau) * Math.min(1, t * 30);
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        var noise = ((seed >>> 9) / 4194304) - 1; lp += (noise - lp) * 0.02;
        d[i] = (lp * 4 + 0.7 * Math.sin(2 * Math.PI * 32 * t) + 0.4 * Math.sin(2 * Math.PI * 21 * t)) * env * 0.9;
      }
      buf.copyToChannel(d, 0);
      var src = ctx.createBufferSource(), g = ctx.createGain();
      src.buffer = buf; g.gain.value = volume; src.connect(g); g.connect(ctx.destination); src.start();
    } catch (e) { console.warn('RW_PlayBoom: ' + e); }
  }
});

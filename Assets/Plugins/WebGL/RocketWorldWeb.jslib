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
    try { v = window.location.origin + window.location.pathname.replace(/lite\/(index\.html)?$/, ''); } catch (e) {}   // shared links always point at the main build
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
  RW_IsLiteBuild: function () { return window.rwLite ? 1 : 0; },
  RW_HideLoading: function () {
    try { localStorage.setItem('rw_boot', '0'); } catch (e) {}   // first frame reached: this load did not crash
    try { var l = document.getElementById('rw-loading'); if (l) { l.style.opacity = '0'; setTimeout(function () { l.style.display = 'none'; }, 500); } } catch (e) {}
  },
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
  }
});

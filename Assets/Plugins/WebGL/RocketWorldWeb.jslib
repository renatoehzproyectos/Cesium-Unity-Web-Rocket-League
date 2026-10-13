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
    try { v = window.location.origin + window.location.pathname; } catch (e) {}
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
  RW_HideLoading: function () {
    try { var l = document.getElementById('rw-loading'); if (l) { l.style.opacity = '0'; setTimeout(function () { l.style.display = 'none'; }, 500); } } catch (e) {}
  }
});

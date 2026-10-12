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
  RW_HideLoading: function () {
    try { var l = document.getElementById('rw-loading'); if (l) l.style.display = 'none'; } catch (e) {}
  }
});

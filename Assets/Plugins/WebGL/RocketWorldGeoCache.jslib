// Unity <-> saved-zones store (TemplateData/rw-geocache.js). Request/answer by polling, like the gamepad bridge: no callbacks into wasm.
mergeInto(LibraryManager.library, {
  RW_Geo_Available: function () { return (typeof window.RWGeo !== 'undefined' && window.RWGeo.Client.available) ? 1 : 0; },
  RW_Geo_Reason: function () { return _rwGeoStr((window.RWGeo && window.RWGeo.Client.reason) || ''); },
  RW_Geo_Request: function (jsonPtr) { try { window.RWGeo.bridge.request(UTF8ToString(jsonPtr)); } catch (e) { console.warn('RW_Geo_Request', e); } },
  // "" when nothing is waiting. One reusable buffer: polled every frame, so it must not allocate each time.
  RW_Geo_Poll: function () { var s = ''; try { s = window.RWGeo ? window.RWGeo.bridge.poll() : ''; } catch (e) {} return _rwGeoStr(s); },
  RW_Geo_Pending: function () { try { return window.RWGeo ? window.RWGeo.bridge.pending() : 0; } catch (e) { return 0; } },
  rwGeoStr: function (s) {
    var need = lengthBytesUTF8(s) + 1;
    if (!Module._rwGeoBuf || Module._rwGeoSize < need) { if (Module._rwGeoBuf) _free(Module._rwGeoBuf); Module._rwGeoSize = Math.max(need, 4096); Module._rwGeoBuf = _malloc(Module._rwGeoSize); }
    stringToUTF8(s, Module._rwGeoBuf, Module._rwGeoSize);
    return Module._rwGeoBuf;
  },
  RW_Geo_Reason__deps: ['rwGeoStr'],
  RW_Geo_Poll__deps: ['rwGeoStr']
});

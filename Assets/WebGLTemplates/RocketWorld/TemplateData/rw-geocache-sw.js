/*
 * Rocket World - geographic tile store, service worker. All the logic lives in rw-geocache.js (shared with the page and the tests).
 * It only touches GET requests to the tile hosts listed in RWGeo.cfg.hosts; everything else goes straight to the browser untouched.
 * Registered by index.html with the scope of the build (/ for the main player, /lite/ for the low-memory one). Both share one IndexedDB.
 */
importScripts('rw-geocache.js');
var SW_VERSION = 1;

self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (e) { e.waitUntil(self.clients.claim()); });   // control the page that registered us, no reload needed
RWGeo.initChannel();

self.addEventListener('fetch', function (e) {
  var r = e.request;
  if (r.method !== 'GET' || !RWGeo.classOf(r.url)) return;
  // If the store itself fails (IndexedDB blocked, quota...) the tile still arrives, straight from the network.
  e.respondWith(RWGeo.handleFetch(r).catch(function () { return fetch(r); }));
});

self.addEventListener('message', function (e) {
  var port = e.ports && e.ports[0];
  if (!port) return;
  e.waitUntil(RWGeo.handleMessage(e.data).then(
    function (v) { port.postMessage({ ok: true, value: v }); },
    function (err) { port.postMessage({ ok: false, error: String((err && err.message) || err) }); }));
});

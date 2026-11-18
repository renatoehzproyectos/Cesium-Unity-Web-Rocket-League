/*
 * Clean-up stub. An earlier build registered a tile-caching service worker at this URL; that feature was reverted.
 * Browsers keep an old service worker alive when its script disappears, so this file replaces it with one that
 * deletes its IndexedDB store and unregisters itself. Nothing registers it any more: it only runs on devices that
 * still have the old worker. Safe to delete (together with its header rule in vercel.json) after a few weeks.
 */
self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (e) {
  e.waitUntil((async function () {
    try { indexedDB.deleteDatabase('rw-geocache'); } catch (err) { }
    try { var ks = await caches.keys(); await Promise.all(ks.filter(function (k) { return /rw-geocache/.test(k); }).map(function (k) { return caches.delete(k); })); } catch (err) { }
    await self.registration.unregister();
    var cs = await self.clients.matchAll({ type: 'window' });
    cs.forEach(function (c) { try { c.navigate(c.url); } catch (err) { } });     // reload so the page is no longer controlled
  })());
});
// no fetch handler: every request goes straight to the network

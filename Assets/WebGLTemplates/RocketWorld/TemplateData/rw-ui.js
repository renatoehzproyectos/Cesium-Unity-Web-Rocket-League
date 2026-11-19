/* Rocket World - page UI (Spanish).
 *  1) Confirmation dialogs before switching between the full build and /lite/ (index.html asks through rwUi.askBoot, the in-game
 *     settings through window.rwAskSwitch). Nothing ever switches builds without the person saying yes.
 *  2) Skippable first-visit tutorial with animated illustrations (window.rwTutorial.open / rwOnGameReady).
 * Plain DOM + CSS (rw-ui.css), no dependencies. Everything the person sees is Spanish. */
(function () {
  'use strict';

  // ------------------------------------------------------------------------------------------------ helpers
  function ls(k, v) { try { if (v === undefined) return localStorage.getItem(k); if (v === null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch (e) { } return null; }
  function ss(k, v) { try { if (v === undefined) return sessionStorage.getItem(k); sessionStorage.setItem(k, v); } catch (e) { } return null; }
  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function raf(f) { (window.requestAnimationFrame || setTimeout)(f, 16); }
  var isTouch = false;
  try { isTouch = !!(window.matchMedia && matchMedia('(hover: none) and (pointer: coarse)').matches); } catch (e) { }

  // Keys must not reach the game while one of our dialogs is open (WASD would drive the car under the overlay).
  var keyGuards = 0;
  function guardKey(e) {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    var k = e.key;
    if (/^F\d+$/.test(k) || k === 'Tab') return;
    e.stopPropagation();
  }
  function lockKeys() { if (keyGuards++ === 0) { window.addEventListener('keydown', guardKey, true); window.addEventListener('keypress', guardKey, true); } }
  function unlockKeys() { if (--keyGuards <= 0) { keyGuards = 0; window.removeEventListener('keydown', guardKey, true); window.removeEventListener('keypress', guardKey, true); } }

  function fadeOut(ov, done) { ov.classList.remove('in'); setTimeout(function () { if (ov.parentNode) ov.parentNode.removeChild(ov); if (done) done(); }, 380); }

  function pill(text, ms) {
    var p = el('div', 'rwu-pill', text); p.setAttribute('role', 'status'); document.body.appendChild(p);
    raf(function () { p.classList.add('in'); });
    setTimeout(function () { p.classList.remove('in'); setTimeout(function () { if (p.parentNode) p.parentNode.removeChild(p); }, 500); }, ms || 5200);
  }

  // ------------------------------------------------------------------------------------------------ 1) switch dialogs
  var ICON_LITE = '<svg viewBox="0 0 24 24" fill="none" stroke="#04202a" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M13 2 4 14h7l-1 8 9-12h-7z"/></svg>';
  var ICON_FULL = '<svg viewBox="0 0 24 24" fill="none" stroke="#04202a" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>';

  function swTexts(to, reason) {
    if (to === 'lite') {
      var why = {
        ios: 'Parece que juegas desde un iPhone o iPad. En estos dispositivos la versión completa suele cerrarse sola porque se queda sin memoria.',
        crash: 'En tus últimos intentos el juego se cerró antes de empezar. Es probable que a tu dispositivo le falte memoria para la versión completa.',
        saved: 'La última vez el juego tuvo problemas de memoria en este dispositivo. La versión Lite está pensada para evitarlo.',
        manual: 'La versión Lite usa menos memoria y funciona mejor en móviles y equipos modestos.'
      }[reason] || 'La versión Lite usa menos memoria y funciona mejor en móviles y equipos modestos.';
      return {
        title: '¿Quieres usar la versión Lite?', why: why, icon: ICON_LITE, warn: reason === 'crash',
        pros: [['ok', 'Menos cierres inesperados y menos tirones.'], ['ok', 'Carga más rápido en móviles y equipos modestos.'], ['no', 'Se ve con menos detalle gráfico.']],
        yes: 'Sí, usar la versión Lite', no: 'No, quedarme en la versión completa',
        foot: 'Puedes cambiar cuando quieras desde Ajustes.'
      };
    }
    var why2 = reason === 'ac-off'
      ? 'Desactivaste el modo anticierres, así que ya no hace falta la versión Lite. La versión completa se ve mejor, pero usa más memoria.'
      : 'La versión completa tiene más calidad gráfica y más detalle, pero usa más memoria y, en algunos dispositivos, puede cerrarse sola.';
    return {
      title: '¿Salir de la versión Lite?', why: why2, icon: ICON_FULL, warn: false,
      pros: [['ok', 'Mejor calidad gráfica y más detalle.'], ['no', 'Usa más memoria: en móviles puede cerrarse sola.']],
      yes: 'Sí, ir a la versión completa', no: 'No, seguir en Lite',
      foot: 'Puedes volver a Lite cuando quieras desde Ajustes.'
    };
  }

  // Navigates to the other build. The person already said yes, so the boot script treats ?lite=1 / ?lite=0 as an explicit choice.
  function go(to) {
    ls('rw_lite', to === 'lite' ? '1' : '0');
    var L = location, base = L.pathname.replace(/(lite|max)\/(index\.html)?$/, '').replace(/index\.html$/, '');
    var qs = new URLSearchParams(L.search); qs.delete('max'); qs.set('lite', to === 'lite' ? '1' : '0');
    window.rwRedirecting = true;
    var m = document.getElementById('rw-msg');
    if (m) m.textContent = to === 'lite' ? 'Cambiando a la versión Lite…' : 'Cambiando a la versión completa…';
    pill(to === 'lite' ? 'Cambiando a la versión Lite…' : 'Cambiando a la versión completa…', 8000);
    L.assign(base + (to === 'lite' ? 'lite/' : '') + '?' + qs.toString() + L.hash);
  }

  var swOpen = false;
  // opts: {to:'lite'|'main', reason:'ios'|'crash'|'saved'|'ac-off'|'manual'}; cb(yes). Remembering the choice is only offered for automatic suggestions.
  function showSwitch(opts, cb) {
    if (swOpen) return; swOpen = true;
    var t = swTexts(opts.to, opts.reason), auto = opts.reason !== 'manual';
    var pros = ''; for (var i = 0; i < t.pros.length; i++) pros += '<div class="' + t.pros[i][0] + '"><i>' + (t.pros[i][0] === 'ok' ? '✔' : '!') + '</i><span>' + t.pros[i][1] + '</span></div>';
    var ov = el('div', 'rwu-ov');
    ov.setAttribute('role', 'alertdialog'); ov.setAttribute('aria-modal', 'true'); ov.setAttribute('aria-labelledby', 'rwu-sw-t'); ov.setAttribute('aria-describedby', 'rwu-sw-d');
    ov.innerHTML = '<div class="rwu-card">' +
      '<div class="rwu-ico' + (t.warn ? ' warn' : '') + '">' + t.icon + '</div>' +
      '<h2 id="rwu-sw-t">' + t.title + '</h2><p id="rwu-sw-d">' + t.why + '</p>' +
      '<div class="rwu-pc">' + pros + '</div>' +
      '<button type="button" class="rwu-btn pri" data-a="yes">' + t.yes + '</button>' +
      '<button type="button" class="rwu-btn" data-a="no">' + t.no + '</button>' +
      (auto ? '<label class="rwu-rem"><input type="checkbox" id="rwu-rem"> Recordar mi elección en este dispositivo</label>' : '') +
      '<small>' + t.foot + '</small></div>';
    document.body.appendChild(ov);
    lockKeys();
    ov.addEventListener('pointerdown', function (e) { e.stopPropagation(); });
    ov.addEventListener('touchstart', function (e) { e.stopPropagation(); }, { passive: true });
    var finished = false;
    function finish(yes) {
      if (finished) return; finished = true;
      var rem = auto && ov.querySelector('#rwu-rem') && ov.querySelector('#rwu-rem').checked;
      document.removeEventListener('keydown', onKey, true);
      unlockKeys(); swOpen = false;
      if (yes) ls('rw_pref_build', opts.to === 'lite' ? 'lite' : 'main');                     // chose it (by hand or by accepting): that is the preference now
      else if (rem) ls('rw_pref_build', opts.to === 'lite' ? 'main' : 'lite');                // "No" + remember: stay where I am, stop asking
      fadeOut(ov, function () { cb(yes); });
    }
    function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(false); } }
    document.addEventListener('keydown', onKey, true);
    ov.addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('button[data-a]') : null;
      if (b) finish(b.getAttribute('data-a') === 'yes');
    });
    raf(function () { ov.classList.add('in'); var f = ov.querySelector('button[data-a="no"]'); if (f) f.focus({ preventScroll: true }); });   // focus the safe answer: Enter never switches by accident
  }

  window.rwAskSwitch = function (toLite) { showSwitch({ to: toLite ? 'lite' : 'main', reason: 'manual' }, function (yes) { if (yes) go(toLite ? 'lite' : 'main'); }); };

  // Called by index.html when the boot script wants to change build on its own: nothing is downloaded until the person answers.
  function askBoot(ask, cb) {
    var m = document.getElementById('rw-msg'); if (m) m.textContent = 'Esperando tu respuesta…';
    showSwitch({ to: ask.to, reason: ask.reason }, function (yes) {
      if (yes) { go(ask.to); return; }
      ss('rw_decl_' + ask.to, String(ask.tries));    // do not ask again for this build until a load fails
      cb(true);
    });
  }
  window.rwUi = { askBoot: askBoot, go: go };

  // ------------------------------------------------------------------------------------------------ 2) tutorial: art pieces
  var CAR = '<path d="M6 40 L10 28 Q12 23 19 23 L38 23 L50 11 Q53 9 58 9 L88 9 Q94 9 98 15 L108 27 Q114 29 114 36 L114 42 Q114 46 110 46 L10 46 Q6 46 6 42 Z" fill="#ff5a10"/>' +
    '<path d="M53 13 L86 13 L97 26 L44 26 Z" fill="#aef0ff" opacity=".9"/><path d="M8 36 H113" stroke="#c94300" stroke-width="2"/>' +
    '<circle cx="30" cy="47" r="10" fill="#0e131c" stroke="#d5deea" stroke-width="3.5"/><circle cx="92" cy="47" r="10" fill="#0e131c" stroke="#d5deea" stroke-width="3.5"/>';
  var FLAME = '<g class="rwu-flame"><path d="M6 36 L-26 40 L6 44 Z" fill="#ffb020"/><path d="M6 38 L-12 40 L6 42 Z" fill="#fff3c0"/></g>';
  function car(x, y, s, extra) { return '<g transform="translate(' + x + ',' + y + ') scale(' + s + ')">' + (extra || '') + CAR + '</g>'; }
  function roadCar(boost) {
    return '<svg viewBox="0 0 200 120" style="width:50%;height:auto" aria-hidden="true"><rect x="0" y="88" width="200" height="32" fill="#16202e"/>' +
      '<line x1="0" y1="104" x2="200" y2="104" stroke="#cfd8e6" stroke-width="3" class="rwu-road"/>' +
      '<g class="rwu-drive">' + car(42, 34, 0.92, boost ? FLAME : '') + '</g></svg>';
  }
  function stick() {
    return '<svg viewBox="0 0 120 120" style="width:40%;height:auto" aria-hidden="true"><circle cx="60" cy="60" r="50" fill="rgba(255,255,255,.06)" stroke="rgba(255,255,255,.28)" stroke-width="3"/>' +
      '<g class="rwu-knob"><circle cx="60" cy="60" r="22" fill="#3dd6f5" stroke="#fff" stroke-opacity=".6" stroke-width="3"/></g></svg>';
  }
  function row(inner) { return '<div style="position:absolute;inset:0;display:flex;align-items:center;justify-content:space-around;padding:0 18px">' + inner + '</div>'; }
  function key(t, cls) { return '<span class="rwu-key ' + (cls || '') + '">' + t + '</span>'; }
  function rbtn(t, cls) { return '<span class="rwu-key round ' + (cls || '') + '">' + t + '</span>'; }
  function mini(svg, title, sub, style) { return '<div class="rwu-mini"' + (style ? ' style="' + style + '"' : '') + '>' + svg + '<div>' + title + '</div><span>' + sub + '</span></div>'; }
  function keyOrBtn(m, kbdLabel, touchLabel, padLabel, cls) { return m === 'kbd' ? key(kbdLabel, cls) : m === 'touch' ? rbtn(touchLabel, cls) : rbtn(padLabel, cls); }

  var MODE_NAMES = { kbd: 'Teclado', touch: 'Pantalla táctil', pad: 'Mando' };

  function artWelcome() {
    var stars = ''; for (var i = 0; i < 14; i++) stars += '<circle cx="' + ((i * 53 + 17) % 400) + '" cy="' + ((i * 37 + 9) % 110) + '" r="' + (i % 3 ? 1.2 : 1.8) + '" fill="#fff" class="rwu-sparkle" style="animation-delay:' + (i % 5) * 0.35 + 's"/>';
    return '<svg viewBox="0 0 400 196" aria-hidden="true"><defs><radialGradient id="rwg1" cx="35%" cy="30%"><stop offset="0" stop-color="#6fe3ff"/><stop offset="1" stop-color="#14598a"/></radialGradient>' +
      '<clipPath id="rwc1"><circle cx="200" cy="82" r="56"/></clipPath></defs>' + stars +
      '<circle cx="200" cy="82" r="56" fill="url(#rwg1)"/>' +
      '<g clip-path="url(#rwc1)" fill="none" stroke="rgba(255,255,255,.35)" stroke-width="1.6"><g class="rwu-spin" style="transform-origin:200px 82px;transform-box:view-box">' +
      '<ellipse cx="200" cy="82" rx="56" ry="20"/><ellipse cx="200" cy="82" rx="28" ry="56"/><ellipse cx="200" cy="82" rx="56" ry="56"/><ellipse cx="200" cy="82" rx="10" ry="56"/>' +
      '<path d="M150 70 q10 -14 24 -6 q6 10 -6 16 q-14 4 -18 -10z M214 96 q12 -8 24 2 q4 12 -10 14 q-12 -2 -14 -16z" fill="rgba(75,224,168,.7)" stroke="none"/></g></g>' +
      '<rect x="0" y="150" width="400" height="46" fill="#16202e"/><line x1="0" y1="174" x2="400" y2="174" stroke="#cfd8e6" stroke-width="3" class="rwu-road"/>' +
      '<g class="rwu-bob">' + car(160, 106, 0.66) + '</g></svg>';
  }
  function artMove(m) {
    if (m === 'kbd') return row('<div class="rwu-wasd"><span class="rwu-key w">W</span><span class="rwu-key a">A</span><span class="rwu-key s">S</span><span class="rwu-key d">D</span></div>' + roadCar(false));
    if (m === 'touch') return row(stick() + roadCar(false));
    return row('<div style="text-align:center">' + stick() + '<div style="margin-top:6px;display:flex;gap:8px;justify-content:center">' + key('LT', 'wide2') + key('RT', 'wide2') + '</div></div>' + roadCar(false));
  }
  function artJump(m) {
    var k = m === 'kbd' ? key('Espacio', 'wide double') : rbtn(m === 'touch' ? 'SALTO' : 'A', 'double');
    return row('<div>' + k + '</div><svg viewBox="0 0 200 150" style="width:50%;height:auto" aria-hidden="true"><rect x="0" y="116" width="200" height="34" fill="#16202e"/>' +
      '<ellipse cx="96" cy="118" rx="44" ry="5" fill="#000" opacity=".4" class="rwu-shadow"/>' +
      '<g class="rwu-flip">' + car(40, 60, 0.9) + '</g></svg>');
  }
  function artBoost(m) {
    var turbo = '<svg viewBox="0 0 66 50"><rect x="4" y="36" width="58" height="8" rx="4" fill="rgba(255,255,255,.15)"/><rect class="rwu-fill" x="4" y="36" width="58" height="8" rx="4" fill="#ffb020"/>' +
      '<path d="M33 4 C42 14 46 20 40 30 C38 24 35 24 33 28 C31 24 28 24 26 30 C20 20 24 14 33 4Z" fill="#ffb020" class="rwu-blink"/></svg>';
    var drift = '<svg viewBox="0 0 66 50"><path d="M6 44 Q40 44 52 12" fill="none" stroke="#cfd8e6" stroke-width="3" stroke-linecap="round" stroke-dasharray="3 6" class="rwu-road"/>' +
      '<g transform="translate(42,6) rotate(-62 10 8) scale(.2)">' + CAR + '</g></svg>';
    var roll = '<svg viewBox="0 0 66 50"><g class="rwu-spin" style="animation-duration:2.6s;transform-origin:33px 25px;transform-box:view-box"><g transform="translate(12,15) scale(.36)">' + CAR + '</g></g></svg>';
    var hyper = '<svg viewBox="0 0 66 50"><g transform="translate(14,12) scale(.3)">' + FLAME + CAR + '</g><path d="M4 8 L18 8 M2 18 L14 18 M4 28 L16 28" stroke="#8ceaff" stroke-width="3" stroke-linecap="round" class="rwu-blink"/></svg>';
    var items = m === 'kbd' ? [mini(turbo, 'Turbo', 'Shift'), mini(drift, 'Derrapar', 'Ctrl'), mini(roll, 'Rodar en el aire', 'Q · E')]
      : m === 'touch' ? [mini(turbo, 'Turbo', 'Botón TURBO'), mini(roll, 'Rodar en el aire', 'RODAR IZQ / DER'), mini(hyper, 'Hiper turbo', 'Botón HIPER ×6')]
      : [mini(turbo, 'Turbo', 'B'), mini(drift, 'Derrapar', 'X'), mini(roll, 'Rodar en el aire', 'LB · RB')];
    return '<div class="rwu-minis">' + items.join('') + '</div>';
  }
  function artBall(m) {
    var tags = m === 'kbd' ? [key('B'), 'Pelota', key('R'), 'Rescatar'] : m === 'touch' ? ['<span class="rwu-chip on" style="cursor:default;transform:none">Pelota</span>', 'Botón de arriba'] : [rbtn('R3'), 'Pelota', rbtn('Atrás'), 'Rescatar'];
    var tg = ''; for (var i = 0; i < tags.length; i += 2) tg += '<div style="display:flex;align-items:center;gap:8px;font-weight:700;font-size:14px">' + tags[i] + '<span>' + tags[i + 1] + '</span></div>';
    return '<svg viewBox="0 0 400 196" aria-hidden="true"><rect x="0" y="150" width="400" height="46" fill="#16202e"/><line x1="0" y1="174" x2="400" y2="174" stroke="#cfd8e6" stroke-width="3" class="rwu-road"/>' +
      '<ellipse cx="290" cy="156" rx="26" ry="5" fill="#000" opacity=".45" class="rwu-shadow"/>' +
      '<g class="rwu-ball"><circle cx="290" cy="126" r="28" fill="#f3f6fb"/><path d="M290 108 l12 9 -4 14 h-16 l-4 -14z" fill="#243349"/><circle cx="290" cy="126" r="28" fill="none" stroke="#9fb1cc" stroke-width="2"/></g>' +
      '<g class="rwu-bob">' + car(60, 100, 0.8) + '</g></svg>' +
      '<div style="position:absolute;top:12px;left:0;right:0;display:flex;gap:22px;justify-content:center;flex-wrap:wrap">' + tg + '</div>';
  }
  function camScene(kind) {
    if (kind === 0) return '<svg viewBox="0 0 110 70"><rect y="52" width="110" height="18" fill="#16202e"/><g transform="translate(40,18) scale(.42)">' + CAR + '</g><path d="M10 26 h14 l4 -6 h10 v14 h-10 l-4 -6 h-14z" fill="#8ceaff"/></svg>';
    if (kind === 1) return '<svg viewBox="0 0 110 70"><rect width="110" height="70" fill="#16202e"/><path d="M0 35 H110 M55 0 V70" stroke="#2b3b52" stroke-width="2"/><g transform="translate(36,28) scale(.32)">' + CAR + '</g><circle cx="55" cy="12" r="7" fill="#8ceaff"/><path d="M55 19 V28" stroke="#8ceaff" stroke-width="2" stroke-dasharray="2 3"/></svg>';
    return '<svg viewBox="0 0 110 70"><g transform="translate(36,22) scale(.34)">' + CAR + '</g><ellipse cx="55" cy="36" rx="46" ry="22" fill="none" stroke="#8ceaff" stroke-width="2.4" stroke-dasharray="5 6" class="rwu-road"/><circle cx="101" cy="36" r="5" fill="#8ceaff"/></svg>';
  }
  function artCamera() {
    return '<div class="rwu-minis">' + mini(camScene(0), 'Seguir', 'Detrás del vehículo', 'animation-delay:0s').replace('class="rwu-mini"', 'class="rwu-mini rwu-cam"') +
      mini(camScene(1), 'Helicóptero', 'Vista desde arriba', 'animation-delay:2s').replace('class="rwu-mini"', 'class="rwu-mini rwu-cam"') +
      mini(camScene(2), 'Órbita', 'Giras tú la cámara', 'animation-delay:4s').replace('class="rwu-mini"', 'class="rwu-mini rwu-cam"') + '</div>';
  }
  function artVehicles() {
    var names = ['Coche', 'Cessna', 'F-35B', 'F-15E', 'Helicóptero', 'DeLorean', 'Starship', 'Megaliner', 'Antonov An-124', 'Dron FPV'], h = '';
    for (var i = 0; i < names.length; i++) h += '<div style="animation-delay:' + ((i % 5) * 0.5 + (i > 4 ? 0.25 : 0)).toFixed(2) + 's' + (i === 4 ? ';color:#04202a;background:#3dd6f5' : '') + '">' + names[i] + '</div>';
    return '<div class="rwu-vlist" style="width:min(92%,360px);display:grid;grid-template-columns:1fr 1fr;grid-auto-flow:column;grid-template-rows:repeat(5,auto);top:50%;transform:translate(-50%,-50%)">' + h + '</div>';
  }
  function artHud() {
    return '<svg viewBox="0 0 400 196" aria-hidden="true"><rect width="400" height="196" fill="#122338"/><path d="M0 130 L400 100 V196 H0z" fill="#16202e"/>' +
      '<circle cx="56" cy="56" r="38" fill="#0b1626" stroke="rgba(255,255,255,.3)" stroke-width="2.5"/><path d="M20 66 Q50 40 92 50" stroke="#3a5272" stroke-width="5" fill="none"/>' +
      '<path d="M56 62 l-7 12 h14z" fill="#3dd6f5" class="rwu-blink" transform="rotate(-20 56 56)"/><text x="56" y="30" text-anchor="middle" font-size="12" font-weight="800" fill="#3dd6f5">N</text>' +
      '<g fill="rgba(255,255,255,.14)"><rect x="236" y="14" width="30" height="22" rx="11"/><rect x="270" y="14" width="58" height="22" rx="11"/><rect x="332" y="14" width="56" height="22" rx="11"/></g>' +
      '<text x="200" y="146" text-anchor="middle" font-size="40" font-weight="800" fill="#fff">128</text><text x="200" y="164" text-anchor="middle" font-size="12" font-weight="700" fill="#9fb1cc" letter-spacing="2">KM/H</text>' +
      '<rect x="150" y="172" width="100" height="5" rx="3" fill="rgba(255,255,255,.18)"/><rect class="rwu-fill" x="150" y="172" width="100" height="5" rx="3" fill="#ffb020"/>' +
      '<g font-size="11" font-weight="800" fill="#04202a"><circle cx="100" cy="30" r="10" fill="#3dd6f5"/><text x="100" y="34" text-anchor="middle">1</text>' +
      '<circle cx="284" cy="52" r="10" fill="#3dd6f5"/><text x="284" y="56" text-anchor="middle">2</text><circle cx="262" cy="150" r="10" fill="#3dd6f5"/><text x="262" y="154" text-anchor="middle">3</text></g></svg>';
  }
  function artSettings() {
    var sw = ['#ff5a10', '#1a61ff', '#dc1a1a', '#ebedf2', '#0f0f12', '#1ab340'], s = '';
    for (var i = 0; i < sw.length; i++) s += '<span style="width:30px;height:30px;border-radius:50%;background:' + sw[i] + ';display:inline-block;box-shadow:0 0 0 ' + (i === 0 ? 3 : 0) + 'px #3dd6f5;' + (i === 3 ? 'animation:rwu-blink 1.6s infinite' : '') + '"></span>';
    return '<div style="position:absolute;left:50%;top:14px;transform:translateX(-50%);width:min(92%,330px);padding:12px;border-radius:14px;background:rgba(8,14,26,.92);box-shadow:0 0 0 1px rgba(255,255,255,.1);text-align:left">' +
      '<div style="font-size:11px;font-weight:800;letter-spacing:.1em;color:#7388a6;margin-bottom:6px">UBICACIÓN</div>' +
      '<div style="display:flex;gap:8px"><div style="flex:1;height:36px;border-radius:10px;background:rgba(255,255,255,.08);padding:0 12px;display:flex;align-items:center;font-weight:700"><span class="rwu-type" style="display:inline-block">París</span><span class="rwu-blink" style="margin-left:2px">|</span></div>' +
      '<div style="width:52px;height:36px;border-radius:10px;background:#3dd6f5;color:#04202a;font-weight:800;display:flex;align-items:center;justify-content:center">Ir</div></div>' +
      '<div style="font-size:11px;font-weight:800;letter-spacing:.1em;color:#7388a6;margin:12px 0 6px">COLOR DEL COCHE</div><div style="display:flex;gap:8px;justify-content:space-between">' + s + '</div></div>' +
      '<div style="position:absolute;left:50%;bottom:10px;transform:translateX(-50%);padding:7px 14px;border-radius:999px;background:#4be0a8;color:#04202a;font-weight:800;font-size:13px;opacity:0;animation:rwu-vitem 5s infinite;white-space:nowrap">¡Teletransportado!</div>';
  }
  function artMulti() {
    var d = '', cols = ['#ff5a10', '#1a61ff', '#dc1a1a', '#1ab340'], c = ['4', '8', '2', '7'];
    for (var i = 0; i < 4; i++) d += '<span style="animation-delay:' + (i * 0.25) + 's">' + c[i] + '</span>';
    var dots = ''; for (var j = 0; j < 4; j++) dots += '<i style="width:22px;height:22px;border-radius:50%;background:' + cols[j] + ';display:inline-block;animation:rwu-pop .5s ' + (1 + j * .25) + 's both"></i>';
    return '<div style="position:absolute;top:16px;left:0;right:0;text-align:center;font-size:12px;font-weight:800;letter-spacing:.12em;color:#7388a6">CÓDIGO DE LA PARTIDA</div>' +
      '<div class="rwu-code" style="top:12px">' + d + '</div><div style="position:absolute;bottom:14px;left:0;right:0;display:flex;gap:10px;justify-content:center;align-items:center;font-size:12px;color:#9fb1cc;font-weight:700">' + dots + ' hasta 4 jugadores</div>';
  }
  function artLite() {
    return '<svg viewBox="0 0 400 196" aria-hidden="true"><g fill="none" stroke="#9fb1cc" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" opacity=".9"><rect x="50" y="46" width="76" height="52" rx="7"/><path d="M72 114 h32 M88 98 v16"/></g>' +
      '<text x="88" y="140" text-anchor="middle" font-size="14" font-weight="800" fill="#eaf2ff">Completa</text><text x="88" y="158" text-anchor="middle" font-size="11" fill="#9fb1cc">más calidad</text>' +
      '<path d="M312 40 l-26 38 h20 l-4 28 26 -40 h-20z" fill="#ffb020" class="rwu-blink"/><text x="312" y="140" text-anchor="middle" font-size="14" font-weight="800" fill="#eaf2ff">Lite</text><text x="312" y="158" text-anchor="middle" font-size="11" fill="#9fb1cc">más ligera</text>' +
      '<rect x="168" y="62" width="64" height="34" rx="17" fill="rgba(255,255,255,.14)"/><g class="rwu-togg"><circle cx="187" cy="79" r="13" fill="#3dd6f5"/></g>' +
      '<text x="200" y="120" text-anchor="middle" font-size="12" font-weight="700" fill="#8ceaff">Tú decides</text>' +
      '<circle cx="30" cy="26" r="2.4" fill="#fff" class="rwu-sparkle"/><circle cx="372" cy="170" r="2.4" fill="#fff" class="rwu-sparkle" style="animation-delay:.6s"/><circle cx="360" cy="24" r="2" fill="#fff" class="rwu-sparkle" style="animation-delay:1.1s"/></svg>';
  }

  // ------------------------------------------------------------------------------------------------ tutorial: steps
  var TOOLBAR = [
    ['⚙', 'Ajustes', 'Calidad gráfica, buscar un lugar del mundo, color del coche, ayuda y más. También se abre con Esc.'],
    ['Cámara', 'Cámara', 'Cambia el tipo de cámara y ajústala a tu gusto. También puedes cambiarla con la tecla C.'],
    ['Coche ▾', 'Vehículo', 'Elige tu vehículo: coche, avionetas, cazas, helicóptero, DeLorean, Starship, aviones gigantes o un dron FPV.'],
    ['Pelota', 'Pelota', 'Invoca una pelota gigante delante de ti para jugar al fútbol con tu coche.'],
    ['Controles', 'Controles', 'Mira y cambia las teclas, los botones o el mando que usas para jugar.'],
    ['Bot  −  0/3  +', 'Bots', 'Añade rivales controlados por el ordenador. Con − y + eliges cuántos quieres.'],
    ['Multijugador', 'Multijugador', 'Juega con tus amigos: crea una partida o únete con un código de 4 números.']
  ];

  var STEPS = [
    { title: '¡Bienvenido a Rocket World!', ctl: false, art: artWelcome,
      text: function () { return '<p>Conduce un coche estilo <b>Rocket League</b> por ciudades reales de todo el mundo, en 3D.</p><p>Te enseñamos lo básico en menos de un minuto. Puedes <b>saltar el tutorial</b> cuando quieras y volver a verlo desde Ajustes.</p>'; } },
    { title: 'Muévete', ctl: true, art: artMove,
      text: function (m) {
        return m === 'kbd' ? '<p>Usa <b>W A S D</b> (o las <b>flechas</b>) para acelerar, frenar y girar.</p><p>Con un poco de práctica, el coche va donde tú quieras.</p>'
          : m === 'touch' ? '<p>Mueve el <b>joystick de la izquierda</b>: arriba acelera, abajo frena, y a los lados giras.</p><p>Los botones de la derecha son para saltar y usar el turbo.</p>'
          : '<p>El <b>stick izquierdo</b> gira. El gatillo derecho (<b>RT</b>) acelera y el izquierdo (<b>LT</b>) frena o da marcha atrás.</p>';
      } },
    { title: 'Salta y haz volteretas', ctl: true, art: artJump,
      text: function (m) {
        return m === 'kbd' ? '<p>Pulsa <b>Espacio</b> para saltar. Púlsalo <b>otra vez en el aire</b>, mientras pulsas una dirección, para dar una voltereta.</p>'
          : m === 'touch' ? '<p>Toca <b>SALTO</b>. Tócalo <b>otra vez en el aire</b> para dar una voltereta.</p>'
          : '<p>Pulsa <b>A</b> para saltar y otra vez <b>en el aire</b> para hacer una voltereta.</p>';
      } },
    { title: 'Turbo, derrape y giros', ctl: true, art: artBoost,
      text: function (m) {
        return m === 'kbd' ? '<p>El turbo es <b>infinito</b>: mantén Shift cuando quieras. Con <b>H</b> activas el hiper turbo (+500 % de velocidad).</p>'
          : m === 'touch' ? '<p>El <b>TURBO</b> no se acaba nunca. <b>HIPER ×6</b> se queda activado: tócalo otra vez para apagarlo.</p>'
          : '<p>El turbo es <b>infinito</b>. La <b>cruceta hacia arriba</b> activa el hiper turbo (+500 % de velocidad).</p>';
      } },
    { title: 'Pelota y rescate', ctl: true, art: artBall,
      text: function (m) {
        return m === 'kbd' ? '<p>Pulsa <b>B</b> (o el botón <b>Pelota</b>) para invocar una pelota gigante y empujarla con tu coche.</p><p>¿Te atascaste o volcaste? Pulsa <b>R</b> y te rescatamos.</p>'
          : m === 'touch' ? '<p>Toca el botón <b>Pelota</b>, arriba a la derecha, para invocar una pelota gigante y empujarla con tu coche.</p>'
          : '<p>Presiona el <b>stick derecho (R3)</b> para invocar una pelota gigante. Si te atascas, <b>Atrás / Select</b> te rescata.</p>';
      } },
    { title: 'Cambia de cámara', ctl: false, art: artCamera,
      text: function () { return '<p>Pulsa <b>C</b> (o <b>Y</b> en el mando) para alternar entre las tres cámaras. En la cámara <b>Órbita</b> la mueves arrastrando con el ratón o con el dedo.</p><p>El botón <b>Cámara</b> abre ajustes finos.</p>'; } },
    { title: 'Elige tu vehículo', ctl: false, art: artVehicles,
      text: function () { return '<p>Toca el <b>nombre del vehículo</b> (arriba, junto a Cámara) para cambiar de uno a otro. También puedes pulsar <b>V</b> para pasar al siguiente.</p><p>Cada uno se maneja distinto: ¡prueba todos!</p>'; } },
    { title: 'La barra de botones', ctl: false, art: null, mount: 'toolbar',
      text: function () { return '<p>Arriba a la derecha tienes todo a mano. <b>Toca un botón</b> de arriba para saber qué hace.</p>'; } },
    { title: 'Lo que ves en pantalla', ctl: false, art: artHud,
      text: function () { return '<div class="rwu-callouts"><div><b>1</b><span><strong>Minimapa:</strong> toca el mapa para alternar entre «gira contigo» y «norte arriba».</span></div>' +
        '<div><b>2</b><span><strong>Botones:</strong> los de la barra de arriba.</span></div><div><b>3</b><span><strong>Velocidad</strong> en km/h y barra de velocidad: se vuelve naranja con el turbo.</span></div></div>'; } },
    { title: 'Viaja a cualquier lugar', ctl: false, art: artSettings,
      text: function () { return '<p>Abre <b>Ajustes</b> (⚙ o <b>Esc</b>), escribe una ciudad, una dirección o un lugar famoso y pulsa <b>Ir</b>: aparecerás allí al instante.</p><p>Ahí también cambias el <b>color del coche</b> y la <b>calidad gráfica</b>.</p>'; } },
    { title: 'Juega con amigos', ctl: false, art: artMulti,
      text: function () { return '<p>Pulsa <b>Multijugador</b> → <b>Crear partida</b> y comparte el código de 4 números. Tus amigos eligen <b>Unirse a partida</b> y lo escriben.</p><p>Máximo 4 jugadores. Quien crea la partida debe dejar su pestaña abierta.</p>'; } },
    { title: 'Si va lento: versión Lite', ctl: false, art: artLite, last: true,
      text: function () { return '<p>Si el juego va lento o la página se cierra sola, prueba la <b>versión Lite</b>: gasta menos memoria. Se cambia en <b>Ajustes → Gráficos</b>.</p><p><b>Siempre te preguntamos antes</b> de cambiar de versión, y puedes volver cuando quieras.</p>'; } }
  ];

  // ------------------------------------------------------------------------------------------------ tutorial: engine
  var T = { ov: null, i: 0, mode: isTouch ? 'touch' : 'kbd', cleanup: null, startX: null, onKey: null };

  function mountToolbar(box) {
    var cur = 0, auto = true, timer = null;
    box.innerHTML = '<div class="rwu-chips"></div><div class="rwu-chipdesc" aria-live="polite"></div>';
    var chips = box.querySelector('.rwu-chips'), desc = box.querySelector('.rwu-chipdesc'), btns = [];
    function sel(i) {
      cur = i;
      for (var k = 0; k < btns.length; k++) { btns[k].className = 'rwu-chip' + (k === i ? ' on' : ''); btns[k].setAttribute('aria-pressed', k === i ? 'true' : 'false'); }
      desc.style.animation = 'none'; void desc.offsetWidth; desc.style.animation = '';
      desc.innerHTML = '<b style="color:#8ceaff">' + TOOLBAR[i][1] + '.</b> ' + TOOLBAR[i][2];
    }
    TOOLBAR.forEach(function (it, i) {
      var b = el('button', 'rwu-chip', it[0]); b.type = 'button';
      b.addEventListener('click', function () { auto = false; sel(i); });
      chips.appendChild(b); btns.push(b);
    });
    sel(0);
    timer = setInterval(function () { if (auto) sel((cur + 1) % TOOLBAR.length); }, 3200);
    return function () { clearInterval(timer); };
  }

  function render(dir) {
    var s = STEPS[T.i], n = STEPS.length, ov = T.ov, card = ov.querySelector('.rwu-card');
    if (T.cleanup) { T.cleanup(); T.cleanup = null; }
    var seg = '';
    if (s.ctl) {
      seg = '<div class="rwu-seg" role="group" aria-label="Tipo de control">';
      ['kbd', 'touch', 'pad'].forEach(function (k) { seg += '<button type="button" data-m="' + k + '" class="' + (T.mode === k ? 'on' : '') + '" aria-pressed="' + (T.mode === k) + '">' + MODE_NAMES[k] + '</button>'; });
      seg += '</div>';
    }
    var dots = ''; for (var d = 0; d < n; d++) dots += '<i class="' + (d === T.i ? 'on' : '') + '"></i>';
    card.innerHTML =
      '<div class="rwu-top"><span class="rwu-count">Paso ' + (T.i + 1) + ' de ' + n + '</span><button type="button" class="rwu-skip" data-a="skip">Saltar tutorial ✕</button></div>' +
      '<div class="rwu-prog" aria-hidden="true"><div style="width:' + Math.round(((T.i + 1) / n) * 100) + '%"></div></div>' +
      seg +
      '<div class="rwu-body' + (dir < 0 ? ' back' : '') + '"><div class="rwu-art" aria-hidden="true">' + (s.art ? s.art(T.mode) : '') + '</div>' +
      '<div class="rwu-txt"><h2 id="rwu-tut-t">' + s.title + '</h2>' + s.text(T.mode) + '</div></div>' +
      '<div class="rwu-nav">' + (T.i > 0 ? '<button type="button" class="rwu-btn back" data-a="back" aria-label="Paso anterior">←</button>' : '') +
      '<div class="rwu-dots" aria-hidden="true">' + dots + '</div>' +
      '<button type="button" class="rwu-btn pri next" data-a="next">' + (s.last ? '¡A jugar!' : T.i === 0 ? 'Empezar' : 'Siguiente') + '</button></div>' +
      '<div class="rwu-hint">' + (isTouch ? 'Desliza para cambiar de paso' : 'Usa ← → para moverte · Esc para saltar') + '</div>';
    if (s.mount === 'toolbar') T.cleanup = mountToolbar(card.querySelector('.rwu-art'));
    var nx = card.querySelector('.next'); if (nx) nx.focus({ preventScroll: true });
    card.scrollTop = 0;
  }

  function step(dir) {
    var j = T.i + dir;
    if (j < 0) return;
    if (j >= STEPS.length) { close(true); return; }
    T.i = j; render(dir);
  }

  function close(done) {
    var ov = T.ov; if (!ov) return;
    T.ov = null;
    if (T.cleanup) { T.cleanup(); T.cleanup = null; }
    document.removeEventListener('keydown', T.onKey, true);
    unlockKeys();
    ls('rw_tut_done', '1');
    fadeOut(ov, function () { try { var c = document.getElementById('unity-canvas'); if (c) c.focus(); } catch (e) { } });
    if (!done) pill('Puedes ver el tutorial otra vez en Ajustes → Ayuda y acerca de.', 5600);
  }

  function open(force) {
    if (T.ov) return;
    if (!force && ls('rw_tut_done') === '1') return;
    T.i = 0; T.mode = isTouch ? 'touch' : 'kbd';
    var ov = el('div', 'rwu-ov rwu-tut'); ov.setAttribute('role', 'dialog'); ov.setAttribute('aria-modal', 'true'); ov.setAttribute('aria-labelledby', 'rwu-tut-t');
    ov.innerHTML = '<div class="rwu-card"></div>';
    document.body.appendChild(ov); T.ov = ov;
    lockKeys();
    ov.addEventListener('pointerdown', function (e) { e.stopPropagation(); });
    ov.addEventListener('click', function (e) {
      var t = e.target.closest ? e.target.closest('button[data-a],button[data-m]') : null; if (!t) return;
      if (t.getAttribute('data-m')) { T.mode = t.getAttribute('data-m'); render(0); var b = ov.querySelector('.rwu-seg .on'); if (b) b.focus({ preventScroll: true }); return; }
      var a = t.getAttribute('data-a');
      if (a === 'next') step(1); else if (a === 'back') step(-1); else if (a === 'skip') close(false);
    });
    ov.addEventListener('touchstart', function (e) { e.stopPropagation(); T.startX = e.touches.length === 1 ? e.touches[0].clientX : null; }, { passive: true });
    ov.addEventListener('touchend', function (e) {
      if (T.startX == null) return;
      var dx = (e.changedTouches[0] ? e.changedTouches[0].clientX : T.startX) - T.startX; T.startX = null;
      if (Math.abs(dx) > 60) step(dx < 0 ? 1 : -1);
    }, { passive: true });
    T.onKey = function (e) {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.key === 'ArrowRight') { e.preventDefault(); e.stopPropagation(); step(1); }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); e.stopPropagation(); step(-1); }
      else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(false); }
      else if ((e.key === 'Enter' || e.key === ' ') && !ov.contains(document.activeElement)) { e.preventDefault(); e.stopPropagation(); step(1); }
    };
    document.addEventListener('keydown', T.onKey, true);
    render(1);
    raf(function () { ov.classList.add('in'); });
  }

  window.rwTutorial = { open: open, close: function () { close(false); } };

  // The game calls this (RW_HideLoading) when the first frame is ready: show the tutorial once, to first-time visitors.
  window.rwOnGameReady = function () {
    var q = new URLSearchParams(location.search).get('tutorial');
    if (q === '0' || window.rwCrashed) return;
    if (q === '1' || ls('rw_tut_done') !== '1') setTimeout(function () { open(q === '1'); }, 1500);
  };
})();

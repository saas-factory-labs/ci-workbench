// Theme bootstrap for CIWorkbench.Components. Load it as a plain, synchronous <script> in <head>, before the stylesheets:
// it resolves Light / Dark / System and stamps <html data-theme> before the first paint, so there is no wrong-theme flash.
// It is an external file (not inline) so a strict Content-Security-Policy with script-src 'self' needs no nonce or hash.
// Stored value: localStorage "ciw:theme" = "light" | "dark" | "system" (non-sensitive UI preference, never a credential). Absent = system.
(function () {
  var KEY = 'ciw:theme';
  var root = document.documentElement;
  var mq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  var listener = null;       // .NET callback that keeps the Blazor theme state in step with another tab or the OS

  function read() {
    try { var v = localStorage.getItem(KEY); if (v === 'light' || v === 'dark' || v === 'system') return v; } catch (e) { /* storage blocked */ }
    return 'system';
  }
  function resolve(pref) { return pref === 'system' ? (mq && mq.matches ? 'dark' : 'light') : pref; }

  function apply(pref) {
    var theme = resolve(pref);
    root.setAttribute('data-theme', theme);
    root.setAttribute('data-theme-pref', pref);
    root.style.colorScheme = theme;
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute('content', theme === 'dark' ? '#0e1218' : '#ffffff');
    return theme;
  }

  var current = read();
  apply(current);

  // Follow the operating system while "System" is selected.
  var onOs = function () { if (current === 'system') { var t = apply('system'); if (listener) listener.invokeMethodAsync('OnThemeResolved', t).catch(function () { }); } };
  if (mq) { if (mq.addEventListener) mq.addEventListener('change', onOs); else if (mq.addListener) mq.addListener(onOs); }

  // Another tab changed the preference.
  window.addEventListener('storage', function (e) {
    if (e.key !== KEY) return;
    current = read(); apply(current);
    if (listener) listener.invokeMethodAsync('OnThemePreference', current).catch(function () { });
  });

  // Blazor enhanced navigation can patch <html>; put the attributes back if anything removes them.
  if (window.MutationObserver) {
    new MutationObserver(function () {
      if (root.getAttribute('data-theme') !== resolve(current) || root.getAttribute('data-theme-pref') !== current) apply(current);
    }).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-theme-pref'] });
  }

  window.ciwTheme = {
    get: function () { return current; },
    resolved: function () { return resolve(current); },
    set: function (pref) {
      if (pref !== 'light' && pref !== 'dark' && pref !== 'system') pref = 'system';
      current = pref;
      try { localStorage.setItem(KEY, pref); } catch (e) { /* storage blocked: the choice still applies for this page view */ }
      return apply(pref);
    },
    subscribe: function (ref) { listener = ref; },
    unsubscribe: function () { listener = null; },
  };
})();

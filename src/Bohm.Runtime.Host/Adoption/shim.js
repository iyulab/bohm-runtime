// The runtime's own script, injected ahead of an adopted application's markup after the storage
// channel's script (which replaces window.localStorage and defines window.__bohm) and before problem
// reporting. It adds what only this runtime does: usage facts the page alone can see, and AI provider
// calls relayed through the runtime. The application's own code is not modified.
//
// This file must stay ASCII: it is spliced into documents of any ASCII-compatible encoding.
(function () {
  "use strict";
  var boot = __BOHM_BOOT__;
  // The page may replace fetch (this script does, below); the runtime's own traffic uses the original.
  var nativeFetch = window.fetch.bind(window);

  function usage(body) {
    body.tab = boot.tab;
    nativeFetch("/__bohm/usage", {
      method: "POST",
      credentials: "same-origin",
      keepalive: true,
      headers: { "Content-Type": "application/json", "X-Bohm-Request": "1" },
      body: JSON.stringify(body)
    }).catch(function () { /* usage is best-effort */ });
  }

  // The first input. It carries nothing the person typed.
  function onFirstInput() {
    window.removeEventListener("keydown", onFirstInput, true);
    window.removeEventListener("pointerdown", onFirstInput, true);
    usage({ kind: "input" });
  }
  window.addEventListener("keydown", onFirstInput, true);
  window.addEventListener("pointerdown", onFirstInput, true);

  // How this page's reads match the data it was given: keys asked for that the data does not have
  // (and that this page has not written), and given keys never read. A revision whose data shape
  // changed shows both. Counts only reach the runtime, never key names. Listing the keys counts as
  // reading them all. The storage channel's localStorage is wrapped, not replaced: every call still
  // goes to it.
  var store = window.localStorage;
  var seeded = new Set(Object.keys(store));
  var readSeeded = new Set();
  var missingReads = new Set();
  var written = new Set();
  function noteRead(key) {
    key = String(key);
    if (store.getItem(key) !== null) { if (seeded.has(key)) readSeeded.add(key); }
    else if (!written.has(key)) missingReads.add(key);
  }
  function noteListed() { seeded.forEach(function (k) { readSeeded.add(k); }); }
  function noteWritten(key) { written.add(String(key)); }

  var methods = {
    getItem: function (key) { noteRead(key); return store.getItem(key); },
    setItem: function (key, value) { noteWritten(key); return store.setItem(key, value); },
    removeItem: function (key) { noteWritten(key); return store.removeItem(key); },
    clear: function () { Object.keys(store).forEach(noteWritten); return store.clear(); },
    key: function (index) { noteListed(); return store.key(index); }
  };
  var tracked = new Proxy(store, {
    // A property read counts as reading a stored key, but a missing name is not counted: libraries probe
    // arbitrary names (toJSON, then) that were never the application's keys.
    get: function (target, name) {
      if (typeof name === "string" && Object.prototype.hasOwnProperty.call(methods, name)) return methods[name];
      if (typeof name !== "string" || name === "length" || name in Object.prototype) return target[name];
      var value = target[name];
      if (value !== null && value !== undefined) noteRead(name);
      return value;
    },
    set: function (target, name, value) { noteWritten(name); target[name] = value; return true; },
    deleteProperty: function (target, name) { noteWritten(name); return delete target[name]; },
    ownKeys: function (target) { noteListed(); return Reflect.ownKeys(target); }
  });
  Object.defineProperty(window, "localStorage", { value: tracked, configurable: true, enumerable: true });

  // One report per page, a few seconds after load, when the application has read what it needs to
  // draw itself. Only when there was data to read.
  window.addEventListener("load", function () {
    setTimeout(function () {
      if (seeded.size === 0) return;
      var unread = 0;
      seeded.forEach(function (k) { if (!readSeeded.has(k)) unread++; });
      usage({ kind: "keys", missing: missingReads.size, unread: unread, seeded: seeded.size });
    }, 5000);
  });

  // AI provider calls. An application that asks for a provider's API key with prompt() gets a
  // placeholder instead of a dialog; its requests to a known provider go to the same origin, where
  // the runtime puts the real key in and relays them. The key itself never enters the page.
  var llmProvider = /\b(gemini|google ai|anthropic|claude|openai|gpt|openrouter|groq|mistral)\b/i;
  var keyWord = /api[\s_-]?key/i;
  var nativePrompt = window.prompt;
  window.prompt = function (message) {
    var text = String(message === undefined ? "" : message);
    if (llmProvider.test(text) && keyWord.test(text) && !/github/i.test(text)) return boot.llmPlaceholder;
    return nativePrompt.apply(window, arguments);
  };

  // An application written for the organization's model server calls its address directly; that
  // address is on another host, so the call goes through the runtime too, which adds the server's key.
  function toProxy(url) {
    try {
      var parsed = new URL(url, location.href);
      if (parsed.protocol === "https:" && boot.llmHosts.indexOf(parsed.hostname.toLowerCase()) >= 0)
        return location.origin + "/__bohm/llm/" + parsed.hostname.toLowerCase() + parsed.pathname + parsed.search;
      var plain = parsed.origin + parsed.pathname;
      if (boot.companyBase && (plain + "/").indexOf(boot.companyBase) === 0)
        return location.origin + "/__bohm/llm/company-model/" + plain.substring(boot.companyBase.length) + parsed.search;
    } catch (e) { /* not a URL; leave it alone */ }
    return null;
  }

  window.fetch = function (input, init) {
    var url = typeof input === "string" ? input : input instanceof URL ? input.href : input && input.url;
    var proxied = url ? toProxy(url) : null;
    if (!proxied) return nativeFetch(input, init);
    return nativeFetch(input instanceof Request ? new Request(proxied, input) : proxied, init);
  };

  var nativeOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url) {
    var proxied = toProxy(String(url));
    if (proxied) arguments[1] = proxied;
    return nativeOpen.apply(this, arguments);
  };
})();

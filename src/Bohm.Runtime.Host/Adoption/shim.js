// Injected ahead of an adopted application's own markup. It replaces window.localStorage with a
// synchronous in-memory map seeded from the runtime's snapshot, and forwards every change to the
// runtime, which journals it durably. The application's own code is not modified.
//
// Delivery: at most one request is in flight. Every operation carries a per-tab sequence number
// and stays "unacknowledged" until the runtime confirms it; the runtime ignores numbers it has
// already applied, so resending is always safe. When the page is being hidden or unloaded, every
// unacknowledged operation is resent with keepalive, so a request still in flight cannot be
// overtaken by a later one.
//
// This file must stay ASCII: it is spliced into documents of any ASCII-compatible encoding.
(function () {
  "use strict";
  var boot = __BOHM_BOOT__;
  var data = new Map(Object.keys(boot.items).map(function (k) { return [k, boot.items[k]]; }));
  var endpoint = "/__bohm/storage";
  var seq = 0;
  var unacked = [];   // sent, awaiting acknowledgement, in sequence order
  var queue = [];     // recorded, not yet sent
  var inflight = false;
  var timer = 0;
  var retryDelay = 250;
  // The page may replace fetch (this script does, below); the runtime's own traffic uses the original.
  var nativeFetch = window.fetch.bind(window);

  function post(ops, keepalive) {
    return nativeFetch(endpoint, {
      method: "POST",
      credentials: "same-origin",
      keepalive: keepalive,
      headers: { "Content-Type": "application/json", "X-Bohm-Request": "1" },
      body: JSON.stringify({ tab: boot.tab, ops: ops })
    }).then(function (response) {
      if (!response.ok) throw new Error("storage request failed: " + response.status);
      return response.json();
    }).then(function (result) {
      unacked = unacked.filter(function (op) { return op.seq > result.ack; });
      return result;
    });
  }

  // Sends everything not yet acknowledged, one request at a time. On failure the operations stay
  // unacknowledged and are sent again after a growing delay.
  function pump() {
    timer = 0;
    if (inflight) return;
    var ops = unacked.concat(queue);
    if (ops.length === 0) return;
    unacked = ops;
    queue = [];
    inflight = true;
    var failed = false;
    post(ops.slice(), false).then(function () {
      retryDelay = 250;
    }, function () {
      failed = true;
      retryDelay = Math.min(retryDelay * 2, 5000);
    }).then(function () {
      inflight = false;
      if (unacked.length > 0 || queue.length > 0) schedule(failed ? retryDelay : 0);
    });
  }

  function schedule(delay) {
    if (!timer) timer = setTimeout(pump, delay);
  }

  function flushOnLeave() {
    var ops = unacked.concat(queue);
    if (ops.length === 0) return;
    unacked = ops;
    queue = [];
    // Browsers cap keepalive bodies at 64 KiB. A larger final batch goes as a normal request,
    // which survives a closing window but not an ending process; the host's shutdown ordering
    // is what covers that case.
    var keepalive = JSON.stringify(ops).length < 60000;
    post(ops.slice(), keepalive).catch(function () { /* nothing left to retry with */ });
  }

  function record(op) {
    op.seq = ++seq;
    queue.push(op);
    schedule(0);
  }

  var storage = {
    getItem: function (key) { key = String(key); return data.has(key) ? data.get(key) : null; },
    setItem: function (key, value) {
      key = String(key); value = String(value);
      data.set(key, value);
      record({ op: "set", key: key, value: value });
    },
    removeItem: function (key) {
      key = String(key);
      if (!data.has(key)) return;
      data.delete(key);
      record({ op: "remove", key: key });
    },
    clear: function () {
      if (data.size === 0) return;
      data.clear();
      record({ op: "clear" });
    },
    key: function (index) {
      var keys = Array.from(data.keys());
      return index >= 0 && index < keys.length ? keys[index] : null;
    }
  };
  // configurable: a Proxy must report every non-configurable own property from ownKeys.
  Object.defineProperty(storage, "length", { get: function () { return data.size; }, configurable: true });

  // Property-style access (localStorage.foo = "bar", localStorage.foo, delete localStorage.foo,
  // Object.keys(localStorage)) behaves like the Web Storage API's named properties.
  var proxy = new Proxy(storage, {
    get: function (target, name) { return name in target ? target[name] : (typeof name === "string" ? storage.getItem(name) : undefined); },
    set: function (target, name, value) { storage.setItem(name, value); return true; },
    has: function (target, name) { return name in target || data.has(String(name)); },
    deleteProperty: function (target, name) { storage.removeItem(name); return true; },
    ownKeys: function () { return Array.from(data.keys()); },
    getOwnPropertyDescriptor: function (target, name) {
      return data.has(String(name)) ? { value: data.get(String(name)), enumerable: true, configurable: true, writable: true } : undefined;
    }
  });

  Object.defineProperty(window, "localStorage", { value: proxy, configurable: true, enumerable: true });

  // Usage facts only the page can see: the first input, and failures while loading. Neither
  // carries anything the person typed; a load failure carries the error message, which the
  // runtime keeps in memory to show the person and never writes down.
  function report(kind, message, category, host) {
    nativeFetch("/__bohm/usage", {
      method: "POST",
      credentials: "same-origin",
      keepalive: true,
      headers: { "Content-Type": "application/json", "X-Bohm-Request": "1" },
      body: JSON.stringify({ tab: boot.tab, kind: kind, message: message, category: category, host: host })
    }).catch(function () { /* usage is best-effort */ });
  }

  function onFirstInput() {
    window.removeEventListener("keydown", onFirstInput, true);
    window.removeEventListener("pointerdown", onFirstInput, true);
    report("input");
  }
  window.addEventListener("keydown", onFirstInput, true);
  window.addEventListener("pointerdown", onFirstInput, true);

  // What stopped the application, in terms a person can be told. The runtime receives facts only:
  // which kind of thing was blocked and from which host, or which error was thrown.
  //  - blocked: the content security policy refused something from another host. A script, style
  //    sheet, font or module is a "library" the application needs; anything fetched or displayed is
  //    "data". This also catches an inline module whose import comes from a CDN, which raises no
  //    useful error of its own.
  //  - load-error: the application's own code failed while loading.
  // Files the original site served next to the page (a relative "footer.js") are the host's to
  // report: it answers those requests itself.
  var reported = {};
  var reports = 0;
  function reportOnce(key, kind, message, category, host) {
    if (reported[key] || reports >= 20) return;
    reported[key] = true;
    reports++;
    report(kind, message, category, host);
  }

  var blockedUrls = {};
  document.addEventListener("securitypolicyviolation", function (event) {
    var directive = String(event.effectiveDirective || event.violatedDirective || "");
    var uri = String(event.blockedURI || "");
    if (!/^https?:/i.test(uri)) return; // inline, eval: allowed by policy, so not a loss
    blockedUrls[uri] = true;
    var host;
    try { host = new URL(uri).host; } catch (e) { return; }
    var category = /^(script|style|font|worker|manifest)-src/.test(directive) ? "library"
      : /^form-action/.test(directive) ? "form" : "data";
    reportOnce("blocked " + category + " " + host, "blocked", undefined, category, host);
  });

  var loading = true;
  function onLoadError(message) {
    if (!loading || !message) return;
    reportOnce("error " + message, "load-error", String(message).slice(0, 500));
  }
  // Capturing on window also sees resources that failed to load, which do not bubble.
  window.addEventListener("error", function (event) {
    var target = event.target;
    if (target && target !== window) {
      var url = target.src || target.href;
      if (!url) return;
      try { if (new URL(url, location.href).origin === location.origin) return; } catch (e) { return; }
      // The policy's violation event arrives after the element's error event; wait for it, so a
      // blocked library is reported once, as blocked.
      setTimeout(function () { if (!blockedUrls[url]) onLoadError("could not load " + url); }, 250);
      return;
    }
    if (!event.message) return;
    // Line numbers in the document count this script too; report them as the person's file has them.
    var line = event.lineno && event.filename === location.href ? event.lineno - boot.lineOffset : event.lineno;
    onLoadError(event.message + (line > 0 ? " (line " + line + ")" : ""));
  }, true);
  window.addEventListener("unhandledrejection", function (event) {
    var reason = event.reason;
    onLoadError(reason && reason.message ? reason.message : String(reason));
  });
  window.addEventListener("load", function () { setTimeout(function () { loading = false; }, 1000); });

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

  function toProxy(url) {
    try {
      var parsed = new URL(url, location.href);
      if (parsed.protocol === "https:" && boot.llmHosts.indexOf(parsed.hostname.toLowerCase()) >= 0)
        return location.origin + "/__bohm/llm/" + parsed.hostname.toLowerCase() + parsed.pathname + parsed.search;
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

  window.addEventListener("pagehide", flushOnLeave);
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "hidden") flushOnLeave();
  });
})();

/* Turns the plain `Star <repo>` links into a GitHub-style star button with a
   live stargazer count. Progressive enhancement only: with this file blocked,
   or api.github.com unreachable, the markup in the page is already a working
   link to the repo and nothing below runs long enough to break that.

   Unauthenticated api.github.com allows 60 requests per hour per IP, so a 403
   is an ordinary outcome, not a bug. Every failure path ends the same way:
   the count chip is never inserted and the link is left exactly as shipped. */
(function () {
  "use strict";

  var API = "https://api.github.com/repos/";
  var CACHE_KEY = "magictray.stars.v1";
  /* A count that moves by one does not need to be fresh; a page-to-page walk
     does need to stop asking. Misses are cached for a shorter window so a
     rate-limited visitor recovers inside the same session. */
  var TTL_OK = 30 * 60 * 1000;
  var TTL_MISS = 5 * 60 * 1000;

  var STAR_PATH =
    "M8 .6 L9.82 5.49 L15.04 5.71 L10.95 8.96 L12.35 13.99 L8 11.1 " +
    "L3.65 13.99 L5.05 8.96 L.96 5.71 L6.18 5.49 Z";

  /* One promise per repo for the lifetime of the page, so ten buttons for the
     same repo make one request rather than ten. */
  var inFlight = {};

  function readCache() {
    try {
      var raw = window.sessionStorage.getItem(CACHE_KEY);
      var parsed = raw ? JSON.parse(raw) : null;
      return parsed && typeof parsed === "object" ? parsed : {};
    } catch (e) {
      /* Private mode, or a storage quota of zero: run without a cache. */
      return {};
    }
  }

  function writeCache(repo, count) {
    try {
      var all = readCache();
      all[repo] = { n: typeof count === "number" ? count : null, t: Date.now() };
      window.sessionStorage.setItem(CACHE_KEY, JSON.stringify(all));
    } catch (e) {
      /* Nothing to do: the count still renders, it just will not persist. */
    }
  }

  function cached(repo) {
    var entry = readCache()[repo];
    if (!entry || typeof entry.t !== "number") return undefined;
    var count = typeof entry.n === "number" ? entry.n : null;
    var age = Date.now() - entry.t;
    if (age < 0 || age > (count === null ? TTL_MISS : TTL_OK)) return undefined;
    return count;
  }

  function format(n) {
    if (n < 1000) return String(n);
    if (n < 10000) {
      var tenths = Math.floor(n / 100) / 10;
      return (tenths % 1 === 0 ? tenths.toFixed(0) : tenths.toFixed(1)) + "k";
    }
    return Math.round(n / 1000) + "k";
  }

  function fetchCount(repo) {
    if (inFlight[repo]) return inFlight[repo];
    var hit = cached(repo);
    if (hit !== undefined) {
      inFlight[repo] = Promise.resolve(hit);
      return inFlight[repo];
    }
    inFlight[repo] = fetch(API + repo, {
      headers: { Accept: "application/vnd.github+json" }
    })
      .then(function (res) {
        /* 403 is rate limiting, 404 a renamed or deleted repo. Both mean no
           number exists to show, and both are remembered so the rest of the
           session does not ask again. */
        if (!res.ok) return null;
        return res.json();
      })
      .then(function (data) {
        var n = data && data.stargazers_count;
        return typeof n === "number" && isFinite(n) && n >= 0 ? Math.floor(n) : null;
      })
      .catch(function () {
        return null;
      })
      .then(function (count) {
        writeCache(repo, count);
        return count;
      });
    return inFlight[repo];
  }

  function glyph() {
    var ns = "http://www.w3.org/2000/svg";
    var svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 16 16");
    svg.setAttribute("width", "14");
    svg.setAttribute("height", "14");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");
    var path = document.createElementNS(ns, "path");
    path.setAttribute("fill", "currentColor");
    path.setAttribute("d", STAR_PATH);
    svg.appendChild(path);
    return svg;
  }

  function enhance(link) {
    var repo = link.getAttribute("data-repo");
    if (!repo || !/^[\w.-]+\/[\w.-]+$/.test(repo)) return;

    var label = (link.textContent || "").trim();
    if (!label) label = "Star " + repo;

    /* The anchor stays an anchor and keeps its href, so the only thing the
       enhancement changes is what is drawn inside it. */
    var face = document.createElement("span");
    face.className = "gh-star-face";
    face.appendChild(glyph());
    var text = document.createElement("span");
    text.className = "gh-star-text";
    text.textContent = label;
    face.appendChild(text);

    link.textContent = "";
    link.appendChild(face);
    link.classList.add("is-enhanced");
    link.setAttribute("aria-label", label + " on GitHub");

    fetchCount(repo).then(function (count) {
      if (count === null) return;
      var chip = document.createElement("span");
      chip.className = "gh-star-count";
      /* The chip is decorative to a screen reader; the number is folded into
         the link's own name instead, so the count is read once and read as
         part of a sentence rather than as a stray digit after the label. */
      chip.setAttribute("aria-hidden", "true");
      chip.textContent = format(count);
      link.appendChild(chip);
      link.classList.add("has-count");
      link.setAttribute(
        "aria-label",
        label + " on GitHub, " + count.toLocaleString("en") +
          (count === 1 ? " star" : " stars")
      );
    });
  }

  var links = document.querySelectorAll("a.gh-star[data-repo]");
  for (var i = 0; i < links.length; i++) enhance(links[i]);
})();

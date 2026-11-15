(function () {
  var root = document.documentElement;
  var toggle = document.getElementById("theme-toggle");
  var meta = document.querySelector('meta[name="theme-color"]');
  var media = window.matchMedia("(prefers-color-scheme: light)");
  var reel = document.getElementById("dev-reel");
  var reelSource = document.getElementById("dev-reel-source");

  function stored() {
    try {
      return localStorage.getItem("aga-theme");
    } catch (error) {
      return null;
    }
  }

  function syncReel(theme) {
    if (!reel || !reelSource) return;
    var src = theme === "light" ? reel.dataset.srcLight : reel.dataset.srcDark;
    var poster = theme === "light" ? reel.dataset.posterLight : reel.dataset.posterDark;
    if (!src || reelSource.getAttribute("src") === src) return;
    var time = reel.currentTime || 0;
    var playing = !reel.paused && !reel.ended;
    reelSource.setAttribute("src", src);
    if (poster) reel.setAttribute("poster", poster);
    reel.load();
    reel.addEventListener("loadedmetadata", function once() {
      reel.removeEventListener("loadedmetadata", once);
      if (time > 0) reel.currentTime = Math.min(time, reel.duration || time);
      if (playing) {
        var p = reel.play();
        if (p && p.catch) p.catch(function () {});
      }
    });
  }

  function apply(theme) {
    root.dataset.theme = theme;
    if (meta) {
      meta.setAttribute("content", theme === "light" ? "#f5f8f7" : "#0a0e13");
    }
    if (toggle) {
      toggle.setAttribute("aria-label", theme === "light" ? "切换到深色模式" : "切换到浅色模式");
      toggle.setAttribute("aria-pressed", theme === "light" ? "true" : "false");
    }
    syncReel(theme);
  }

  apply(root.dataset.theme === "light" ? "light" : "dark");

  if (toggle) {
    toggle.addEventListener("click", function () {
      var next = root.dataset.theme === "light" ? "dark" : "light";
      apply(next);
      try {
        localStorage.setItem("aga-theme", next);
      } catch (error) {
        /* 隐私模式下无法持久化时只切换当前页面 */
      }
    });
  }

  media.addEventListener("change", function (event) {
    if (!stored()) {
      apply(event.matches ? "light" : "dark");
    }
  });
})();

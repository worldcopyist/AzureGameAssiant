(function () {
  var root = document.documentElement;
  var toggle = document.getElementById("theme-toggle");
  var meta = document.querySelector('meta[name="theme-color"]');
  var media = window.matchMedia("(prefers-color-scheme: light)");

  function stored() {
    try {
      return localStorage.getItem("aga-theme");
    } catch (error) {
      return null;
    }
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

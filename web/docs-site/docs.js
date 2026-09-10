/* Docs behaviour: diagrams, the phone menu, and the "on this page" highlight.
   Loaded from the same origin — the site's content-security policy allows no
   inline script, so everything that runs on a docs page lives here. */
(function () {
  // Mermaid diagrams, only when the page shipped the library.
  if (window.mermaid) {
    window.mermaid.initialize({
      startOnLoad: true,
      theme: 'dark',
      themeVariables: { fontFamily: 'inherit', primaryColor: '#16202e', primaryTextColor: '#e2e9f3', lineColor: '#74849a' },
      securityLevel: 'strict',
    })
  }

  // Phone: the side navigation folds behind the menu button.
  var menu = document.querySelector('.top__menu')
  var side = document.getElementById('sidenav')
  if (menu && side) {
    menu.addEventListener('click', function () {
      var open = side.classList.toggle('is-open')
      menu.setAttribute('aria-expanded', open ? 'true' : 'false')
    })
  }

  // "On this page": the heading currently in view is marked in the outline.
  var links = Array.prototype.slice.call(document.querySelectorAll('.toc a'))
  if (links.length && 'IntersectionObserver' in window) {
    var byId = {}
    links.forEach(function (a) { byId[a.getAttribute('href').slice(1)] = a })
    var current = null
    var io = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (e) {
          if (e.isIntersecting) {
            if (current) current.classList.remove('is-active')
            current = byId[e.target.id]
            if (current) current.classList.add('is-active')
          }
        })
      },
      { rootMargin: '-64px 0px -70% 0px', threshold: 0 },
    )
    Object.keys(byId).forEach(function (id) {
      var h = document.getElementById(id)
      if (h) io.observe(h)
    })
  }
})()

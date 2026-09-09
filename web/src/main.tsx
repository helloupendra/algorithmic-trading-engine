import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App.tsx'

// A deploy renames every chunk. A tab opened before it still asks for the old
// names when it first reaches a lazy screen, and Vite reports the 404 here
// rather than as an error the page can catch. The fix is the new index.html,
// which a reload fetches; nothing in the old tab could have been saved by
// waiting.
window.addEventListener('vite:preloadError', (event) => {
  event.preventDefault()
  window.location.reload()
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)

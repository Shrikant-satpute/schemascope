import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createHighlighterCore } from './monaco-setup'
import './index.css'
import App from './App.tsx'
import { ViewerApp } from './ViewerApp.tsx'

createHighlighterCore()

// A /s/<token> address means this page was opened from a shared link, so it
// shows the read-only viewer instead of the app.
const shared = /^\/s\/([A-Za-z0-9_-]{16,64})\/?$/.exec(window.location.pathname)

createRoot(document.getElementById('root')!).render(
  <StrictMode>{shared ? <ViewerApp token={shared[1]} /> : <App />}</StrictMode>,
)

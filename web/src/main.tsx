import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createHighlighterCore } from './monaco-setup'
import './index.css'
import App from './App.tsx'

createHighlighterCore()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)

/**
 * What the desktop window can do that a browser tab cannot.
 *
 * The desktop app is a WebView2 window, and WebView2 gives the page a private
 * message channel to the window that hosts it (`window.chrome.webview`). That
 * channel is how the page asks for a real Windows folder picker and asks
 * Explorer to open a folder - without adding an HTTP route that any other web
 * page on this machine could call.
 *
 * In a normal browser (`--server` mode, or Vite during development) the channel
 * does not exist, so every helper here falls back to something a browser can
 * do: a text prompt for the folder, and nothing at all for "open folder".
 */

interface WebViewMessageEvent {
  data: unknown
}

interface WebViewChannel {
  postMessage(message: unknown): void
  addEventListener(type: 'message', handler: (e: WebViewMessageEvent) => void): void
  removeEventListener(type: 'message', handler: (e: WebViewMessageEvent) => void): void
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewChannel }
  }
}

function channel(): WebViewChannel | undefined {
  return window.chrome?.webview
}

/** True inside the SchemaScope desktop window, false in a browser tab. */
export function hasNativeHost(): boolean {
  return !!channel()
}

/** A correlation id, so a reply is matched to the request that asked for it. */
function newId(): string {
  try {
    return crypto.randomUUID()
  } catch {
    // randomUUID needs a secure context; plain http on a LAN address is not one.
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
  }
}

interface PickFolderReply {
  id?: unknown
  type?: unknown
  path?: unknown
  busy?: unknown
}

export interface PickFolderOptions {
  /** The picker's title (desktop) or the prompt's question (browser). */
  title?: string
  /** Browser only: what the prompt offers when `initial` is empty. Leave out for no suggestion. */
  suggestion?: string
}

/**
 * Lets the user choose a folder. Resolves to the full path, or null when they
 * cancel.
 *
 * In the desktop window this opens the Windows folder picker. The window only
 * allows one picker at a time; a second request is answered straight away
 * with no path, which reads here the same as a cancel.
 */
export function pickFolder(initial?: string, options: PickFolderOptions = {}): Promise<string | null> {
  const title = options.title ?? 'Choose where to write the scripts'
  const wv = channel()
  if (!wv) {
    const typed = window.prompt(`${title}:`, initial || options.suggestion || '')
    return Promise.resolve(typed?.trim() ? typed.trim() : null)
  }

  const id = newId()
  return new Promise((resolve) => {
    const handler = (e: WebViewMessageEvent) => {
      const reply = e.data as PickFolderReply | null
      if (!reply || typeof reply !== 'object' || reply.id !== id || reply.type !== 'pickFolderResult') return
      wv.removeEventListener('message', handler)
      resolve(typeof reply.path === 'string' && reply.path ? reply.path : null)
    }
    wv.addEventListener('message', handler)
    wv.postMessage({ id, type: 'pickFolder', initial: initial || null, title })
  })
}

/**
 * True when {@link openFolder} can open this path: in the desktop window, and
 * not for a path with a comma, which the window refuses (Explorer would read
 * the comma as an argument separator).
 */
export function canOpenFolder(path: string): boolean {
  return hasNativeHost() && !!path && !path.includes(',')
}

/**
 * Opens a folder in Explorer. Desktop window only - a browser has no way to
 * do this, so there it does nothing and the caller offers "Copy path" instead.
 */
export function openFolder(path: string): void {
  const wv = channel()
  if (!wv || !path) return
  wv.postMessage({ id: newId(), type: 'openFolder', path })
}

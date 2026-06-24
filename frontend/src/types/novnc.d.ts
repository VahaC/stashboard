/**
 * V8.6 — minimal ambient types for `@novnc/novnc` (the library ships none). Covers
 * only the slice of the RFB API the VM console uses: construction with a target
 * element + websocket URL + options, the lifecycle events, viewport scaling, and
 * teardown. See https://github.com/novnc/noVNC/blob/master/docs/API.md.
 */
declare module '@novnc/novnc' {
  export interface RFBCredentials {
    username?: string
    password?: string
    target?: string
  }

  export interface RFBOptions {
    /** RFB credentials; for Proxmox the `password` is the one-time vncproxy ticket. */
    credentials?: RFBCredentials
    /** WebSocket subprotocols to request — Proxmox speaks `['binary']`. */
    wsProtocols?: string[]
    shared?: boolean
    repeaterID?: string
  }

  export default class RFB extends EventTarget {
    /** `urlOrChannel` may be a `ws(s)://` URL string or a pre-created WebSocket /
     *  RTCDataChannel that noVNC attaches to (the latter lets the caller observe the
     *  socket's close reason). */
    constructor(target: HTMLElement, urlOrChannel: string | WebSocket, options?: RFBOptions)

    /** Scale the remote framebuffer to fit the container. */
    scaleViewport: boolean
    /** Ask the server to resize its session to the container (VNC ExtendedDesktopSize). */
    resizeSession: boolean
    /** Clip the viewport instead of scrolling. */
    clipViewport: boolean
    /** Read-only (no input forwarded) when true. */
    viewOnly: boolean
    /** CSS background behind the framebuffer. */
    background: string
    focusOnClick: boolean

    /** Closes the connection (fires the `disconnect` event). */
    disconnect(): void
    /** Supplies credentials in response to a `credentialsrequired` event. */
    sendCredentials(credentials: RFBCredentials): void
    /** Moves keyboard focus to the canvas. */
    focus(): void
    blur(): void
  }
}

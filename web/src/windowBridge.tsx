import React from 'react';

type WindowEdge = 'n' | 's' | 'w' | 'e' | 'nw' | 'ne' | 'sw' | 'se';

interface NativeBridgePayload {
  type:
    | 'window:drag'
    | 'window:resize'
    | 'window:minimize'
    | 'window:toggleMaximize'
    | 'window:hideToTray'
    | 'window:toggleMini'
    | 'window:preferencesSync'
    | 'window:openUrl'
    | 'tray:openMain'
    | 'tray:hide'
    | 'tray:exitApp'
    | 'tray:toggleMini'
    | 'tray:resize';
  edge?: WindowEdge;
  url?: string;
  height?: number;
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (msg: unknown) => void;
        addEventListener?: (name: string, fn: (e: { data: unknown }) => void) => void;
        removeEventListener?: (name: string, fn: (e: { data: unknown }) => void) => void;
      };
    };
    webkit?: {
      messageHandlers?: {
        windowControl?: {
          postMessage: (msg: unknown) => void;
        };
      };
    };
  }
}

export function isDesktopShell(): boolean {
  return Boolean(window.chrome?.webview || window.webkit?.messageHandlers?.windowControl);
}

export function sendWindowCommand(payload: NativeBridgePayload): void {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(payload);
    return;
  }
  if (window.webkit?.messageHandlers?.windowControl) {
    window.webkit.messageHandlers.windowControl.postMessage(payload);
  }
}

export function startWindowDrag(e: React.MouseEvent): void {
  if (e.button !== 0) return;
  const target = e.target as HTMLElement | null;
  if (target?.closest('button, a, input, select, textarea, label, [data-no-drag="true"]')) {
    return;
  }
  sendWindowCommand({ type: 'window:drag' });
}

export function toggleWindowMaximizeOnDoubleClick(e: React.MouseEvent): void {
  const target = e.target as HTMLElement | null;
  if (target?.closest('button, a, input, select, textarea, label, [data-no-drag="true"]')) {
    return;
  }
  sendWindowCommand({ type: 'window:toggleMaximize' });
}

export function subscribeMaximizedState(onChange: (maximized: boolean) => void): () => void {
  const webview = window.chrome?.webview;
  if (!webview?.addEventListener) return () => {};
  const handler = (ev: { data: unknown }) => {
    try {
      const data = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
      if (data && typeof data === 'object' && (data as { type?: string }).type === 'window:maximizedState') {
        onChange(Boolean((data as { maximized?: boolean }).maximized));
      }
    } catch {
      // 忽略无关消息
    }
  };
  webview.addEventListener('message', handler);
  return () => webview.removeEventListener?.('message', handler);
}

export function subscribeTrayShown(onShown: () => void): () => void {
  const webview = window.chrome?.webview;
  if (!webview?.addEventListener) return () => {};
  const handler = (ev: { data: unknown }) => {
    try {
      const data = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
      if (data && typeof data === 'object' && (data as { type?: string }).type === 'tray:shown') {
        onShown();
      }
    } catch {
      // 忽略无关消息
    }
  };
  webview.addEventListener('message', handler);
  return () => webview.removeEventListener?.('message', handler);
}

const EDGE_HANDLES: { edge: WindowEdge; className: string }[] = [
  { edge: 'n', className: 'win-edge win-edge-n' },
  { edge: 's', className: 'win-edge win-edge-s' },
  { edge: 'w', className: 'win-edge win-edge-w' },
  { edge: 'e', className: 'win-edge win-edge-e' },
  { edge: 'nw', className: 'win-edge win-edge-nw' },
  { edge: 'ne', className: 'win-edge win-edge-ne' },
  { edge: 'sw', className: 'win-edge win-edge-sw' },
  { edge: 'se', className: 'win-edge win-edge-se' }
];

export const WindowResizeHandles: React.FC = () => (
  <>
    {EDGE_HANDLES.map(h => (
      <div
        key={h.edge}
        className={h.className}
        aria-hidden="true"
        onMouseDown={e => {
          if (e.button === 0) {
            e.preventDefault();
            sendWindowCommand({ type: 'window:resize', edge: h.edge });
          }
        }}
      />
    ))}
  </>
);

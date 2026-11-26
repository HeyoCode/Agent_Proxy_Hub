import React from 'react';
import ReactDOM from 'react-dom/client';
import '@fontsource-variable/plus-jakarta-sans';
import '@fontsource-variable/jetbrains-mono';
import { App } from './App';
import { TrayQuickPanel } from './components/TrayQuickPanel';
import { applyResolvedTheme, loadThemePreference, resolveTheme } from './theme';
import './styles/nordic-dashboard.css';

const isTrayMode = new URLSearchParams(window.location.search).get('tray') === '1';
if (isTrayMode) {
  document.documentElement.setAttribute('data-tray', '1');
}

// 启动即同步主题并监听系统深浅色与跨窗口偏好变更
const syncTheme = () => applyResolvedTheme(resolveTheme(loadThemePreference()));
syncTheme();
if (typeof window !== 'undefined' && window.matchMedia) {
  const mql = window.matchMedia('(prefers-color-scheme: dark)');
  mql.addEventListener?.('change', syncTheme);
}
window.addEventListener('storage', e => {
  if (e.key === 'aph_theme_mode') syncTheme();
});

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    {isTrayMode ? <TrayQuickPanel /> : <App />}
  </React.StrictMode>
);

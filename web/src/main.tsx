import React from 'react';
import ReactDOM from 'react-dom/client';
import '@fontsource-variable/plus-jakarta-sans';
import '@fontsource-variable/jetbrains-mono';
import { App } from './App';
import { TrayQuickPanel } from './components/TrayQuickPanel';
import './styles/nordic-dashboard.css';

const isTrayMode = new URLSearchParams(window.location.search).get('tray') === '1';
if (isTrayMode) {
  document.documentElement.setAttribute('data-tray', '1');
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    {isTrayMode ? <TrayQuickPanel /> : <App />}
  </React.StrictMode>
);

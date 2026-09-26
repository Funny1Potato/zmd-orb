/* 取数薄层：本地采集端（127.0.0.1:8910）与壳侧调用。
 *
 * 壳里和浏览器里都能跑：浏览器直接 fetch；壳里 CSP 已放行同一源。
 * 拿不到数据一律返回 null，让页面回落演示数据（沿用 zmd 的做法）。
 */
const API_BASE = 'http://127.0.0.1:8910';

async function apiGet(path, timeoutMs) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeoutMs || 1200);
  try {
    const r = await fetch(API_BASE + path, { signal: ctl.signal, cache: 'no-store' });
    if (!r.ok) return null;
    return await r.json();
  } catch (e) {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

const apiSnapshot = () => apiGet('/snapshot');
const apiHealth = () => apiGet('/health');

/* ---- 壳侧（Tauri）能力，浏览器里全部静默降级 ---- */
function tauriCore() {
  return (window.__TAURI__ && window.__TAURI__.core) || null;
}

/** 当前窗口（壳里才有）；浏览器返回 null */
function tauriWindow() {
  const w = window.__TAURI__ && window.__TAURI__.window;
  return w && w.getCurrentWindow ? w.getCurrentWindow() : null;
}

function invokeCmd(cmd, args) {
  const core = tauriCore();
  return core ? core.invoke(cmd, args) : Promise.resolve(null);
}

/** 原生窗口拖动（无边框窗口；pywebview 那套 ReleaseCapture 不需要，Tauri 原生支持） */
function startDrag() {
  const w = tauriWindow();
  if (w && w.startDragging) w.startDragging();
}

const inShell = () => !!tauriCore();
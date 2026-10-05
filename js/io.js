// QuantScope Web — ファイルの読み書きとキー操作（ファイルはブラウザーの中だけで扱い、どこにも送らない）

// JPEG・BMP・GIF・WebP など: ブラウザーの部品で読み、先頭 8 バイトに幅と高さ、そのあとに RGBA を並べて返す
export async function decodeImage(bytes, type) {
  const blob = new Blob([bytes], { type: type || '' });
  let bmp;
  try {
    bmp = await createImageBitmap(blob, { colorSpaceConversion: 'none', premultiplyAlpha: 'none' });
  } catch {
    return null;
  }
  const w = bmp.width, h = bmp.height;
  const cv = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(w, h) : Object.assign(document.createElement('canvas'), { width: w, height: h });
  const ctx = cv.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(bmp, 0, 0);
  const px = ctx.getImageData(0, 0, w, h).data;
  const out = new Uint8Array(8 + px.length);
  const dv = new DataView(out.buffer);
  dv.setUint32(0, w, true); dv.setUint32(4, h, true);
  out.set(px, 8);
  return out;
}

export function download(name, mime, bytes) {
  const blob = new Blob([bytes], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; a.rel = 'noopener';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

// レポートを新しいタブで開く（スマホでは保存の代わりに見られるように）
export function openHtml(html) {
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
  const w = window.open(url, '_blank', 'noopener');
  setTimeout(() => URL.revokeObjectURL(url), 60000);
  return !!w;
}

export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}

export function load(key) { try { return localStorage.getItem(key); } catch { return null; } }
export function save(key, value) { try { localStorage.setItem(key, value); } catch { /* 保存できなくても動く */ } }

let keyHandler = null;
export function listenKeys(dotnet) {
  if (keyHandler) document.removeEventListener('keydown', keyHandler);
  keyHandler = e => {
    const t = e.target;
    const typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    const mod = e.ctrlKey || e.metaKey;
    let key = null;
    if (mod && !e.altKey) {
      const k = e.key.toLowerCase();
      if (k === 'o') key = 'open';
      else if (k === 's') key = 'save';
      else if (!typing && k === 'z') key = e.shiftKey ? 'redo' : 'undo';
      else if (!typing && k === 'y') key = 'redo';
    } else if (!typing && !e.altKey) {
      key = { v: 'pan', r: 'rect', e: 'ellipse', p: 'polygon', l: 'line', x: 'exclude', c: 'compare', f: 'fit', '1': 'actual', Delete: 'delete' }[e.key] || null;
    }
    if (key) { e.preventDefault(); dotnet.invokeMethodAsync('OnKey', key); }
  };
  document.addEventListener('keydown', keyHandler);
}

export function clickElement(id) { document.getElementById(id)?.click(); }
export function scrollIntoView(id) { document.getElementById(id)?.scrollIntoView({ block: 'nearest' }); }
export function isNarrow() { return window.matchMedia('(max-width: 1099px)').matches; }

// RGBA を PNG にする（長い辺を maxEdge 以下に縮める。0 なら縮めない）
export async function encodePng(w, h, rgba, maxEdge) {
  const data = new ImageData(new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, w * h * 4), w, h);
  const s = maxEdge > 0 ? Math.min(1, maxEdge / Math.max(w, h)) : 1;
  const ow = Math.max(1, Math.round(w * s)), oh = Math.max(1, Math.round(h * s));
  const src = document.createElement('canvas'); src.width = w; src.height = h; src.getContext('2d').putImageData(data, 0, 0);
  const dst = document.createElement('canvas'); dst.width = ow; dst.height = oh;
  const ctx = dst.getContext('2d'); ctx.imageSmoothingQuality = 'high'; ctx.drawImage(src, 0, 0, ow, oh);
  const blob = await new Promise(r => dst.toBlob(r, 'image/png'));
  return new Uint8Array(await blob.arrayBuffer());
}

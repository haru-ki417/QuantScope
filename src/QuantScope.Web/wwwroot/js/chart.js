// グラフの線をドラッグするための小さな助け（要素の左端と幅、指・マウスを離さずに追う）
export function rect(el) { if (!el) return [0, 1]; const r = el.getBoundingClientRect(); return [r.left, r.width]; }
export function capture(el, id) { try { el.setPointerCapture(id); } catch { /* 古いブラウザー */ } }

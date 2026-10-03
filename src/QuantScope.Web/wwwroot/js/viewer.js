// QuantScope Web — 画像の表示（拡大・移動・ピンチ、重ね合わせ、番号、範囲・線・粒を除く、左右に比べる）
// 計算は C# 側で行い、ここでは画面に描くことと、マウス・指の操作を C# に伝えることだけをする。

const ACCENT = '#45D19A';
const MAGENTA = '#FF5FAA';
const LABEL_BG = 'rgba(10,14,16,0.66)';
const NICE = [1, 2, 5, 10];

class Viewer {
  constructor(host, dotnet) {
    this.host = host;
    this.dotnet = dotnet;
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'viewer-canvas';
    this.canvas.tabIndex = 0;
    host.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d');
    this.image = null; this.overlay = null; this.compare = null;
    this.iw = 0; this.ih = 0;
    this.scale = 1; this.ox = 0; this.oy = 0; this.fitted = false;
    this.opts = { showOverlay: true, opacity: 0.75, showNumbers: true, tool: 'pan', comparing: false, unit: 'px', upp: 1 };
    this.particles = []; this.selected = null; this.roi = null; this.line = null; this.matches = [];
    this.pointers = new Map();
    this.drag = null; this.poly = []; this.hover = null; this.split = 0.5;
    this.pendingHover = null;
    const ro = new ResizeObserver(() => this.resize());
    ro.observe(host);
    this.bind();
    this.resize();
  }

  // ------------------------------------------------------------ C# から
  async setImage(w, h, rgba) {
    const changed = w !== this.iw || h !== this.ih;
    this.image = await toBitmap(w, h, rgba);
    this.iw = w; this.ih = h;
    if (changed) this.fitted = false;
    this.draw();
  }
  async setOverlay(w, h, rgba) { this.overlay = rgba ? await toBitmap(w, h, rgba) : null; this.draw(); }
  async setCompare(w, h, rgba) { this.compare = rgba ? await toBitmap(w, h, rgba) : null; this.draw(); }
  clear() { this.image = null; this.overlay = null; this.compare = null; this.iw = this.ih = 0; this.draw(); }
  setOptions(o) {
    const toolChanged = o.tool !== undefined && o.tool !== this.opts.tool;
    Object.assign(this.opts, o);
    if (toolChanged) { this.poly = []; this.drag = null; }
    this.canvas.style.cursor = this.opts.tool === 'pan' ? 'default' : this.opts.tool === 'exclude' ? 'pointer' : 'crosshair';
    this.draw();
  }
  setShapes(s) {
    this.particles = s.particles || [];
    this.selected = s.selected || null;
    this.roi = s.roi || null;
    this.line = s.line || null;
    this.matches = s.matches || [];
    this.draw();
  }
  fit() {
    if (!this.iw) return;
    const pad = this.cw < 600 ? 8 : 24;
    this.scale = Math.max(0.01, Math.min((this.cw - 2 * pad) / this.iw, (this.ch - 2 * pad) / this.ih));
    this.ox = (this.cw - this.iw * this.scale) / 2;
    this.oy = (this.ch - this.ih * this.scale) / 2;
    this.fitted = true;
    this.userMoved = false;
    this.draw();
  }
  zoomTo(s) { this.zoomAt(this.cw / 2, this.ch / 2, s / this.scale); }
  centerOn(cx, cy, size) {
    if (!this.iw) return;
    if (size * this.scale < 40) this.scale = Math.min(16, Math.max(this.scale, 80 / Math.max(size, 1)));
    this.ox = this.cw / 2 - cx * this.scale;
    this.oy = this.ch / 2 - cy * this.scale;
    this.userMoved = true;
    this.draw();
  }

  // ------------------------------------------------------------ 描く
  resize() {
    const r = this.host.getBoundingClientRect();
    this.dpr = window.devicePixelRatio || 1;
    this.cw = Math.max(1, r.width); this.ch = Math.max(1, r.height);
    this.canvas.width = Math.round(this.cw * this.dpr);
    this.canvas.height = Math.round(this.ch * this.dpr);
    this.canvas.style.width = this.cw + 'px';
    this.canvas.style.height = this.ch + 'px';
    // 自分で拡大・移動していなければ、大きさが変わったら全体を表示し直す
    if (!this.fitted || !this.userMoved) this.fit(); else this.draw();
  }
  toImage(x, y) { return { x: (x - this.ox) / this.scale, y: (y - this.oy) / this.scale }; }
  toScreen(x, y) { return { x: x * this.scale + this.ox, y: y * this.scale + this.oy }; }

  draw() {
    if (this.raf) return;
    this.raf = requestAnimationFrame(() => { this.raf = 0; this.render(); });
  }

  render() {
    const c = this.ctx, d = this.dpr;
    c.setTransform(d, 0, 0, d, 0, 0);
    c.fillStyle = '#000'; c.fillRect(0, 0, this.cw, this.ch);
    if (!this.image) return;
    if (!this.fitted) this.fit();
    const w = this.iw * this.scale, h = this.ih * this.scale;
    c.imageSmoothingEnabled = this.scale < 2;
    c.imageSmoothingQuality = 'high';
    c.drawImage(this.image, this.ox, this.oy, w, h);
    const compare = this.opts.comparing && this.compare;
    const sx = this.ox + w * this.split;
    if (this.opts.showOverlay && this.overlay) {
      c.save();
      if (compare) { c.beginPath(); c.rect(sx, this.oy, Math.max(0, this.ox + w - sx), h); c.clip(); }
      c.globalAlpha = this.opts.opacity;
      c.drawImage(this.overlay, this.ox, this.oy, w, h);
      c.restore();
    }
    if (compare) {
      c.save(); c.beginPath(); c.rect(this.ox, this.oy, Math.max(0, sx - this.ox), h); c.clip();
      c.drawImage(this.compare, this.ox, this.oy, w, h);
      c.restore();
    }
    this.drawNumbers(); this.drawSelection(); this.drawMatches(); this.drawRoi(); this.drawLine(); this.drawScaleBar();
    if (compare) this.drawSplit(sx);
  }

  label(text, x, y, color = '#fff', size = 12, bg = LABEL_BG) {
    const c = this.ctx;
    c.font = `${size}px "Bahnschrift","Segoe UI",system-ui,sans-serif`;
    const tw = c.measureText(text).width;
    c.fillStyle = bg; roundRect(c, x - 4, y - size + 1, tw + 8, size + 6, 3); c.fill();
    c.fillStyle = color; c.fillText(text, x, y + 2);
  }

  drawNumbers() {
    if (!this.opts.showNumbers || !this.opts.showOverlay || !this.particles.length || this.particles.length > 1500) return;
    const c = this.ctx;
    const size = Math.max(9, Math.min(14, 11 * Math.sqrt(this.scale)));
    c.font = `${size}px "Bahnschrift","Segoe UI",system-ui,sans-serif`;
    c.textBaseline = 'middle'; c.textAlign = 'center';
    for (const p of this.particles) {
      if (p[3] * this.scale < 14) continue;
      const s = this.toScreen(p[1], p[2]);
      if (s.x < -20 || s.y < -20 || s.x > this.cw + 20 || s.y > this.ch + 20) continue;
      const t = String(p[0]); const tw = c.measureText(t).width;
      c.fillStyle = LABEL_BG; roundRect(c, s.x - tw / 2 - 3, s.y - size / 2 - 1, tw + 6, size + 2, 3); c.fill();
      c.fillStyle = 'rgba(255,255,255,0.95)'; c.fillText(t, s.x, s.y + 0.5);
    }
    c.textAlign = 'left'; c.textBaseline = 'alphabetic';
  }

  drawSelection() {
    const p = this.selected; if (!p || !this.opts.showOverlay) return;
    const a = this.toScreen(p[0], p[1]), b = this.toScreen(p[0] + p[2], p[1] + p[3]);
    const c = this.ctx; c.strokeStyle = ACCENT; c.lineWidth = 2;
    c.strokeRect(a.x - 4, a.y - 4, b.x - a.x + 8, b.y - a.y + 8);
  }

  drawMatches() {
    const c = this.ctx; c.setLineDash([5, 4]); c.strokeStyle = MAGENTA; c.lineWidth = 1.6;
    for (const m of this.matches) {
      const a = this.toScreen(m[0], m[1]), b = this.toScreen(m[0] + m[2], m[1] + m[3]);
      c.strokeRect(a.x, a.y, b.x - a.x, b.y - a.y);
      c.fillStyle = MAGENTA; c.font = '10px "Bahnschrift",system-ui,sans-serif'; c.fillText(m[4].toFixed(2), a.x + 2, a.y - 3);
    }
    c.setLineDash([]);
  }

  roiPath(shape, pts) {
    const c = this.ctx; c.beginPath();
    if (shape === 'polygon') {
      pts.forEach((p, i) => { const s = this.toScreen(p[0], p[1]); i ? c.lineTo(s.x, s.y) : c.moveTo(s.x, s.y); });
      c.closePath();
    } else {
      const a = this.toScreen(pts[0][0], pts[0][1]), b = this.toScreen(pts[1][0], pts[1][1]);
      if (shape === 'ellipse') c.ellipse((a.x + b.x) / 2, (a.y + b.y) / 2, Math.abs(b.x - a.x) / 2, Math.abs(b.y - a.y) / 2, 0, 0, Math.PI * 2);
      else c.rect(a.x, a.y, b.x - a.x, b.y - a.y);
    }
  }

  drawRoi() {
    const c = this.ctx;
    if (this.roi) {
      c.save();
      c.beginPath(); c.rect(0, 0, this.cw, this.ch);
      this.roiPath(this.roi.shape, this.roi.points);
      c.fillStyle = 'rgba(0,0,0,0.33)'; c.fill('evenodd');
      c.restore();
      this.roiPath(this.roi.shape, this.roi.points);
      c.strokeStyle = ACCENT; c.lineWidth = 1.6; c.stroke();
    }
    const t = this.opts.tool;
    if (this.drag && this.drag.kind === 'shape' && (t === 'rect' || t === 'ellipse')) {
      const s = this.drag.start, n = this.drag.now;
      this.roiPath(t === 'ellipse' ? 'ellipse' : 'rect', [[s.x, s.y], [n.x, n.y]]);
      c.fillStyle = 'rgba(69,209,154,0.18)'; c.fill();
      c.setLineDash([5, 4]); c.strokeStyle = ACCENT; c.lineWidth = 1.4; c.stroke(); c.setLineDash([]);
    }
    if (t === 'polygon' && this.poly.length) {
      const pts = this.poly.map(p => [p.x, p.y]); if (this.hover) pts.push([this.hover.x, this.hover.y]);
      c.beginPath(); pts.forEach((p, i) => { const s = this.toScreen(p[0], p[1]); i ? c.lineTo(s.x, s.y) : c.moveTo(s.x, s.y); });
      c.setLineDash([5, 4]); c.strokeStyle = ACCENT; c.lineWidth = 1.4; c.stroke(); c.setLineDash([]);
      c.fillStyle = ACCENT; for (const p of this.poly) { const s = this.toScreen(p.x, p.y); c.beginPath(); c.arc(s.x, s.y, 3.5, 0, 7); c.fill(); }
    }
  }

  drawLine() {
    let l = this.line;
    if (this.opts.tool === 'line' && this.drag && this.drag.kind === 'shape') l = [this.drag.start.x, this.drag.start.y, this.drag.now.x, this.drag.now.y];
    if (!l) return;
    const c = this.ctx, a = this.toScreen(l[0], l[1]), b = this.toScreen(l[2], l[3]);
    c.lineCap = 'round';
    c.strokeStyle = 'rgba(0,0,0,0.55)'; c.lineWidth = 4; c.beginPath(); c.moveTo(a.x, a.y); c.lineTo(b.x, b.y); c.stroke();
    c.strokeStyle = ACCENT; c.lineWidth = 2; c.beginPath(); c.moveTo(a.x, a.y); c.lineTo(b.x, b.y); c.stroke();
    c.fillStyle = ACCENT; for (const p of [a, b]) { c.beginPath(); c.arc(p.x, p.y, 3.5, 0, 7); c.fill(); }
    const px = Math.hypot(l[2] - l[0], l[3] - l[1]);
    const text = this.opts.unit !== 'px' ? `${fmt(px * this.opts.upp)} ${this.opts.unit}` : `${px.toFixed(1)} px`;
    this.label(text, (a.x + b.x) / 2 + 8, (a.y + b.y) / 2 - 8);
  }

  drawScaleBar() {
    if (this.opts.unit === 'px') return;
    const c = this.ctx, upp = this.opts.upp;
    const target = 120 / this.scale * upp;
    const p = Math.pow(10, Math.floor(Math.log10(target)));
    let len = p; for (const m of NICE) if (m * p <= target * 1.4) len = m * p;
    const w = len / upp * this.scale, x = 16, y = this.ch - 20;
    c.fillStyle = LABEL_BG; c.fillRect(x - 8, y - 24, w + 16, 34);
    c.fillStyle = '#fff'; c.fillRect(x, y, w, 4);
    c.font = '11px "Bahnschrift","Segoe UI",system-ui,sans-serif';
    const t = `${+len.toPrecision(3)} ${this.opts.unit}`; const tw = c.measureText(t).width;
    c.fillText(t, x + (w - tw) / 2, y - 6);
  }

  drawSplit(x) {
    const c = this.ctx, top = Math.max(0, this.oy), bottom = Math.min(this.ch, this.oy + this.ih * this.scale);
    c.strokeStyle = 'rgba(0,0,0,0.55)'; c.lineWidth = 4; c.beginPath(); c.moveTo(x, top); c.lineTo(x, bottom); c.stroke();
    c.strokeStyle = '#fff'; c.lineWidth = 1.5; c.beginPath(); c.moveTo(x, top); c.lineTo(x, bottom); c.stroke();
    const cy = (top + bottom) / 2;
    c.fillStyle = '#fff'; c.beginPath(); c.arc(x, cy, 13, 0, 7); c.fill();
    c.fillStyle = '#0a0e10'; c.beginPath();
    c.moveTo(x - 9, cy); c.lineTo(x - 4, cy - 5); c.lineTo(x - 4, cy + 5); c.closePath();
    c.moveTo(x + 9, cy); c.lineTo(x + 4, cy - 5); c.lineTo(x + 4, cy + 5); c.closePath(); c.fill();
    const ly = Math.max(top + 10, 50) + 12;
    c.font = '12px system-ui,sans-serif';
    const lt = '元の画像', rt = '今の表示';
    if (x - c.measureText(lt).width - 18 > 0) this.label(lt, x - c.measureText(lt).width - 14, ly);
    if (x + c.measureText(rt).width + 18 < this.cw) this.label(rt, x + 14, ly);
  }

  // ------------------------------------------------------------ 操作
  bind() {
    const cv = this.canvas;
    cv.addEventListener('wheel', e => {
      if (!this.image) return;
      e.preventDefault();
      const r = cv.getBoundingClientRect();
      this.zoomAt(e.clientX - r.left, e.clientY - r.top, Math.pow(1.0018, -e.deltaY));
    }, { passive: false });
    cv.addEventListener('pointerdown', e => this.down(e));
    cv.addEventListener('pointermove', e => this.move(e));
    cv.addEventListener('pointerup', e => this.up(e));
    cv.addEventListener('pointercancel', e => this.up(e, true));
    cv.addEventListener('pointerleave', () => { this.hover = null; this.showHover(null); });
    cv.addEventListener('dblclick', e => {
      if (this.opts.tool === 'polygon' && this.poly.length >= 3) this.finishPolygon();
      else if (this.opts.tool === 'pan') this.fit();
      e.preventDefault();
    });
    cv.addEventListener('keydown', e => {
      if (e.key === 'Escape') {
        if (this.poly.length || this.drag) { this.poly = []; this.drag = null; this.draw(); }
        else this.dotnet.invokeMethodAsync('OnRoi', null, null);
      } else if (e.key === 'Enter' && this.opts.tool === 'polygon' && this.poly.length >= 3) this.finishPolygon();
    });
    cv.addEventListener('contextmenu', e => e.preventDefault());
    // 画像のファイルをドロップして開く
    const host = this.host;
    host.addEventListener('dragover', e => { e.preventDefault(); host.classList.add('dropping'); });
    host.addEventListener('dragleave', () => host.classList.remove('dropping'));
    host.addEventListener('drop', async e => {
      e.preventDefault(); host.classList.remove('dropping');
      const f = e.dataTransfer?.files?.[0]; if (!f) return;
      const bytes = new Uint8Array(await f.arrayBuffer());
      this.dotnet.invokeMethodAsync('OnDrop', f.name, f.type || '', bytes);
    });
  }

  pos(e) { const r = this.canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }

  zoomAt(x, y, f) {
    this.userMoved = true;
    const next = Math.min(64, Math.max(0.02, this.scale * f)); f = next / this.scale;
    this.ox = x - (x - this.ox) * f; this.oy = y - (y - this.oy) * f; this.scale = next; this.draw();
  }

  clampImg(p) { return { x: Math.min(this.iw, Math.max(0, p.x)), y: Math.min(this.ih, Math.max(0, p.y)) }; }

  down(e) {
    if (!this.image) return;
    this.canvas.focus();
    this.canvas.setPointerCapture(e.pointerId);
    const p = this.pos(e);
    this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 2) {
      // 2 本の指: ピンチで拡大・縮小、そのまま移動
      const [a, b] = [...this.pointers.values()];
      this.drag = { kind: 'pinch', dist: Math.hypot(a.x - b.x, a.y - b.y), mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
      this.poly = this.opts.tool === 'polygon' ? this.poly : [];
      return;
    }
    if (this.pointers.size > 2) return;
    const w = this.iw * this.scale, sx = this.ox + w * this.split;
    if (this.opts.comparing && this.compare && Math.abs(p.x - sx) < (e.pointerType === 'touch' ? 22 : 10)) {
      this.drag = { kind: 'split' }; return;
    }
    const tool = this.opts.tool;
    if (e.button === 1 || e.button === 2 || tool === 'pan' || tool === 'exclude') {
      this.drag = { kind: 'pan', start: p, ox: this.ox, oy: this.oy, moved: false, button: e.button, type: e.pointerType };
      return;
    }
    const ip = this.clampImg(this.toImage(p.x, p.y));
    if (tool === 'polygon') {
      if (this.poly.length >= 3) {
        const f = this.toScreen(this.poly[0].x, this.poly[0].y);
        if (Math.hypot(f.x - p.x, f.y - p.y) < (e.pointerType === 'touch' ? 18 : 8)) { this.finishPolygon(); return; }
      }
      this.poly.push(ip); this.draw(); return;
    }
    this.drag = { kind: 'shape', start: ip, now: ip };
  }

  move(e) {
    const p = this.pos(e);
    if (this.pointers.has(e.pointerId)) this.pointers.set(e.pointerId, p);
    const d = this.drag;
    if (d && d.kind === 'pinch' && this.pointers.size >= 2) {
      const [a, b] = [...this.pointers.values()];
      const dist = Math.hypot(a.x - b.x, a.y - b.y), mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      this.ox += mid.x - d.mid.x; this.oy += mid.y - d.mid.y;
      this.zoomAt(mid.x, mid.y, dist / Math.max(d.dist, 1));
      d.dist = dist; d.mid = mid; return;
    }
    if (d && d.kind === 'split') {
      this.split = Math.min(1, Math.max(0, (p.x - this.ox) / (this.iw * this.scale))); this.draw(); return;
    }
    if (d && d.kind === 'pan') {
      const dx = p.x - d.start.x, dy = p.y - d.start.y;
      if (Math.hypot(dx, dy) > (d.type === 'touch' ? 8 : 3)) d.moved = true;
      if (d.moved) { this.ox = d.ox + dx; this.oy = d.oy + dy; this.userMoved = true; this.draw(); }
      return;
    }
    const ip = this.toImage(p.x, p.y);
    this.hover = ip;
    this.queueHover(ip);
    if (d && d.kind === 'shape') {
      let n = this.clampImg(ip);
      if (e.shiftKey) n = this.constrain(d.start, n);
      d.now = n; this.draw();
    } else if (this.opts.tool === 'polygon' && this.poly.length) this.draw();
    if (this.opts.comparing && this.compare && !d) {
      const sx = this.ox + this.iw * this.scale * this.split;
      this.canvas.style.cursor = Math.abs(p.x - sx) < 10 ? 'ew-resize' : (this.opts.tool === 'pan' ? 'default' : this.opts.tool === 'exclude' ? 'pointer' : 'crosshair');
    }
  }

  up(e, cancel) {
    const p = this.pos(e);
    this.pointers.delete(e.pointerId);
    const d = this.drag;
    if (!d) return;
    if (d.kind === 'pinch') { if (this.pointers.size === 0) this.drag = null; return; }
    this.drag = null;
    if (cancel) { this.draw(); return; }
    if (d.kind === 'pan') {
      if (!d.moved && d.button === 0) {
        const ip = this.toImage(p.x, p.y);
        if (ip.x >= 0 && ip.y >= 0 && ip.x < this.iw && ip.y < this.ih) {
          this.dotnet.invokeMethodAsync(this.opts.tool === 'exclude' ? 'OnExclude' : 'OnClick', ip.x, ip.y);
          if (e.pointerType !== 'mouse') this.queueHover(ip);
        }
      }
      return;
    }
    if (d.kind === 'shape') {
      const s = d.start, n = d.now;
      const big = Math.abs(n.x - s.x) * this.scale > 3 || Math.abs(n.y - s.y) * this.scale > 3;
      if (big) {
        const t = this.opts.tool;
        if (t === 'line') this.dotnet.invokeMethodAsync('OnLine', s.x, s.y, n.x, n.y);
        else this.dotnet.invokeMethodAsync('OnRoi', t === 'ellipse' ? 'ellipse' : 'rect',
          [Math.min(s.x, n.x), Math.min(s.y, n.y), Math.max(s.x, n.x), Math.max(s.y, n.y)]);
      }
      this.draw();
    }
  }

  finishPolygon() {
    const flat = []; for (const p of this.poly) flat.push(p.x, p.y);
    this.poly = [];
    this.dotnet.invokeMethodAsync('OnRoi', 'polygon', flat);
    this.draw();
  }

  constrain(s, p) {
    const dx = p.x - s.x, dy = p.y - s.y;
    if (this.opts.tool === 'line') {
      const a = Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), l = Math.hypot(dx, dy);
      return this.clampImg({ x: s.x + Math.cos(a) * l, y: s.y + Math.sin(a) * l });
    }
    const m = Math.max(Math.abs(dx), Math.abs(dy));
    return this.clampImg({ x: s.x + Math.sign(dx) * m, y: s.y + Math.sign(dy) * m });
  }

  // マウスの位置の値は、C# に聞いて直接書く（画面全体を描き直さないため）
  queueHover(ip) {
    this.pendingHover = ip;
    if (this.hoverBusy) return;
    this.hoverBusy = true;
    requestAnimationFrame(async () => {
      const q = this.pendingHover; this.pendingHover = null;
      try { this.showHover(await this.dotnet.invokeMethodAsync('HoverText', q.x, q.y)); } catch { /* 閉じたあと */ }
      this.hoverBusy = false;
      if (this.pendingHover) this.queueHover(this.pendingHover);
    });
  }
  showHover(t) {
    for (const id of (this.host.dataset.hover || '').split(' ')) {
      const el = id && document.getElementById(id);
      if (el) { el.textContent = t || ''; el.classList.toggle('show', !!t); }
    }
  }
}

function roundRect(c, x, y, w, h, r) { c.beginPath(); c.roundRect ? c.roundRect(x, y, w, h, r) : c.rect(x, y, w, h); }
function fmt(v) { const a = Math.abs(v); return a >= 1000 ? Math.round(v).toLocaleString() : a >= 10 ? v.toFixed(1) : a >= 0.1 ? v.toFixed(2) : v.toPrecision(3); }

async function toBitmap(w, h, rgba) {
  const data = new ImageData(new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, w * h * 4), w, h);
  if (window.createImageBitmap) return await createImageBitmap(data);
  const cv = document.createElement('canvas'); cv.width = w; cv.height = h; cv.getContext('2d').putImageData(data, 0, 0); return cv;
}

const viewers = new Map();
export function create(id, dotnet) { const host = document.getElementById(id); const v = new Viewer(host, dotnet); viewers.set(id, v); }
export function dispose(id) { viewers.delete(id); }
export function setImage(id, w, h, rgba) { return viewers.get(id)?.setImage(w, h, rgba); }
export function setOverlay(id, w, h, rgba) { return viewers.get(id)?.setOverlay(w, h, rgba); }
export function setCompare(id, w, h, rgba) { return viewers.get(id)?.setCompare(w, h, rgba); }
export function clear(id) { viewers.get(id)?.clear(); }
export function setOptions(id, o) { viewers.get(id)?.setOptions(o); }
export function setShapes(id, s) { viewers.get(id)?.setShapes(s); }
export function fit(id) { viewers.get(id)?.fit(); }
export function zoomTo(id, s) { viewers.get(id)?.zoomTo(s); }
export function centerOn(id, x, y, size) { viewers.get(id)?.centerOn(x, y, size); }

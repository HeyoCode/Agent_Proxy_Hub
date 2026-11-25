import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';

const root = 'f:\\source\\Agent_Proxy_Hub';
const chromePath = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const brainDir = 'C:\\Users\\wehoo\\.gemini\\antigravity\\brain\\e8a4ee0a-b575-42c2-bab1-251536bcec70';
const sleep = ms => new Promise(r => setTimeout(r, ms));

class CdpClient {
  constructor(wsUrl) {
    this.ws = new WebSocket(wsUrl);
    this.nextId = 1;
    this.pending = new Map();
    this.ws.onmessage = ev => {
      const m = JSON.parse(ev.data);
      if (m.id && this.pending.has(m.id)) {
        const { resolve, reject } = this.pending.get(m.id);
        this.pending.delete(m.id);
        if (m.error) reject(new Error(m.error.message));
        else resolve(m.result);
      }
    };
  }
  async open() {
    if (this.ws.readyState === WebSocket.OPEN) return;
    await new Promise((res, rej) => { this.ws.onopen = res; this.ws.onerror = rej; });
  }
  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((res, rej) => {
      this.pending.set(id, { resolve: res, reject: rej });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }
  close() { this.ws.close(); }
}

async function waitJson(url) {
  for (let i = 0; i < 40; i++) {
    try {
      const r = await fetch(url);
      if (r.ok) return await r.json();
    } catch {}
    await sleep(250);
  }
  throw new Error('Timeout waiting for CDP');
}

function encodeBmp24(width, height, rgba) {
  const rowStride = Math.ceil((width * 3) / 4) * 4;
  const pixelBytes = rowStride * height;
  const fileSize = 54 + pixelBytes;
  const buf = Buffer.alloc(fileSize);

  // BITMAPFILEHEADER
  buf.write('BM', 0, 'ascii');
  buf.writeUInt32LE(fileSize, 2);
  buf.writeUInt32LE(0, 6);
  buf.writeUInt32LE(54, 10);

  // BITMAPINFOHEADER
  buf.writeUInt32LE(40, 14);
  buf.writeInt32LE(width, 18);
  buf.writeInt32LE(height, 22); // 正数表示自底向上
  buf.writeUInt16LE(1, 26);
  buf.writeUInt16LE(24, 28);
  buf.writeUInt32LE(0, 30);
  buf.writeUInt32LE(pixelBytes, 34);
  buf.writeInt32LE(2835, 38);
  buf.writeInt32LE(2835, 42);

  for (let y = 0; y < height; y++) {
    const srcRow = (height - 1 - y) * width * 4;
    const dstRow = 54 + y * rowStride;
    for (let x = 0; x < width; x++) {
      const s = srcRow + x * 4;
      const d = dstRow + x * 3;
      buf[d] = rgba[s + 2];     // B
      buf[d + 1] = rgba[s + 1]; // G
      buf[d + 2] = rgba[s];     // R
    }
  }
  return buf;
}

function buildIco(entries) {
  const count = entries.length;
  const headerSize = 6 + count * 16;
  const totalSize = headerSize + entries.reduce((s, e) => s + e.png.length, 0);
  const buf = Buffer.alloc(totalSize);

  buf.writeUInt16LE(0, 0);
  buf.writeUInt16LE(1, 2);
  buf.writeUInt16LE(count, 4);

  let offset = headerSize;
  entries.forEach((entry, idx) => {
    const base = 6 + idx * 16;
    buf[base] = entry.size === 256 ? 0 : entry.size;
    buf[base + 1] = entry.size === 256 ? 0 : entry.size;
    buf[base + 2] = 0;
    buf[base + 3] = 0;
    buf.writeUInt16LE(1, base + 4);
    buf.writeUInt16LE(32, base + 6);
    buf.writeUInt32LE(entry.png.length, base + 8);
    buf.writeUInt32LE(offset, base + 12);
    entry.png.copy(buf, offset);
    offset += entry.png.length;
  });

  return buf;
}

const html = `<!DOCTYPE html>
<html>
<head><meta charset="utf-8" /></head>
<body style="margin:0;background:#0A0A0D;">
<script>
function drawLogoOnCtx(ctx, size) {
  const s = size / 512;
  ctx.save();
  ctx.scale(s, s);
  const pad = 20;
  const w = 472;
  const r = 112;

  // 背景渐层圆角矩形
  const bgGrad = ctx.createLinearGradient(0, 0, 0, 512);
  bgGrad.addColorStop(0, '#1E1E26');
  bgGrad.addColorStop(1, '#0B0B0F');

  ctx.beginPath();
  ctx.roundRect(pad, pad, w, w, r);
  ctx.fillStyle = bgGrad;
  ctx.fill();
  ctx.lineWidth = 10;
  ctx.strokeStyle = '#323240';
  ctx.stroke();

  // 内高光环
  ctx.beginPath();
  ctx.roundRect(44, 44, 424, 424, 90);
  ctx.lineWidth = 4;
  ctx.strokeStyle = 'rgba(255,255,255,0.09)';
  ctx.stroke();

  // 翡翠盾牌
  const shGrad = ctx.createLinearGradient(128, 96, 384, 428);
  shGrad.addColorStop(0, '#34D399');
  shGrad.addColorStop(1, '#059669');
  ctx.beginPath();
  ctx.moveTo(256, 96);
  ctx.lineTo(128, 148);
  ctx.lineTo(128, 248);
  ctx.bezierCurveTo(128, 334, 182, 406, 256, 428);
  ctx.bezierCurveTo(330, 406, 384, 334, 384, 248);
  ctx.lineTo(384, 148);
  ctx.closePath();
  ctx.fillStyle = 'rgba(16, 185, 129, 0.14)';
  ctx.fill();
  ctx.lineWidth = 24;
  ctx.lineJoin = 'round';
  ctx.strokeStyle = shGrad;
  ctx.stroke();

  // 路由节点核
  ctx.strokeStyle = '#6EE7B7';
  ctx.lineWidth = 14;
  ctx.lineCap = 'round';
  [[256,174,256,206],[256,290,256,326],[182,248,214,248],[298,248,330,248]].forEach(([x1,y1,x2,y2]) => {
    ctx.beginPath(); ctx.moveTo(x1,y1); ctx.lineTo(x2,y2); ctx.stroke();
  });

  ctx.beginPath(); ctx.arc(256, 248, 42, 0, Math.PI * 2);
  ctx.fillStyle = '#34D399'; ctx.fill();
  ctx.beginPath(); ctx.arc(256, 248, 18, 0, Math.PI * 2);
  ctx.fillStyle = '#0B0B0F'; ctx.fill();

  ctx.restore();
}

window.renderIconPng = (size) => {
  const c = document.createElement('canvas');
  c.width = size; c.height = size;
  const ctx = c.getContext('2d');
  drawLogoOnCtx(ctx, size);
  return c.toDataURL('image/png').split(',')[1];
};

window.renderBannerRgba = () => {
  const w = 328, h = 628;
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  const ctx = c.getContext('2d');

  // 黑曜石点阵背景
  const g = ctx.createLinearGradient(0, 0, 0, h);
  g.addColorStop(0, '#0E0E13');
  g.addColorStop(1, '#08080B');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, w, h);

  ctx.fillStyle = 'rgba(255,255,255,0.065)';
  for (let y = 12; y < h; y += 16) {
    for (let x = 12; x < w; x += 16) {
      ctx.fillRect(x, y, 1.5, 1.5);
    }
  }

  // 右上微弱翡翠环境辉光（极克制）
  const glow = ctx.createRadialGradient(164, 120, 10, 164, 120, 190);
  glow.addColorStop(0, 'rgba(16,185,129,0.13)');
  glow.addColorStop(1, 'rgba(16,185,129,0)');
  ctx.fillStyle = glow;
  ctx.fillRect(0, 0, w, 340);

  // 中央居中徽章
  ctx.save();
  ctx.translate(104, 52);
  drawLogoOnCtx(ctx, 120);
  ctx.restore();

  // 品牌标题与版本
  ctx.textAlign = 'center';
  ctx.fillStyle = '#F4F4F6';
  ctx.font = '700 22px -apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", sans-serif';
  ctx.fillText('Agent Proxy Hub', w / 2, 206);

  ctx.fillStyle = '#34D399';
  ctx.font = '600 13px Consolas, monospace';
  ctx.fillText('v0.2.0 · .NET 10 Core', w / 2, 230);

  ctx.fillStyle = '#A1A1AA';
  ctx.font = '400 13px "Microsoft YaHei", sans-serif';
  ctx.fillText('AI 专用独立出口网关', w / 2, 256);

  // 同心圆角特性触感卡片
  ctx.beginPath();
  ctx.roundRect(22, 286, w - 44, 242, 18);
  ctx.fillStyle = 'rgba(255,255,255,0.035)';
  ctx.fill();
  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(255,255,255,0.09)';
  ctx.stroke();

  ctx.beginPath();
  ctx.roundRect(27, 291, w - 54, 232, 14);
  ctx.fillStyle = '#141419';
  ctx.fill();
  ctx.strokeStyle = 'rgba(255,255,255,0.08)';
  ctx.stroke();

  const features = [
    ['物理网卡硬件直绑', '绕过 Clash / Wintun / utun'],
    ['防 Fake-IP SNI 还原', '独立 DNS + Fail-Close 熔断'],
    ['全协议与批量导入', 'SOCKS5 / HTTP / HTTPS'],
    ['CC Switch 级统计', '免解密本地日志双源汇总']
  ];
  ctx.textAlign = 'left';
  features.forEach((f, i) => {
    const y = 326 + i * 52;
    ctx.beginPath();
    ctx.arc(46, y - 4, 4, 0, Math.PI * 2);
    ctx.fillStyle = '#10B981';
    ctx.fill();

    ctx.fillStyle = '#ECEFF4';
    ctx.font = '600 13px "Microsoft YaHei", sans-serif';
    ctx.fillText(f[0], 58, y);

    ctx.fillStyle = '#8B95A5';
    ctx.font = '400 11.5px "Microsoft YaHei", Consolas, sans-serif';
    ctx.fillText(f[1], 58, y + 18);
  });

  // 底部 GitHub 铭牌
  ctx.textAlign = 'center';
  ctx.fillStyle = '#71717A';
  ctx.font = '500 11.5px Consolas, monospace';
  ctx.fillText('github.com/HeyoCode/Agent_Proxy_Hub', w / 2, 592);

  return Array.from(ctx.getImageData(0, 0, w, h).data);
};

window.renderSmallRgba = () => {
  const w = 110, h = 110;
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  const ctx = c.getContext('2d');
  ctx.fillStyle = '#F4F4F6';
  ctx.fillRect(0, 0, w, h);
  ctx.save();
  ctx.translate(11, 11);
  drawLogoOnCtx(ctx, 88);
  ctx.restore();
  return Array.from(ctx.getImageData(0, 0, w, h).data);
};
</script>
</body>
</html>`;

const userDataDir = path.join(brainDir, 'scratch', 'chrome-assets');
fs.mkdirSync(userDataDir, { recursive: true });
const port = 19228;
const chrome = spawn(chromePath, [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  `--remote-debugging-port=${port}`, `--user-data-dir=${userDataDir}`, 'about:blank'
], { stdio: 'ignore' });

try {
  const targets = await waitJson(`http://127.0.0.1:${port}/json/list`);
  const page = targets.find(t => t.type === 'page') || targets[0];
  const cdp = new CdpClient(page.webSocketDebuggerUrl);
  await cdp.open();

  await cdp.send('Page.navigate', { url: 'data:text/html;charset=utf-8,' + encodeURIComponent(html) });
  await sleep(300);

  // 1. 生成 512px PNG & 多尺寸 ICO
  const sizes = [16, 32, 48, 64, 256];
  const icoEntries = [];
  for (const sz of sizes) {
    const r = await cdp.send('Runtime.evaluate', { expression: `window.renderIconPng(${sz})`, returnByValue: true });
    icoEntries.push({ size: sz, png: Buffer.from(r.result.value, 'base64') });
  }
  const png512 = await cdp.send('Runtime.evaluate', { expression: `window.renderIconPng(512)`, returnByValue: true });
  fs.writeFileSync(path.join(root, 'assets', 'icon-512.png'), Buffer.from(png512.result.value, 'base64'));
  fs.writeFileSync(path.join(root, 'assets', 'app.ico'), buildIco(icoEntries));
  console.log('Generated assets/icon-512.png and assets/app.ico (5 resolutions)');

  // 2. 生成标准 24-bit BMP 向导素材
  const bannerRes = await cdp.send('Runtime.evaluate', { expression: `window.renderBannerRgba()`, returnByValue: true });
  const bannerBmp = encodeBmp24(328, 628, bannerRes.result.value);
  fs.writeFileSync(path.join(root, 'installer', 'windows', 'assets', 'wizard-banner.bmp'), bannerBmp);

  const smallRes = await cdp.send('Runtime.evaluate', { expression: `window.renderSmallRgba()`, returnByValue: true });
  const smallBmp = encodeBmp24(110, 110, smallRes.result.value);
  fs.writeFileSync(path.join(root, 'installer', 'windows', 'assets', 'wizard-small.bmp'), smallBmp);
  console.log('Generated wizard-banner.bmp (328x628) and wizard-small.bmp (110x110)');

  // 3. 渲染安装向导视觉预览图供直接人工审阅
  const previewHtml = `<!DOCTYPE html><html><head><meta charset="utf-8"><style>
    body{margin:0;background:#1A1B20;font-family:"Microsoft YaHei",sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;}
    .win{width:760px;height:520px;background:#FFFFFF;border-radius:10px;box-shadow:0 24px 60px rgba(0,0,0,0.45);overflow:hidden;display:flex;flex-direction:column;}
    .bar{height:34px;background:#F3F4F6;border-bottom:1px solid #E5E7EB;display:flex;align-items:center;justify-content:space-between;padding:0 12px;font-size:12px;color:#111827;}
    .body{flex:1;display:flex;min-height:0;}
    .banner{width:248px;height:100%;object-fit:cover;}
    .right{flex:1;padding:28px 30px;display:flex;flex-direction:column;justify-content:space-between;background:#FFFFFF;color:#111827;}
    .foot{height:52px;background:#F8FAFC;border-top:1px solid #E5E7EB;display:flex;align-items:center;justify-content:space-between;padding:0 20px;}
    .btn{padding:7px 18px;border-radius:7px;font-size:12.5px;border:1px solid #D1D5DB;background:#FFF;}
    .btn.p{background:#151519;color:#FFF;border-color:#111;font-weight:600;}
  </style></head><body><div class="win">
    <div class="bar"><span>安装 — Agent Proxy Hub v0.2.0</span><span>—  □  ✕</span></div>
    <div class="body">
      <img class="banner" src="data:image/png;base64,${await cdp.send('Runtime.evaluate', { expression: `(() => { const c = document.createElement('canvas'); c.width=328; c.height=628; const d=window.renderBannerRgba(); const id=new ImageData(new Uint8ClampedArray(d),328,628); c.getContext('2d').putImageData(id,0,0); return c.toDataURL('image/png').split(',')[1]; })()`, returnByValue: true }).then(x => x.result.value)}" />
      <div class="right">
        <div>
          <h2 style="margin:0 0 12px;font-size:20px;">欢迎安装 Agent Proxy Hub</h2>
          <p style="font-size:13px;color:#4B5563;line-height:1.65;">将在您的计算机上安装 <strong>Agent Proxy Hub v0.2.0</strong>（Claude / ChatGPT / Antigravity 独立出口网关与双源用量统计工具）。</p>
          <div style="margin-top:18px;padding:12px 14px;border-radius:8px;background:#F3F4F6;border:1px solid #E5E7EB;font-size:12px;color:#374151;line-height:1.7;">
            <div>✓ 默认用户级目录安装（无需管理员弹窗）</div>
            <div>✓ 物理网卡硬件直绑，零篡改系统代理设置</div>
            <div>✓ 支持开机静默驻留托盘（--minimized）</div>
          </div>
        </div>
        <div style="font-size:11.5px;color:#6B7280;">单击“下一步”选择安装位置与启动选项。</div>
      </div>
    </div>
    <div class="foot">
      <span style="font-size:11.5px;color:#6B7280;">github.com/HeyoCode/Agent_Proxy_Hub</span>
      <div style="display:flex;gap:8px;"><span class="btn p">下一步(N) &gt;</span><span class="btn">取消</span></div>
    </div>
  </div></body></html>`;

  await cdp.send('Page.navigate', { url: 'data:text/html;charset=utf-8,' + encodeURIComponent(previewHtml) });
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 880, height: 600, deviceScaleFactor: 2, mobile: false });
  await sleep(300);
  const shot = await cdp.send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(path.join(brainDir, 'installer_preview.png'), Buffer.from(shot.data, 'base64'));
  console.log('Generated installer_preview.png');

  cdp.close();
} finally {
  chrome.kill();
}

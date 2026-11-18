// 렌더 룩 콘솔 화면 (ProductRenderStudio 패널 시안과 같은 구성)
// 흐름: 0.25초마다 /api/state로 유니티 상태를 받아 그림 → 고르거나 슬라이더·끌어놓기를 하면 /api/cmd로 보냄
//       가운데 렌더 뷰는 /stream (유니티 카메라 그림, UI 없이)
// 슬라이더를 움직이는 동안은 유니티 값으로 덮지 않음 (손에서 놓고 0.8초 뒤부터 다시 맞춤)

const $ = (id) => document.getElementById(id);
const LOOK_SLIDERS = [
  ["ambient", "환경광", 0, 2, 0.05], ["reflection", "반사", 0, 2, 0.05], ["sceneLight", "원래 조명", 0, 2, 0.05],
  ["exposure", "노출", -2, 2, 0.05], ["contrast", "대비", -50, 50, 1], ["saturation", "채도", -60, 60, 1],
  ["temperature", "색온도", -60, 60, 1], ["tint", "색조", -60, 60, 1], ["bloom", "빛 번짐", 0, 1, 0.01],
];
const MAT_SLIDERS = [
  ["metallic", "금속성", 0, 1, 0.01, "rMetal"], ["roughness", "거칠기", 0, 1, 0.01, "rRough"],
  ["normalStrength", "결 세기", 0, 1, 0.01, "rNormal"], ["tiling", "결 크기", 0.25, 4, 0.05, null],
];
const MODE_NOTE = {
  original: "장면에 원래 있던 재질 그대로예요.",
  replace: "프리셋 색·질감으로 바꿔요. 원래 사진 텍스처는 빠져요.",
  keep: "원래 색·텍스처는 두고 금속성·거칠기·결만 바꿔요.",
};

let webVersion = 0;
let S = null, sel = null, editAt = 0, lastSeq = -1, showAuto = false, lastMsg = "", wasConn = false;
const fresh = () => performance.now() - editAt > 800;
const post = (cmd) => fetch("/api/cmd", { method: "POST", body: JSON.stringify(cmd) }).catch(() => {});
const pend = {};
function throttled(key, make) {   // 같은 종류 명령은 0.05초에 한 번 (마지막 값은 꼭 보냄)
  editAt = performance.now();
  if (pend[key]) { pend[key].make = make; return; }
  post(make()); pend[key] = { make: null };
  setTimeout(() => { const p = pend[key]; delete pend[key]; if (p && p.make) throttled(key, p.make); }, 50);
}
const hex = (c) => "#" + (c || [0.8, 0.8, 0.8]).slice(0, 3).map(v => Math.round(Math.min(1, Math.max(0, v)) * 255).toString(16).padStart(2, "0")).join("");
const unhex = (h) => [1, 3, 5].map(i => +(parseInt(h.substr(i, 2), 16) / 255).toFixed(3));
const fmt = (v, step) => step >= 1 ? String(Math.round(v)) : (+v).toFixed(2);
const esc = (t) => String(t ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

// ---------------------------------------------------------------- 재질 공 그림
// 썸네일이 있으면 그 사진, 없으면 색·금속·거칠기로 그린 공
function cssBall(color, metal, rough) {
  const c = hex(color), shine = Math.round((1 - rough) * 90 + 10), dark = metal > 0.5 ? 0.18 : 0.42;
  return `background:radial-gradient(circle at 34% 28%, rgba(255,255,255,${shine / 100}) 0, rgba(255,255,255,0) ${18 + rough * 30}%),`
       + `radial-gradient(circle at 50% 55%, ${c} 0, ${c} 45%, rgba(0,0,0,${dark}) 100%), ${c}`;
}
function ball(kind, x, cls = "ball") {
  if (kind === "mat" && x.thumb) return `<img class="${cls}" src="/assets/${x.thumb}" alt="" draggable="false">`;
  if (kind === "mat") return `<span class="${cls}" style="${cssBall(x.color, x.metallic, x.roughness)}"></span>`;
  if (kind === "part") return `<span class="${cls}" style="${cssBall(x.color, x.metallic, 1 - x.smoothness)}"></span>`;
  return `<span class="${cls}" style="${cssBall(x.color, x.metallic, x.roughness)}"></span>`;   // state 값
}

// ---------------------------------------------------------------- 상태 읽기 도우미
const preset = (id) => S.materials.find(m => m.id === id);
function partState(id) {
  return (S.states || []).find(s => s.id === id) || { id, mode: "original", preset: "", metallic: 0, roughness: 0.5, normalStrength: 0, tiling: 1, color: [0.8, 0.8, 0.8] };
}
function source(st) {   // orig | preset | custom
  if (st.mode === "original") return "orig";
  const m = preset(st.preset);
  if (!m) return "custom";
  const near = (a, b) => Math.abs(a - b) < 0.006;
  return near(st.metallic, m.metallic) && near(st.roughness, m.roughness) && (st.mode === "keep" || (st.color || []).every((v, i) => near(v, m.color[i]))) ? "preset" : "custom";
}
const TAG = { orig: "원본", preset: "기본", custom: "커스텀" };
function matLabel(p, st) {
  const src = source(st), m = preset(st.preset);
  if (src === "orig") return p.auto ? "장면 재질 그대로" : "원래 재질";
  return (m ? m.name : st.preset) + (src === "custom" ? " (수정)" : "") + (st.mode === "keep" ? " · 텍스처 유지" : "");
}
function partBall(p, st, cls) {
  const src = source(st), m = preset(st.preset);
  if (src === "orig") return ball("part", p, cls);
  if (src === "preset" && m && st.mode === "replace") return ball("mat", m, cls);
  return ball("state", st.mode === "keep" ? Object.assign({}, st, { color: p.color }) : st, cls);
}

// ---------------------------------------------------------------- 슬라이더 (시안과 같은 모양, 파란 띠 = 프리셋 허용 범위)
// getDefault() = 이 값의 기본값 (없으면 null) → ↺ 버튼으로 그 값만 되돌림
function slider(parent, [key, label, min, max, step], onInput, getDefault) {
  const el = document.createElement("div");
  el.className = "slider";
  el.innerHTML = `<div class="top"><strong>${label}</strong><em><span class="v"></span><span class="r"></span>
      <button class="undo" title="기본값으로 되돌리기" aria-label="${label} 기본값으로 되돌리기">↺</button></em></div>
    <div class="track"><div class="rail"></div><div class="bandv hidden"></div><input type="range" min="${min}" max="${max}" step="${step}"></div>`;
  const input = el.querySelector("input"), v = el.querySelector(".v"), band = el.querySelector(".bandv"), r = el.querySelector(".r"), undo = el.querySelector(".undo");
  const pct = (x) => (x - min) / (max - min) * 100;
  const mark = () => { const d = getDefault && getDefault(); undo.classList.toggle("show", d != null && Math.abs(+input.value - d) > step / 2); undo.title = d != null ? `기본값 ${fmt(d, step)}으로 되돌리기` : ""; };
  input.oninput = () => { v.textContent = fmt(input.value, step); mark(); onInput(key, +input.value); };
  undo.onclick = () => { const d = getDefault && getDefault(); if (d == null) return; input.value = d; input.oninput(); };
  el.set = (val) => { input.value = val; v.textContent = fmt(val, step); mark(); };
  el.mark = mark;
  el.range = (rg) => {
    const ok = rg && rg.length === 2;
    band.classList.toggle("hidden", !ok);
    r.textContent = ok ? ` · 범위 ${+(+rg[0]).toFixed(2)}–${+(+rg[1]).toFixed(2)}` : "";
    if (ok) { band.style.left = pct(rg[0]) + "%"; band.style.width = (pct(rg[1]) - pct(rg[0])) + "%"; }
  };
  el.enable = (on) => el.classList.toggle("off", !on);
  parent.appendChild(el);
  return el;
}

// ---------------------------------------------------------------- 룩
const lookSl = {};
LOOK_SLIDERS.forEach(d => {
  lookSl[d[0]] = slider($("lookSliders"), d, () => {
    const look = Object.assign({}, S && S.lookParams);   // 슬라이더에 없는 값(색 맞추기 lift·gamma·gain)은 그대로
    for (const k in lookSl) look[k] = +lookSl[k].querySelector("input").value;
    throttled("look", () => ({ cmd: "look_params", look }));
  }, () => S && S.look && S.lookDefaults ? S.lookDefaults[d[0]] : null);
});

function drawLooks() {
  const items = [{ id: "", name: "원래 조명", note: "장면 그대로" }, ...S.looks];
  $("lookCount").textContent = `${S.looks.length}개`;
  $("looks").innerHTML = items.map(l => `<button class="look${(S.look || "") === l.id ? " on" : ""}${l.thumb ? "" : " plain"}" data-id="${esc(l.id)}">
      ${l.thumb ? `<img class="ph" src="/assets/${l.thumb}" alt="">` : `<span class="ph"></span>`}
      <b>${esc(l.name)}</b><small>${esc(l.id ? (l.note || l.id) : l.note)}</small></button>`).join("");
  $("looks").querySelectorAll(".look").forEach(b => b.onclick = () => { editAt = 0; post({ cmd: "look", id: b.dataset.id }); });
  const cur = S.looks.find(l => l.id === S.look);
  $("lookNote").textContent = cur ? cur.note || "" : "룩을 고르면 조명·반사·후처리가 한 번에 바뀌어요. 처음 고른 룩은 사진이 자동으로 찍혀요.";
  $("lookHint").textContent = cur ? "움직이면 바로 반영" : "룩을 고르면 조절할 수 있어요";
  for (const k in lookSl) {
    lookSl[k].enable(!!cur);
    if (fresh()) lookSl[k].set(cur && S.lookParams ? S.lookParams[k] : 0);
  }
}

// ---------------------------------------------------------------- 부위 목록
function partRow(p) {
  const st = partState(p.id), src = source(st);
  return `<div class="part${sel === p.id && document.body.classList.contains("bench-open") ? " sel" : ""}${p.found ? "" : " gone"}" data-id="${esc(p.id)}" title="${p.found ? `${p.count}곳` : "지금 장면에 안 보여요"}">
    ${partBall(p, st)}<div style="min-width:0"><b>${esc(p.label)}</b><small><span class="tag ${src}">${TAG[src]}</span>${esc(matLabel(p, st))}</small></div></div>`;
}

function drawParts() {
  const lab = S.parts.filter(p => !p.auto), auto = S.parts.filter(p => p.auto);
  $("labeled").innerHTML = lab.length ? lab.map(partRow).join("")
    : `<p class="hint">이름표가 없어요. 아래 "장면에서 찾은 재질"에서 고르거나 parts/${esc(S.scene)}.json으로 이름을 붙여요.</p>`;
  if (!lab.length) showAuto = true;
  const q = $("search").value.trim().toLowerCase();
  $("toggleAuto").textContent = `${auto.length}개 ${showAuto ? "접기" : "펼치기"}`;
  $("autoWrap").classList.toggle("hidden", !showAuto);
  $("auto").innerHTML = showAuto ? auto.filter(p => !q || p.label.toLowerCase().includes(q)).map(partRow).join("") : "";
  document.querySelectorAll(".part").forEach(r => {
    r.onclick = () => { sel = r.dataset.id; editAt = 0; openBench(); };
    dropZone(r, id => { sel = r.dataset.id; applyPreset(id); });
  });
}
$("toggleAuto").onclick = () => { showAuto = !showAuto; drawParts(); };
$("search").oninput = () => drawParts();

// ---------------------------------------------------------------- 재질 작업대
const matSl = {};
MAT_SLIDERS.forEach(d => {
  matSl[d[0]] = slider($("sliders"), d, () => sendPart(false), () => {
    const st = sel && partState(sel), m = st && st.mode !== "original" && preset(st.preset);
    return m ? m[d[0]] : null;
  });
});
const colorDefault = () => { const st = sel && partState(sel), m = st && st.mode === "replace" && preset(st.preset); return m ? hex(m.color) : null; };
const markColor = () => { const d = colorDefault(); $("cUndo").classList.toggle("show", !!d && d !== $("c").value); };
$("c").oninput = (e) => { $("cHex").textContent = e.target.value.toUpperCase(); markColor(); sendPart(false); };
$("cUndo").onclick = () => { const d = colorDefault(); if (!d) return; $("c").value = d; $("c").oninput({ target: $("c") }); };

function current() {
  const st = partState(sel);
  const out = { id: sel, mode: st.mode, preset: st.preset || "", color: unhex($("c").value) };
  for (const k in matSl) out[k] = +matSl[k].querySelector("input").value;
  return out;
}

function sendPart(pick, patch = {}) {
  if (!sel) return;
  const part = Object.assign(current(), patch);
  if (pick || patch.mode) {
    editAt = 0;
    S.states = (S.states || []).filter(s => s.id !== sel).concat([part]);   // 화면 먼저 바꿔 둠 (깜빡임 방지)
    post({ cmd: "part", part, pick });
    drawParts(); drawBench();
  } else {
    S.states = (S.states || []).filter(s => s.id !== sel).concat([part]);
    throttled("part:" + sel, () => ({ cmd: "part", part, pick: false }));
    drawParts(); drawBenchHead();
  }
}

function applyPreset(id) {
  if (!S || !preset(id)) return;   // 재질 카드만 받음 (그림·글자를 끌어다 놓은 건 무시)
  const st = partState(sel);
  sendPart(true, { preset: id, mode: st.mode === "original" ? "replace" : st.mode });
  const p = S.parts.find(x => x.id === sel), m = preset(id);
  toast(`${p ? p.label : sel}에 ${m ? m.name : id} 적용`);
  openBench();
}

function drawBenchHead() {
  const p = sel && S.parts.find(x => x.id === sel);
  if (!p) return;
  const st = partState(sel), m = preset(st.preset);
  $("selName").textContent = p.label;
  $("big").innerHTML = partBall(p, st, "ball");
  $("matName").textContent = matLabel(p, st);
  $("matNote").textContent = st.mode === "original" ? MODE_NOTE.original : (m && m.note ? m.note : MODE_NOTE[st.mode]);
  document.querySelectorAll("#seg button").forEach(b => b.classList.toggle("on", b.dataset.m === st.mode));
}

function drawBench() {
  const p = sel && S.parts.find(x => x.id === sel);
  if (!p) { document.body.classList.remove("bench-open"); return; }
  drawBenchHead();
  const st = partState(sel), m = preset(st.preset), on = st.mode !== "original";
  $("rangeHint").textContent = !on ? "원본은 고칠 수 없어요 · 재질을 골라 보세요" : m ? "파란 띠 = 프리셋 권장 범위" : "";
  $("colorRow").classList.toggle("off", st.mode !== "replace");
  if (fresh()) { $("c").value = hex(st.mode === "keep" ? p.color : st.color); $("cHex").textContent = st.mode === "keep" ? "원래 색 유지" : $("c").value.toUpperCase(); }
  markColor();
  MAT_SLIDERS.forEach(([k, , , , , rk]) => {
    matSl[k].enable(on);
    matSl[k].range(on && m && rk ? m[rk] : null);
    if (fresh()) matSl[k].set(on ? st[k] : (k === "metallic" ? p.metallic : k === "roughness" ? 1 - p.smoothness : k === "tiling" ? 1 : 0));
  });
  $("lib").innerHTML = S.materials.map(c => `<div class="mcard${on && st.preset === c.id ? " on" : ""}" draggable="true" data-id="${esc(c.id)}" title="${esc(c.note || "")}">${ball("mat", c)}<b>${esc(c.name)}</b></div>`).join("");
  $("lib").querySelectorAll(".mcard").forEach(c => {
    c.addEventListener("dragstart", e => e.dataTransfer.setData("text/plain", c.dataset.id));
    c.onclick = () => applyPreset(c.dataset.id);
  });
}

document.querySelectorAll("#seg button").forEach(b => b.onclick = () => {
  const st = partState(sel);
  if (b.dataset.m === st.mode) return;
  sendPart(b.dataset.m !== "original" && !st.preset, { mode: b.dataset.m });
});

function openBench() { document.body.classList.remove("lib-open"); document.body.classList.add("bench-open"); drawParts(); drawBench(); }
$("openBench").onclick = () => { if (!sel && S) sel = (S.parts[0] || {}).id; openBench(); };
$("closeBench").onclick = () => { document.body.classList.remove("bench-open"); drawParts(); };

function dropZone(el, onDrop) {
  el.addEventListener("dragover", e => { e.preventDefault(); el.classList.add("drop"); });
  el.addEventListener("dragleave", () => el.classList.remove("drop"));
  el.addEventListener("drop", e => { e.preventDefault(); el.classList.remove("drop"); const id = e.dataTransfer.getData("text/plain"); if (id) onDrop(id); });
}
dropZone($("big"), id => applyPreset(id));
dropZone($("stage"), id => { if (sel) applyPreset(id); else toast("먼저 왼쪽에서 부위를 고르세요"); });

// ---------------------------------------------------------------- 아래 버튼
$("resetAll").onclick = () => { editAt = 0; post({ cmd: "reset" }); };
$("resetPart").onclick = () => { if (sel) sendPart(false, { mode: "original" }); };
$("saveLook").onclick = () => {
  if (!S || !S.look) return toast("먼저 룩을 고르세요");
  const name = prompt("새 룩 이름", ((S.looks.find(l => l.id === S.look) || {}).name || "") + " (조절)");
  if (name) post({ cmd: "save_look", name });
};
$("saveMat").onclick = () => {
  const st = sel && partState(sel);
  if (!st || st.mode === "original") return toast("재질을 먼저 골라야 저장할 수 있어요");
  const m = preset(st.preset);
  const name = prompt("새 재질 이름", (m ? m.name : "새 재질") + " (조절)");
  if (name) post({ cmd: "save_material", id: sel, name });
};

// ---------------------------------------------------------------- 테마 · 알림
function setTheme(t) {
  document.documentElement.dataset.theme = t;
  document.querySelectorAll(".toggle button").forEach(x => x.classList.toggle("on", x.dataset.t === t));
  try { localStorage.setItem("rlc-theme-v2", t); } catch (e) {}
}
document.querySelectorAll(".toggle button").forEach(b => b.onclick = () => setTheme(b.dataset.t));
try { setTheme(localStorage.getItem("rlc-theme-v2") || "dark"); } catch (e) { setTheme("dark"); }

let toastTimer = 0;
function toast(t) {
  $("toastText").textContent = t; $("toast").classList.remove("fade");
  clearTimeout(toastTimer); toastTimer = setTimeout(() => $("toast").classList.add("fade"), 4000);
}

// ---------------------------------------------------------------- 렌더 뷰
// 유니티는 이 화면 크기(픽셀) 그대로 그려 보냄 → 선명. 확대·이동도 유니티가 그 부분을 다시 그림 (그림을 늘리지 않음)
//   휠 = 마우스 있는 곳으로 확대/축소 · 끌기 = 이동 · 더블클릭 / [맞춤] = 전체 · Tab / [패널 숨기기] = 렌더 뷰만 크게
let viewOn = false;
const V = { zoom: 1, cx: 0.5, cy: 0.5 };
function startView() {
  if (viewOn) return;
  viewOn = true;
  const img = $("view");
  img.onload = () => { img.classList.remove("hidden"); $("empty").classList.add("hidden"); };
  img.onerror = () => { img.classList.add("hidden"); $("empty").classList.remove("hidden"); viewOn = false; };
  img.src = "/stream?" + Date.now();
  sendView();
}
function clampView() {
  V.zoom = Math.min(12, Math.max(1, V.zoom));
  const h = 0.5 / V.zoom;
  V.cx = Math.min(1 - h, Math.max(h, V.cx)); V.cy = Math.min(1 - h, Math.max(h, V.cy));
  $("zoomVal").textContent = Math.round(V.zoom * 100) + "%";
}
function sendView() {
  clampView();
  const box = $("stage").getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
  const keep = editAt;   // 확대·이동은 슬라이더 맞춤을 막지 않음
  throttled("view", () => ({ cmd: "view", w: Math.round(box.width * dpr), h: Math.round(box.height * dpr), zoom: V.zoom, cx: V.cx, cy: V.cy }));
  editAt = keep;
}
function zoomAt(factor, ux = 0.5, uy = 0.5) {   // ux·uy = 화면에서 고정할 점 (0~1)
  const px = V.cx + (ux - 0.5) / V.zoom, py = V.cy + (uy - 0.5) / V.zoom;
  V.zoom *= factor; clampView();
  V.cx = px - (ux - 0.5) / V.zoom; V.cy = py - (uy - 0.5) / V.zoom;
  sendView();
}
const stage = $("stage");
stage.addEventListener("wheel", e => {
  if (onOverlay(e)) return;
  e.preventDefault();
  const r = stage.getBoundingClientRect();
  zoomAt(Math.pow(1.0015, -e.deltaY), (e.clientX - r.left) / r.width, (e.clientY - r.top) / r.height);
}, { passive: false });
let drag = null;
const onOverlay = (e) => e.target.closest(".hud, .toggle, .toast, .chip-live");   // 렌더 뷰 위 버튼은 끌기·확대 안 함
stage.addEventListener("pointerdown", e => { if (e.button !== 0 || onOverlay(e)) return; drag = { x: e.clientX, y: e.clientY }; stage.setPointerCapture(e.pointerId); stage.classList.add("grab"); });
stage.addEventListener("pointermove", e => {
  if (!drag) return;
  const r = stage.getBoundingClientRect();
  V.cx -= (e.clientX - drag.x) / r.width / V.zoom; V.cy -= (e.clientY - drag.y) / r.height / V.zoom;
  drag = { x: e.clientX, y: e.clientY }; sendView();
});
stage.addEventListener("pointerup", () => { drag = null; stage.classList.remove("grab"); });
stage.addEventListener("dblclick", (e) => { if (onOverlay(e)) return; V.zoom = 1; V.cx = V.cy = 0.5; sendView(); });
$("zoomIn").onclick = () => zoomAt(1.5);
$("zoomOut").onclick = () => zoomAt(1 / 1.5);
$("zoomFit").onclick = () => { V.zoom = 1; V.cx = V.cy = 0.5; sendView(); };
function togglePanels() { document.body.classList.toggle("full"); $("panels").textContent = document.body.classList.contains("full") ? "패널 보이기" : "패널 숨기기"; setTimeout(sendView, 380); }
$("panels").onclick = togglePanels;
document.addEventListener("keydown", e => { if (e.key === "Tab" && !/INPUT|TEXTAREA/.test(document.activeElement.tagName)) { e.preventDefault(); togglePanels(); } });
window.addEventListener("resize", () => sendView());
// 작업대를 열고 닫으면 렌더 뷰 크기가 바뀜 → 다 바뀐 뒤 다시 알림
new MutationObserver(() => setTimeout(sendView, 380)).observe(document.body, { attributes: true, attributeFilter: ["class"] });

// ---------------------------------------------------------------- 실사 라이브러리 (Poly Haven HDRI · ambientCG 재질)
// 목록·받기는 콘솔 서버가 함 (/api/lib/...). HDRI = 받으면 그 룩으로 바로 바뀜, 재질 = 고른 부위에 입힘
let libKind = "hdri", libItems = [], libTimer = 0, pendingMat = null, pendingPart = null, lastJob = "";
async function loadLib() {
  $("libHint").textContent = "목록 불러오는 중…"; $("libGrid").innerHTML = "";
  const q = $("libSearch").value.trim();
  try {
    const r = await (await fetch(libKind === "hdri" ? "/api/lib/hdri" : "/api/lib/mat?q=" + encodeURIComponent(q))).json();
    if (!r.ok) { $("libHint").textContent = r.msg; return; }
    libItems = libKind === "hdri" ? r.data : r.data.items;
    drawLib();
  } catch (e) { $("libHint").textContent = "콘솔 서버에 연결이 안 돼요"; }
}
function drawLib() {
  const q = $("libSearch").value.trim().toLowerCase();
  let list = libItems;
  if (libKind === "hdri" && q) list = list.filter(x => x.name.toLowerCase().includes(q) || x.id.includes(q) || x.cats.some(c => c.includes(q)));
  const have = new Set([...(S ? S.looks : []), ...(S ? S.materials : [])].map(x => x.id));
  const pre = libKind === "hdri" ? "lib_hdri_" : "lib_";
  $("libHint").textContent = libKind === "hdri" ? `HDRI ${list.length}개 (인기순) · 실내는 indoor, 스튜디오는 studio` : `재질 ${list.length}개 · 금속은 metal, 고무는 rubber`;
  $("libGrid").innerHTML = list.slice(0, 80).map(x => `<button class="look" data-id="${esc(x.id)}" title="${esc(x.cats.join(", "))}">
      <img class="ph" src="${esc(x.thumb)}" alt="" loading="lazy">${have.has(pre + x.id) ? `<span class="have">받음</span>` : ""}
      <b>${esc(x.name)}</b><small>${esc(x.cats.slice(0, 3).join(" · "))}</small></button>`).join("");
  $("libGrid").querySelectorAll(".look").forEach(b => b.onclick = () => libGet(b));
}
async function libGet(b) {
  if (libKind === "mat") {
    if (!sel) return toast("먼저 왼쪽 [부위 재질]에서 입힐 부위를 고르세요");
    pendingMat = "lib_" + b.dataset.id; pendingPart = sel;
  }
  const r = await (await fetch("/api/lib/get", { method: "POST", body: JSON.stringify({ kind: libKind, id: b.dataset.id }) })).json();
  if (!r.ok) { toast(r.msg); pendingMat = null; return; }
  b.classList.add("busy"); toast("받는 중… (처음 한 번만, 수 MB)");
}
$("openLib").onclick = () => { document.body.classList.remove("bench-open"); document.body.classList.add("lib-open"); if (!libItems.length) loadLib(); };
$("closeLib").onclick = () => document.body.classList.remove("lib-open");
$("libTabs").querySelectorAll("button").forEach(b => b.onclick = () => {
  libKind = b.dataset.k; libItems = []; $("libSearch").value = "";
  $("libTabs").querySelectorAll("button").forEach(x => x.classList.toggle("on", x === b));
  $("libStatus").textContent = libKind === "hdri" ? "HDRI는 받으면 그 조명 룩으로 바로 바뀌어요" : "재질은 왼쪽에서 고른 부위에 입혀져요";
  loadLib();
});
$("libSearch").oninput = () => { if (libKind === "hdri") return drawLib(); clearTimeout(libTimer); libTimer = setTimeout(loadLib, 450); };
function libTick(r) {   // poll마다: 진행 알림, 받은 재질을 부위에 입히기
  if (r.job && r.job !== lastJob) { lastJob = r.job; toast(r.job); $("libStatus").textContent = r.job; }
  if (!r.busy) document.querySelectorAll("#libGrid .busy").forEach(b => b.classList.remove("busy"));
  if (pendingMat && S && preset(pendingMat)) { sel = pendingPart; applyPreset(pendingMat); pendingMat = null; drawLib(); }
}

// ---------------------------------------------------------------- 레퍼런스 색 맞추기
// 사진은 이 PC의 콘솔 서버로만 감 → 사진·렌더 뷰의 밝기·대비·채도·색온도를 비교해 조명 세부 슬라이더를 맞춤
let refFile = null;
function setRef(f) {
  if (!f || !f.type.startsWith("image/")) return;
  refFile = f; $("refImg").src = URL.createObjectURL(f); $("refImg").classList.remove("hidden"); $("refText").textContent = f.name;
}
$("refFile").onchange = (e) => setRef(e.target.files[0]);
["dragover", "dragleave", "drop"].forEach(ev => $("refBox").addEventListener(ev, e => {
  if (!e.dataTransfer || !e.dataTransfer.types.includes("Files")) return;
  e.preventDefault(); $("refBox").classList.toggle("drop", ev === "dragover");
  if (ev === "drop") setRef(e.dataTransfer.files[0]);
}));
$("refGo").onclick = async () => {
  if (!refFile) return toast("먼저 레퍼런스 사진을 고르세요");
  if (!S || !S.look) return toast("먼저 룩을 하나 고르세요 (그 룩의 세부 슬라이더를 움직여요)");
  editAt = 0;
  const r = await (await fetch("/api/reference", { method: "POST", body: refFile })).json();
  toast(r.ok ? "색 맞추는 중…" : r.msg);
};
$("refUndo").onclick = () => {   // 룩의 원래 세부 값으로 (색 맞추기 전)
  if (!S || !S.look || !S.lookDefaults) return toast("되돌릴 룩이 없어요");
  editAt = 0; post({ cmd: "look_params", look: S.lookDefaults }); toast("색 맞추기 되돌림");
};

// ---------------------------------------------------------------- 받아서 그리기
async function poll() {
  try {
    const r = await (await fetch("/api/state")).json();
    if (webVersion && r.version && r.version !== webVersion) return location.reload();   // 화면 파일이 바뀜
    webVersion = r.version || webVersion;
    $("live").classList.toggle("on", r.connected);
    $("conn").textContent = r.connected ? "유니티 연결됨" : r.state ? "연결 끊김" : "유니티 기다리는 중";
    if (r.connected) startView();
    libTick(r);
    $("refHint").textContent = S && S.look ? "지금 룩 위에 맞춰요" : "룩을 먼저 고르기";
    const img = $("view");
    $("liveChip").classList.toggle("on", r.connected && img.naturalWidth > 0);
    $("liveText").textContent = r.connected && img.naturalWidth ? `LIVE · ${img.naturalWidth}×${img.naturalHeight}${V.zoom > 1.01 ? " · " + Math.round(V.zoom * 100) + "%" : ""}` : "RENDER VIEW";
    if (r.connected && !wasConn && viewOn) sendView();   // 유니티를 다시 켜면 화면 크기·확대를 다시 알려 줌
    wasConn = r.connected;
    if (r.state && r.state.seq !== lastSeq) {
      S = r.state; lastSeq = S.seq;
      $("sceneInfo").textContent = `${S.scene} · 부위 ${S.parts.length}`;
      if (sel && !S.parts.find(p => p.id === sel)) sel = null;
      drawLooks(); drawParts();
      if (document.body.classList.contains("bench-open")) drawBench();
      if (S.msg && S.msg !== lastMsg) { lastMsg = S.msg; toast(S.msg); }
    }
  } catch (e) {
    $("live").classList.remove("on"); $("conn").textContent = "콘솔 서버 꺼짐";
  }
  setTimeout(poll, 250);
}
poll();

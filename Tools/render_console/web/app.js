// 렌더 룩 콘솔 화면
// 흐름: 0.25초마다 /api/state로 유니티 상태를 받아 목록을 그림 → 고르거나 슬라이더를 움직이면 /api/cmd로 보냄
// 슬라이더를 움직이는 동안은 유니티 값으로 덮지 않음 (손에서 놓고 0.8초 뒤부터 다시 맞춤)

const $ = (id) => document.getElementById(id);
const LOOK_SLIDERS = [
  ["ambient", "환경광", 0, 2, 0.05],
  ["reflection", "반사", 0, 2, 0.05],
  ["sceneLight", "원래 조명", 0, 2, 0.05],
  ["exposure", "노출", -2, 2, 0.05],
  ["contrast", "대비", -50, 50, 1],
  ["saturation", "채도", -60, 60, 1],
  ["bloom", "빛 번짐", 0, 1, 0.01],
];
const MAT_SLIDERS = [
  ["metallic", "금속", 0, 1, 0.01],
  ["roughness", "거칠기", 0, 1, 0.01],
  ["normalStrength", "결 세기", 0, 1, 0.01],
  ["tiling", "결 크기", 0.25, 4, 0.05],
];
const MODE_NOTE = {
  original: "이 부위는 원래 재질 그대로예요.",
  replace: "프리셋 색·질감으로 바꿔요. 원래 사진 텍스처는 빠져요.",
  keep: "원래 색·텍스처는 그대로 두고 금속·거칠기·결만 바꿔요.",
};

let S = null;            // 유니티가 보낸 마지막 상태
let sel = null;          // 고른 부위 id
let editAt = 0;          // 마지막으로 손댄 시각 (이 동안은 유니티 값으로 안 덮음)
let lastSeq = -1;

function post(cmd) {
  return fetch("/api/cmd", { method: "POST", body: JSON.stringify(cmd) }).catch(() => {});
}

// 같은 종류 명령은 0.05초에 한 번만 (마지막 값은 꼭 보냄)
const pend = {};
function throttled(key, make) {
  editAt = performance.now();
  if (pend[key]) { pend[key].make = make; return; }
  post(make());
  pend[key] = { make: null };
  setTimeout(function flush() {
    const p = pend[key]; delete pend[key];
    if (p && p.make) throttled(key, p.make);
  }, 50);
}

const fmt = (v, step) => step >= 1 ? Math.round(v) : step >= 0.05 ? (+v).toFixed(2).replace(/0$/, "") : (+v).toFixed(2);
const toHex = (c) => "#" + (c || [0.8, 0.8, 0.8]).slice(0, 3).map(v => Math.round(Math.min(1, Math.max(0, v)) * 255).toString(16).padStart(2, "0")).join("");
const fromHex = (h) => [1, 3, 5].map(i => +(parseInt(h.substr(i, 2), 16) / 255).toFixed(3));

function sliderRow(parent, [key, label, min, max, step], onInput) {
  const row = document.createElement("div");
  row.className = "row";
  row.innerHTML = `<label>${label}</label><input type="range" min="${min}" max="${max}" step="${step}"><b></b>`;
  const input = row.querySelector("input"), out = row.querySelector("b");
  input.dataset.key = key;
  input.oninput = () => { out.textContent = fmt(input.value, step); onInput(key, +input.value); };
  input.set = (v) => { input.value = v; out.textContent = fmt(v, step); };
  parent.appendChild(row);
  return input;
}

// ---------------------------------------------------------------- 조명 분위기
const lookInputs = {};
LOOK_SLIDERS.forEach(d => {
  lookInputs[d[0]] = sliderRow($("lookSliders"), d, () => {
    const look = {};
    for (const k in lookInputs) look[k] = +lookInputs[k].value;
    throttled("look", () => ({ cmd: "look_params", look }));
  });
});

function renderLooks() {
  const box = $("looks");
  const items = [{ id: "", name: "원래대로", note: "장면에 원래 있던 조명 그대로" }, ...S.looks];
  box.innerHTML = "";
  items.forEach(l => {
    const b = document.createElement("button");
    b.className = "item" + ((S.look || "") === l.id ? " on" : "");
    b.innerHTML = `${l.name}<span class="sub">${l.id ? l.id : "기본"}</span>`;
    b.onclick = () => { editAt = 0; post({ cmd: "look", id: l.id }); };
    box.appendChild(b);
  });
  const cur = S.looks.find(l => l.id === S.look);
  $("lookNote").textContent = cur ? cur.note || "" : "분위기를 고르면 아래 수치로 더 다듬을 수 있어요.";
  const on = !!cur;
  for (const k in lookInputs) {
    lookInputs[k].disabled = !on;
    if (fresh()) lookInputs[k].set(on && S.lookParams ? S.lookParams[k] : 0);
  }
}

// ---------------------------------------------------------------- 부위
function partState(id) {
  return (S.states || []).find(s => s.id === id) || { id, mode: "original", preset: "", metallic: 0, roughness: 0.5, normalStrength: 0, tiling: 1, color: [0.8, 0.8, 0.8] };
}

function partButton(p) {
  const b = document.createElement("button");
  const st = partState(p.id);
  b.className = "item" + (sel === p.id ? " on" : "") + (st.mode !== "original" ? " changed" : "") + (p.found ? "" : " gone");
  b.innerHTML = `${p.label}<span class="sub">${p.found ? `${p.count}곳` : "지금 장면에 안 보임"}</span>`;
  b.onclick = () => { sel = p.id; editAt = 0; render(true); };
  return b;
}

function renderParts() {
  const lab = S.parts.filter(p => !p.auto), auto = S.parts.filter(p => p.auto);
  const q = $("search").value.trim().toLowerCase();
  $("labeled").innerHTML = "";
  if (!lab.length) $("labeled").innerHTML = `<p class="note">이름표가 없어요. 아래 "장면에서 찾은 재질"에서 고르거나, StreamingAssets/RenderTool/parts/${S.scene}.json으로 이름을 붙일 수 있어요.</p>`;
  lab.forEach(p => $("labeled").appendChild(partButton(p)));
  $("auto").innerHTML = "";
  auto.filter(p => !q || p.label.toLowerCase().includes(q)).forEach(p => $("auto").appendChild(partButton(p)));
  $("autoCount").textContent = `${auto.length}개`;
  if (!lab.length) $("autoBox").open = true;
}
$("search").oninput = () => S && renderParts();

// ---------------------------------------------------------------- 재질
const matInputs = {};
MAT_SLIDERS.forEach(d => { matInputs[d[0]] = sliderRow($("matSliders"), d, () => sendPart(false)); });
$("color").oninput = () => sendPart(false);

function current() {
  const st = partState(sel);
  const out = { id: sel, mode: st.mode, preset: st.preset || "", color: fromHex($("color").value) };
  for (const k in matInputs) out[k] = +matInputs[k].value;
  return out;
}

function sendPart(pick, patch = {}) {
  if (!sel) return;
  const part = Object.assign(current(), patch);
  if (pick || patch.mode) {   // 프리셋·방식을 바꾸면 바로 보냄 (슬라이더는 묶어서)
    editAt = 0;
    // 화면도 바로 바꿔 둠 (유니티 응답 오기 전 깜빡임 방지)
    S.states = (S.states || []).filter(s => s.id !== sel).concat([part]);
    post({ cmd: "part", part, pick });
    renderMat();
  } else throttled("part:" + sel, () => ({ cmd: "part", part, pick: false }));
}

document.querySelectorAll("#modes button").forEach(b => {
  b.onclick = () => sendPart(partState(sel).mode === "original" && b.dataset.mode !== "original" && !partState(sel).preset, { mode: b.dataset.mode });
});

function renderMat() {
  const p = sel && S.parts.find(x => x.id === sel);
  $("matPanel").classList.toggle("hidden", !p);
  $("partHint").classList.toggle("hidden", !!p);
  if (!p) { $("partTitle").textContent = "부위를 고르세요"; return; }
  const st = partState(sel);
  $("partTitle").textContent = p.label + (p.auto ? "" : "");
  document.querySelectorAll("#modes button").forEach(b => b.classList.toggle("on", b.dataset.mode === st.mode));
  $("modeNote").textContent = MODE_NOTE[st.mode] || "";

  const chips = $("mats");
  chips.innerHTML = "";
  S.materials.forEach(m => {
    const c = document.createElement("button");
    c.className = "chip" + (st.mode !== "original" && st.preset === m.id ? " on" : "");
    c.title = m.note || "";
    c.innerHTML = `<i style="background:${toHex(m.color)}"></i>${m.name}`;
    c.onclick = () => sendPart(true, { preset: m.id, mode: st.mode === "original" ? "replace" : st.mode });
    chips.appendChild(c);
  });

  const off = st.mode === "original";
  for (const k in matInputs) {
    matInputs[k].disabled = off;
    if (fresh()) matInputs[k].set(st[k] ?? 0);
  }
  $("color").disabled = off || st.mode === "keep";
  if (fresh()) $("color").value = toHex(st.color);
  $("colorNote").textContent = st.mode === "keep" ? "텍스처 유지 중엔 원래 색을 써요" : off ? "" : "전부 바꾸기일 때만";
}

// ---------------------------------------------------------------- 위 버튼
$("btnReset").onclick = () => { editAt = 0; post({ cmd: "reset" }); };
$("btnRescan").onclick = () => post({ cmd: "rescan" });
$("btnSaveLook").onclick = () => {
  if (!S || !S.look) return toast("먼저 조명 분위기를 고르세요");
  const name = prompt("새 룩 이름", (S.looks.find(l => l.id === S.look) || {}).name + " (조절)");
  if (name) post({ cmd: "save_look", name });
};
$("btnSaveMat").onclick = () => {
  const st = sel && partState(sel);
  if (!st || st.mode === "original") return toast("부위를 고르고 재질을 먼저 입히세요");
  const m = S.materials.find(x => x.id === st.preset);
  const name = prompt("새 재질 이름", (m ? m.name : "새 재질") + " (조절)");
  if (name) post({ cmd: "save_material", id: sel, name });
};
$("btnQuit").onclick = () => { if (confirm("렌더 콘솔을 끌까요? (유니티 장면은 그대로)")) fetch("/api/shutdown", { method: "POST" }).then(() => window.close()); };

function toast(t) { $("msg").textContent = t; }
const fresh = () => performance.now() - editAt > 800;

// ---------------------------------------------------------------- 받아서 그리기
function render(force) {
  if (!S) return;
  $("scene").textContent = `장면 ${S.scene} · 룩 ${S.looks.length} · 재질 ${S.materials.length} · 부위 ${S.parts.length}`;
  renderLooks(); renderParts(); renderMat();
  if (S.msg) $("msg").textContent = S.msg;
}

async function poll() {
  try {
    const r = await (await fetch("/api/state")).json();
    const c = $("conn");
    c.textContent = r.connected ? "유니티 연결됨" : (r.state ? "연결 끊김 — 유니티 플레이 중인지 확인" : "유니티 기다리는 중 — 플레이를 눌러 주세요");
    c.className = "pill " + (r.connected ? "on" : "off");
    if (r.state && (r.state.seq !== lastSeq)) {
      // 유니티가 다시 켜지면 seq가 처음부터 → 그대로 받음
      S = r.state; lastSeq = S.seq;
      if (sel && !S.parts.find(p => p.id === sel)) sel = null;
      render();
    }
  } catch (e) {
    $("conn").textContent = "콘솔 서버 꺼짐"; $("conn").className = "pill off";
  }
  setTimeout(poll, 250);
}
poll();

// SO-ARM101 패널 화면 — 0.1초마다 /api/status를 받아 다시 그리고, 버튼은 /api/cmd로 명령을 보냄
// 명령 이름은 UDP 명령(5061)과 같음 (docs/so101_messages.md)

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
import { createViewer } from "./robot3d.js";
let info = null, last = null, logCount = 0, viewer = null, stopped = false;

// ---------------------------------------------------------------- 공통
async function cmd(body) {
  const r = await fetch("/api/cmd", { method: "POST", body: JSON.stringify(body) }).then(r => r.json())
    .catch(() => ({ ok: false, msg: "패널 서버에 연결할 수 없어요 (터미널이 꺼졌나요?)" }));
  if (!r.ok) toast(r.msg);
  return r;
}
function toast(text) {
  const t = $("#toast"); t.textContent = text; t.classList.remove("hidden");
  clearTimeout(toast.h); toast.h = setTimeout(() => t.classList.add("hidden"), 4000);
}
function confirmBox(title, text) {
  return new Promise(res => {
    $("#modalTitle").textContent = title; $("#modalText").textContent = text;
    $("#modal").classList.remove("hidden");
    const done = v => { $("#modal").classList.add("hidden"); res(v); };
    $("#modalYes").onclick = () => done(true); $("#modalNo").onclick = () => done(false);
  });
}
const f1 = v => (v === null || v === undefined) ? "—" : v.toFixed(1);

// ---------------------------------------------------------------- 처음 한 번
async function init() {
  info = await fetch("/api/info").then(r => r.json());
  $("#mirrorDesc").textContent = "리더 각도 → " + info.angles_out.join(", ");
  fillPorts(info.ports);
  buildGauges();
  createViewer($("#view3d")).then(v => { viewer = v; $("#loading3d").remove(); })
    .catch(e => { $("#loading3d").textContent = "3D 모델을 못 불러왔어요: " + e.message; });
  $("#resetView").onclick = () => viewer && viewer.resetView();

  $$(".conn").forEach(c => {
    $(".connect", c).onclick = async () => {
      const role = c.dataset.role;
      if (last && last[role].connected) await cmd({ cmd: "disconnect", role });
      else await cmd({ cmd: "connect", role, port: $(".port", c).value });
    };
  });
  $("#scan").onclick = async () => {
    $("#scan").textContent = "찾는 중…";
    const r = await cmd({ cmd: "scan_ports" });
    if (r.ports) fillPorts(r.ports);
    $("#scan").textContent = "포트 찾기";
  };
  $("#mirror").onchange = e => cmd({ cmd: "mirror", on: e.target.checked });
  $$("#source button").forEach(b => b.onclick = () => cmd({ cmd: "set_source", source: b.dataset.v }));
  $("#follow").onclick = async () => {
    const dry = $("#dry").checked;
    if (!dry) {
      const ok = await confirmBox("흰 팔 연동 — 실제로 움직여요",
        `흰 팔이 '${last.source}' 자세를 따라 움직입니다.\n최대 ${last.max_speed_dps}°/초 · 지금 자세에서 출발 · 그리퍼 ${last.follow_ids.length === 6 ? "포함" : "제외"}\n\n흰 팔 옆에 있나요? 멈출 땐 Space.`);
      if (!ok) return;
    }
    cmd({ cmd: "follow", on: true, dry_run: dry });
  };
  $("#stop").onclick = () => cmd({ cmd: "stop" });
  const gotoPose = async (pose, name) => {
    const dry = $("#dry").checked;
    if (!dry) {
      const ok = await confirmBox(`흰 팔을 ${name}로 — 실제로 움직여요`,
        `벨트에 안 닿게 3단계로 움직여요.\n1) 어깨를 로봇 오른쪽으로 45° 돌리기\n2) 팔을 ${name}로 바꾸기\n3) 어깨를 되돌리기\n\n20°/초 · 지금 자세에서 출발 · 그리퍼 제외\n흰 팔 옆에 있나요? 멈출 땐 Space.`);
      if (!ok) return;
    }
    cmd({ cmd: "goto_pose", pose, dry_run: dry });
  };
  $("#toL").onclick = () => gotoPose("l_pose", "ㄱ자");
  $("#toRest").onclick = () => gotoPose("rest", "쉬는 자세");
  $("#release").onclick = async () => {
    if (await confirmBox("힘 빼기", "힘을 빼면 흰 팔이 떨어집니다.\n팔을 손으로 받쳤나요?")) cmd({ cmd: "release", confirm: true });
  };
  $$("[data-zero]").forEach(b => b.onclick = async () => {
    const [role, method] = b.dataset.zero.split(",");
    if (await confirmBox("영점 저장", b.dataset.q + "\n\n읽기만 하고 로봇은 움직이지 않아요.")) cmd({ cmd: "capture_zero", role, method });
  });
  $("#zeroReset").onclick = async () => {
    if (await confirmBox("흰 팔 영점 기본값으로", "흰 팔 영점을 기본값(10/23 쉬는 자세 기준)으로 되돌려요.\n로봇은 움직이지 않아요.")) cmd({ cmd: "reset_zero" });
  };
  $("#estop").onclick = () => cmd({ cmd: "estop" });
  $("#estopReset").onclick = () => cmd({ cmd: "estop_reset" });
  $("#quit").onclick = async () => {
    if (await confirmBox("패널 종료", "연결을 모두 해제하고 패널을 끕니다.\n(연동 중이면 흰 팔은 그 자리에서 정지)")) {
      stopped = true;
      await fetch("/api/shutdown", { method: "POST", body: "{}" });
      document.body.innerHTML = "<p style='padding:40px;color:#9fb0cc'>패널이 종료됐어요. 이 창을 닫아도 돼요.</p>";
    }
  };
  document.addEventListener("keydown", e => {               // Space = 비상 정지
    if (e.code === "Space") { e.preventDefault(); cmd({ cmd: "estop" }); }
  });
  loop();
}

function fillPorts(ports) {
  $$(".conn").forEach(c => {
    const sel = $(".port", c), cur = sel.value || (last && last[c.dataset.role].port) || "";
    const list = ports.map(p => p.port);
    if (cur && !list.includes(cur)) list.unshift(cur);
    sel.innerHTML = list.map(p => `<option ${p === cur ? "selected" : ""}>${p}</option>`).join("");
  });
}

// ---------------------------------------------------------------- 0.1초마다
async function loop() {
  if (stopped) return;
  try {
    const d = await fetch("/api/status?since=" + logCount).then(r => r.json());
    render(d.status);
    appendLogs(d.logs, d.log_count);
  } catch (e) {
    $("#stateText").textContent = "패널 서버 연결 끊김"; $("#state").className = "state stop";
  }
  setTimeout(loop, 100);
}

function render(st) {
  const first = !last;
  last = st;
  const L = st.leader, F = st.follower;

  // 상태
  const s = $("#state");
  if (st.estop) { s.className = "state stop"; $("#stateText").textContent = "비상 정지"; }
  else if (st.follow && st.motion) { s.className = "state " + (st.dry_run ? "dry" : "run"); $("#stateText").textContent = `${st.motion}로 이동 중 · ${st.motion_step}/${st.motion_total}단계 · ${st.dry_run ? "dry-run" : "실제"}`; }
  else if (st.follow) { s.className = "state " + (st.dry_run ? "dry" : "run"); $("#stateText").textContent = `연동 중 · ${st.source} · ${st.dry_run ? "dry-run" : "실제"}`; }
  else { s.className = "state"; $("#stateText").textContent = `대기 · 흰 팔 힘 ${st.torque ? "켜짐" : "꺼짐"}`; }
  $("#estopBanner").classList.toggle("hidden", !st.estop);
  $("#dryBadge").classList.toggle("hidden", !(st.follow && st.dry_run));
  // 영점 상태
  const zl = $("#zeroLeader"), zf = $("#zeroFollower");
  zl.textContent = st.leader.zero_ready ? "ㄱ자 기준 (10/22)" : "없음"; zl.className = st.leader.zero_ready ? "ok" : "warn";
  zf.textContent = !st.follower.zero_ready ? "없음" : (st.follower.zero_default ? "기본값 (쉬는 자세 기준)" : "기본값과 다름");
  zf.className = st.follower.zero_default ? "ok" : "warn";
  $("#zeroReset").classList.toggle("hidden", !!st.follower.zero_default);

  // 칩
  const chip = (id, a) => {
    const el = $(id);
    el.className = "chip" + (a.connected ? (a.zero_ready ? " on" : " warn") : "");
    $("b", el).textContent = a.connected ? `${a.port} · ${a.hz.toFixed(0)}Hz${a.zero_ready ? "" : " · 영점 없음"}` : "끊김";
  };
  chip("#chipLeader", L); chip("#chipFollower", F);
  $("#chipUnity").className = "chip" + (st.mirror ? " on" : "");
  $("#chipUnity b").textContent = st.mirror ? `보내는 중 · ${st.sent}` : "꺼짐";

  // 연결 카드
  $$(".conn").forEach(c => {
    const a = st[c.dataset.role], btn = $(".connect", c), sel = $(".port", c);
    if (first && a.port) { if (![...sel.options].some(o => o.value === a.port)) sel.insertAdjacentHTML("afterbegin", `<option>${a.port}</option>`); sel.value = a.port; }
    btn.textContent = a.connected ? "해제" : "연결";
    btn.classList.toggle("on", a.connected);
    sel.disabled = a.connected;
  });

  // 동작
  $("#mirror").checked = st.mirror;
  $$("#source button").forEach(b => b.classList.toggle("on", b.dataset.v === st.source));
  $("#follow").disabled = st.follow || st.estop;
  $("#toL").disabled = $("#toRest").disabled = st.follow || st.estop || !F.connected;
  $("#dry").disabled = st.follow;
  $("#followMeta").innerHTML = `속도 제한 <b>${st.max_speed_dps}°/초</b> · 신호가 <b>${info.watchdog_s}초</b> 끊기면 자동 정지 · 그리퍼 <b>${st.follow_ids.length === 6 ? "포함" : "제외"}</b>`;

  renderGauges(st);
  $("#rates").textContent = `리더 ${L.hz.toFixed(0)}Hz · 흰 팔 ${F.hz.toFixed(0)}Hz`;
  if (viewer) viewer.update(F.connected ? F.deg : null, L.connected ? L.deg : null);
  renderAngles(st);
}

// ---------------------------------------------------------------- 관절 게이지
const G = { cx: 70, cy: 62, r: 50, a0: 210, a1: -30 };   // 240° 호
const ang = (v, lo, hi) => G.a0 + (G.a1 - G.a0) * Math.max(0, Math.min(1, (v - lo) / (hi - lo)));
const pt = (a, r = G.r) => [G.cx + r * Math.cos(a * Math.PI / 180), G.cy - r * Math.sin(a * Math.PI / 180)];
function arc(a, b, r = G.r) {
  const [x0, y0] = pt(a, r), [x1, y1] = pt(b, r);
  return `M${x0},${y0} A${r},${r} 0 ${Math.abs(b - a) > 180 ? 1 : 0} ${a > b ? 1 : 0} ${x1},${y1}`;
}
function buildGauges() {
  $("#gaugeRow").innerHTML = info.joints.map((j, i) => `
    <div class="g" id="g${i}">
      <span class="flag hidden">범위 밖</span>
      <svg viewBox="0 0 140 92">
        <defs><linearGradient id="gg${i}" x1="0" x2="1"><stop offset="0" stop-color="var(--accent)"/><stop offset="1" stop-color="var(--accent-2)"/></linearGradient></defs>
        <path d="${arc(G.a0, G.a1)}" stroke="#1b2740" stroke-width="9" fill="none" stroke-linecap="round"/>
        <path class="safe" stroke="rgba(47,140,255,.28)" stroke-width="9" fill="none"/>
        <path class="val" stroke="url(#gg${i})" stroke-width="5" fill="none" stroke-linecap="round" style="filter:drop-shadow(0 0 4px rgba(73,211,255,.6))"/>
        <path class="lead" fill="#dfe6f5"/>
        <text class="num" x="70" y="64" text-anchor="middle" fill="#e9f0ff" font-size="20" font-weight="700">—</text>
        <text x="70" y="80" text-anchor="middle" fill="#7f8ca8" font-size="9">리더 °</text>
      </svg>
      <div class="name">${j.ko}</div>
      <div class="sub">흰 팔 <b class="fv">—</b>° · <span class="tk">—</span></div>
    </div>`).join("");
}
function renderGauges(st) {
  const L = st.leader, F = st.follower;
  info.joints.forEach((j, i) => {
    const g = $("#g" + i), [lo, hi] = j.limit, [slo, shi] = st.safe_range[i];
    const ld = L.deg[i], fd = F.deg[i];
    $(".safe", g).setAttribute("d", arc(ang(slo, lo, hi), ang(shi, lo, hi)));
    $(".val", g).setAttribute("d", fd === null ? "" : arc(G.a0, Math.min(G.a0 - 0.5, ang(fd, lo, hi))));
    if (ld === null) $(".lead", g).setAttribute("d", "");
    else {
      const a = ang(ld, lo, hi), [x, y] = pt(a, G.r + 9), [x1, y1] = pt(a + 6, G.r + 16), [x2, y2] = pt(a - 6, G.r + 16);
      $(".lead", g).setAttribute("d", `M${x},${y} L${x1},${y1} L${x2},${y2}Z`);
    }
    $(".num", g).textContent = f1(ld);
    $(".fv", g).textContent = f1(fd);
    $(".tk", g).textContent = F.ticks[i] === null ? "틱 —" : `틱 ${F.ticks[i]}`;
    const out = st.follow_ids.includes(i + 1) && ((ld !== null && (ld < slo || ld > shi)) || st.clipped[i]);
    g.classList.toggle("out", out); $(".flag", g).classList.toggle("hidden", !out);
  });
}

// ---------------------------------------------------------------- 로그
function appendLogs(lines, count) {
  if (lines.length) {
    const box = $("#log"), atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 30;
    for (const l of lines) {
      const div = document.createElement("div"); div.textContent = l; div.title = l;
      if (l.includes("[오류]")) div.className = "err"; else if (l.includes("정지")) div.className = "stop";
      box.appendChild(div);
    }
    while (box.children.length > 300) box.firstChild.remove();
    if (atBottom) box.scrollTop = box.scrollHeight;
  }
  logCount = count;
}

// ---------------------------------------------------------------- 3D 위 각도 카드
function renderAngles(st) {
  const L = st.leader, F = st.follower, rows = [0, 1, 2, 3];
  $("#angles").innerHTML = rows.map(i => {
    const cmd = st.follow && st.cmd_deg && st.follow_ids.includes(i + 1) ? ` · 명령 ${f1(st.cmd_deg[i])}°` : "";
    return `<div class="ang"><i></i><div><span>${info.joints[i].ko}</span><b>${f1(F.deg[i])}°</b></div><em>리더 ${f1(L.deg[i])}°${cmd}</em></div>`;
  }).join("");
  $("#sideMeta").textContent = `손목 회전 ${f1(L.deg[4])}° / ${f1(F.deg[4])}° · 그리퍼 ${f1(L.deg[5])}° / ${f1(F.deg[5])}°  (리더 / 흰 팔)`;
}

init();

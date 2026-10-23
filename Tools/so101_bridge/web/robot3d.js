// 3D 화면 — 유니티와 같은 URDF·STL을 three.js로 그림
//   흰 팔(실제 각도) = 흰 플라스틱 로봇, 리더 = 반투명 파란 유령 로봇 (컨베이어 등 전체 장면은 유니티 화면 몫)
//   관절 각도(°)를 setJointValue(라디안)로 넣으면 바로 그 자세가 됨 (URDF 각도 = 웹 시뮬·유니티 각도)
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import URDFLoader from './vendor/urdf-loader/URDFLoader.js';

const URDF_URL = '/urdf/so101_new_calib_camera.urdf';
const JOINTS = ['shoulder_pan', 'shoulder_lift', 'elbow_flex', 'wrist_flex', 'wrist_roll', 'gripper'];

const MAT = {
  body: new THREE.MeshPhysicalMaterial({ color: 0xeef3fb, roughness: 0.42, metalness: 0.05, clearcoat: 0.35, clearcoatRoughness: 0.4 }),
  motor: new THREE.MeshStandardMaterial({ color: 0x1b2537, roughness: 0.5, metalness: 0.3 }),
  ghost: new THREE.MeshBasicMaterial({ color: 0x49d3ff, transparent: true, opacity: 0.16, depthWrite: false }),
};

// STL은 한 번만 받아서 두 로봇(흰 팔·리더)이 같이 씀
const geoCache = new Map();
function loadMeshCb(path, manager, done) {
  if (!geoCache.has(path)) {
    geoCache.set(path, fetch(path).then(r => r.arrayBuffer()).then(buf => {
      const g = parseSTL(buf); g.computeVertexNormals(); return g;
    }));
  }
  geoCache.get(path).then(g => {
    const m = new THREE.Mesh(g);
    m.userData.motor = /sts3215/.test(path);
    done(m);
  }).catch(err => done(null, err));
}
// STLLoader 없이 바이너리/아스키 STL 둘 다 읽기
import { STLLoader } from 'three/addons/loaders/STLLoader.js';
const stl = new STLLoader();
const parseSTL = buf => stl.parse(buf);

function loadRobot() {
  return new Promise((resolve, reject) => {
    const manager = new THREE.LoadingManager();
    const loader = new URDFLoader(manager);
    loader.loadMeshCb = loadMeshCb;
    let robot = null;
    manager.onLoad = () => robot && resolve(robot);
    manager.onError = u => reject(new Error('못 읽음: ' + u));
    loader.load(URDF_URL, r => { robot = r; }, undefined, reject);
  });
}

function paint(robot, kind) {
  robot.traverse(o => {
    if (!o.isMesh) return;
    if (o.parent && o.parent.isURDFCollider) { o.visible = false; return; }
    o.material = kind === 'ghost' ? MAT.ghost : (o.userData.motor ? MAT.motor : MAT.body);
    o.castShadow = kind !== 'ghost';
    o.receiveShadow = kind !== 'ghost';
    if (kind === 'ghost') o.renderOrder = 2;
  });
}

export async function createViewer(container) {
  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  container.appendChild(renderer.domElement);

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 20);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.minDistance = 0.3; controls.maxDistance = 3;
  controls.maxPolarAngle = Math.PI * 0.49;

  // 조명: 하늘·바닥 + 주광(그림자) + 파란 테두리광
  scene.add(new THREE.HemisphereLight(0xcfe3ff, 0x0b1220, 1.1));
  const key = new THREE.DirectionalLight(0xffffff, 2.2);
  key.position.set(0.6, 1.2, 0.8); key.castShadow = true;
  key.shadow.mapSize.set(2048, 2048);
  Object.assign(key.shadow.camera, { left: -0.6, right: 0.6, top: 0.6, bottom: -0.6, near: 0.1, far: 3 });
  scene.add(key);
  const rim = new THREE.DirectionalLight(0x49d3ff, 1.6);
  rim.position.set(-0.8, 0.5, -0.9); scene.add(rim);

  // 바닥: 그림자 받는 판 + 파란 격자 + 빛 원
  const floor = new THREE.Mesh(new THREE.CircleGeometry(1.4, 64), new THREE.ShadowMaterial({ opacity: 0.35 }));
  floor.rotation.x = -Math.PI / 2; floor.receiveShadow = true; scene.add(floor);
  const grid = new THREE.GridHelper(2.4, 48, 0x2f8cff, 0x2f8cff);
  grid.material.transparent = true; grid.material.opacity = 0.09; scene.add(grid);
  const glow = new THREE.Mesh(new THREE.CircleGeometry(0.42, 64), new THREE.MeshBasicMaterial({ color: 0x2f8cff, transparent: true, opacity: 0.08, depthWrite: false }));
  glow.rotation.x = -Math.PI / 2; glow.position.y = 0.001; scene.add(glow);

  // 로봇 두 대 (URDF는 Z가 위 → Y가 위로 돌림)
  const [follower, leader] = await Promise.all([loadRobot(), loadRobot()]);
  paint(follower, 'body'); paint(leader, 'ghost');
  for (const r of [follower, leader]) { r.rotation.x = -Math.PI / 2; scene.add(r); }

  // 로봇 정면 방향 = 0자세에서 받침 → 그리퍼 방향 (바닥 평면)
  for (const j of JOINTS) follower.setJointValue(j, 0);
  follower.updateMatrixWorld(true);
  const g = new THREE.Vector3(), b = new THREE.Vector3();
  follower.links.gripper_link ? follower.links.gripper_link.getWorldPosition(g) : follower.getWorldPosition(g);
  follower.getWorldPosition(b);
  const fwd = g.sub(b).setY(0).normalize();
  const up = new THREE.Vector3(0, 1, 0), right = new THREE.Vector3().crossVectors(fwd, up).normalize();

  // 카메라: 로봇 앞·오른쪽 위에서 로봇을 봄
  const target = new THREE.Vector3().addScaledVector(fwd, 0.12).setY(0.13);
  const place = () => {
    controls.target.copy(target);
    camera.position.copy(target).addScaledVector(fwd, 0.5).addScaledVector(right, 0.55).add(new THREE.Vector3(0, 0.3, 0));
    controls.update();
  };
  place();

  function resize() {
    const w = container.clientWidth, h = container.clientHeight;
    renderer.setSize(w, h, false); camera.aspect = w / h; camera.updateProjectionMatrix();
  }
  new ResizeObserver(resize).observe(container); resize();
  renderer.setAnimationLoop(() => { controls.update(); renderer.render(scene, camera); });

  // URDF 로더가 메시를 늦게 붙이면서 URDF 색으로 덮어쓰므로, 매번 우리 재질로 다시 입힘 (메시 40개라 가벼움)
  const apply = (robot, deg, kind) => {
    paint(robot, kind);
    if (!deg) { robot.visible = false; return; }
    robot.visible = true;
    JOINTS.forEach((j, i) => { if (deg[i] !== null && deg[i] !== undefined) robot.setJointValue(j, deg[i] * Math.PI / 180); });
  };
  return {
    // 흰 팔 실제 각도, 리더 각도 (없으면 null)
    // 흰 팔이 연결 전이면 ㄱ자(0°)로 보여줌 (화면이 비어 보이지 않게)
    update(followerDeg, leaderDeg) { apply(follower, followerDeg || [0, 0, 0, 0, 0, 0], 'body'); apply(leader, leaderDeg, 'ghost'); },
    resetView() { place(); },
  };
}

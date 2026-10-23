# SO-ARM101 로봇팔 모듈 — 메시지 규격 (v1, 10/23)

브리지(`Tools/so101_bridge`, 패널 또는 headless)가 시리얼(로봇)을 맡고, 다른 프로그램(유니티 등)과는 **UDP + JSON**으로만 주고받는다.
모든 메시지는 JSON 한 덩어리, UTF-8, `type` 칸으로 종류를 구분. 각도는 도(°), 관절 순서는 아래 표 순서.

| 순서 | 관절 | 한국어 | URDF 한계(°) | 관절 한계(실측 반영) |
|---|---|---|---|---|
| 0 | shoulder_pan | 어깨 회전 | -110 ~ 110 | 같음 |
| 1 | shoulder_lift | 어깨 들기 | -100 ~ 100 | **-110** ~ 100 (실물 쉬는 자세 -107.7°) |
| 2 | elbow_flex | 팔꿈치 | -96.8 ~ 96.8 | 같음 |
| 3 | wrist_flex | 손목 굽힘 | -95 ~ 95 | 같음 |
| 4 | wrist_roll | 손목 회전 | -157.2 ~ 162.8 | 같음 |
| 5 | gripper | 그리퍼 | -10 ~ 100 | 유니티 하한 -12 (리더 꽉 쥠) |

## 포트

| 포트 | 방향 | type | 주기 |
|---|---|---|---|
| 5060 | 브리지 → 유니티 | `so101_leader` | 30Hz (미러링 켰을 때) |
| 5061 | 누구든 → 브리지 | `so101_cmd` | 필요할 때 |
| 5062 | 브리지 → 구독자 | `so101_status`, `so101_ack` | 상태 5Hz, 응답은 명령마다 |
| 5063 | 유니티 → 브리지 | `so101_target` | 30Hz (입력 소스 = unity일 때, v2) |

포트는 `Tools/so101_bridge/config/so101_config.json`의 `udp`에서 바꿈. 5053(카메라 프로젝트)·5050(다른 프로그램)은 피함.
브리지는 명령·목표를 127.0.0.1에서만 받음 (같은 PC).

## 메시지

### `so101_leader` (5060) — 리더 각도 (기존 그대로)
```json
{"type": "so101_leader", "seq": 12, "t": 1792634500.12, "deg": [0.0, -107.6, 87.2, 71.1, -4.7, -11.7]}
```
`deg`는 관절 한계(위 표)로 자른 값.

### `so101_target` (5063) — 흰 팔 목표 자세 (v2)
```json
{"type": "so101_target", "seq": 3, "t": 1792634500.12, "deg": [0, -30, 20, 60, 0, 10]}
```
입력 소스가 `unity`이고 연동 중일 때만 흰 팔이 따라감. 0.3초 넘게 안 오면 자동 정지.

### `so101_cmd` (5061) — 명령
```json
{"type": "so101_cmd", "id": 7, "cmd": "connect", "role": "leader", "port": "COM5"}
```

| cmd | 인자 | 하는 일 |
|---|---|---|
| `scan_ports` | – | USB 포트마다 서보 응답 확인 |
| `connect` | `role`(leader/follower), `port` | 연결 (포트는 설정 파일에 저장) |
| `disconnect` | `role` | 연결 해제 (연동 중이면 먼저 정지) |
| `mirror` | `on` | 유니티로 리더 각도 보내기 켜기/끄기 |
| `set_source` | `source`(leader/unity/off) | 흰 팔 입력 소스 (연동 중이면 정지) |
| `follow` | `on`, `dry_run` | 흰 팔 연동 시작/정지. dry_run이면 흰 팔에 안 씀 |
| `goto_pose` | `pose`(l_pose/rest), `dry_run` | 흰 팔을 ㄱ자(모두 0°) 또는 쉬는 자세(`rest_pose_deg`)로 자동 이동. 벨트에 안 닿게 ① 어깨를 `clear_pan_deg`(45°)로 → ② 나머지 관절 → ③ 어깨 되돌리기, `pose_speed_dps`(20°/초) |
| `stop` | – | 흰 팔을 지금 실제 위치에서 정지 (힘 유지). 연동·자세 이동 둘 다 멈춤 |
| `estop` | – | 비상 정지: stop + 해제 전까지 연동 잠금 |
| `estop_reset` | – | 비상 정지 해제 |
| `release` | `confirm`: true | 흰 팔 힘 빼기 (팔이 떨어짐 → 받치고) |
| `capture_zero` | `role`, `method`(l_pose/rest) | 영점 저장. rest는 흰 팔만(두 팔 쉬는 자세 차이) |
| `reset_zero` | – | 흰 팔 영점을 설정 파일 `follower_zero_default`(10/23 쉬는 자세 기준)로 되돌림 |
| `get_status` | – | 상태 한 번 |

### `so101_ack` (5062) — 명령 응답
```json
{"type": "so101_ack", "id": 7, "ok": true, "msg": "리더 COM5 연결 (응답 6/6)"}
```

### `so101_status` (5062) — 상태
```json
{"type": "so101_status", "t": 1792634500.1,
 "leader":   {"connected": true, "port": "COM5", "hz": 30.0, "ticks": [..6], "deg": [..6], "zero_ready": true},
 "follower": {"connected": true, "port": "COM6", "hz": 30.0, "ticks": [..6], "deg": [..6], "zero_ready": true},
 "mirror": true, "sent": 1520, "source": "leader", "follow": false, "dry_run": false,
 "estop": false, "torque": false, "cmd_deg": null, "clipped": [false, ...], "follow_ids": [1,2,3,4,5],
 "safe_range": [[-105,105], [-108,95], ...], "max_speed_dps": 30, "last_error": "",
 "motion": "ㄱ자", "motion_step": 2, "motion_total": 3}
```

## 안전 규칙 (브리지 안에서 항상)
- 흰 팔 목표는 **안전 범위**(관절 한계에서 5° 안쪽, 예외는 설정 파일 `safe_range_override`)로 자름. 시작 자세가 범위 밖이면 그 자세까지는 허용
- 속도 제한 `max_speed_dps`(기본 30°/초). 시작은 흰 팔 **지금 위치**에서
- 입력이 `watchdog_s`(0.3초) 넘게 안 오면 자동 정지. 정지·비상 정지는 흰 팔 **지금 실제 위치**를 목표로 씀
- 그리퍼는 `follow_gripper`가 true일 때만 (v1 기본 false)
- 소프트웨어 정지와 별개로 흰 팔 전원 어댑터를 바로 뽑을 수 있게 둘 것

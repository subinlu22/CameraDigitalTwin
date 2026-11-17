# 렌더 룩 콘솔 메시지 (UDP, 127.0.0.1)

| 포트 | 방향 | type | 언제 |
|---|---|---|---|
| 5070 | 콘솔 → 유니티 | `render_cmd` | 고르거나 슬라이더를 움직일 때 (슬라이더는 0.05초에 한 번) |
| 5071 | 유니티 → 콘솔 | `render_state` | 바뀔 때(0.1초 모아서) + 1초마다 |

로봇팔 포트(5060~5063)·카메라 프로젝트(5053)와 겹치지 않음. 포트는 `RenderLookLink.cs`·`console.py` 맨 위 상수.

## `render_cmd` (5070)

```json
{"type": "render_cmd", "cmd": "look", "id": "robot_warm_workshop"}
```

| cmd | 같이 보내는 값 | 하는 일 |
|---|---|---|
| `hello` | — | 상태를 바로 보내 달라 (콘솔이 연결 안 됐을 때 2초마다) |
| `rescan` | — | 프리셋 파일·부위를 다시 읽음 |
| `look` | `id` (빈 값 = 원래대로) | 조명 분위기 고르기. 세부 수치는 프리셋 값으로 |
| `look_params` | `look`: `{ambient, reflection, sceneLight, exposure, contrast, saturation, bloom}` | 세부 수치만 바꿈 |
| `part` | `part`: `{id, mode, preset, metallic, roughness, normalStrength, tiling, color[3]}`, `pick` | 부위 재질. `mode` = `original` / `replace`(전부 바꾸기) / `keep`(텍스처 유지·수치만). `pick: true`면 수치를 프리셋 기본값으로 채움 |
| `reset` | — | 조명·모든 부위를 처음 상태로 |
| `save_material` | `id`(부위), `name` | 부위의 지금 수치를 `materials/<프리셋>_custom.json`으로 (있으면 `_2`…, 원래 파일은 안 덮음) |
| `save_look` | `name` | 지금 룩 + 세부 수치를 `looks/<룩>_custom.json`으로 |

## `render_state` (5071)

```json
{"type": "render_state", "scene": "SampleScene", "seq": 12, "msg": "로봇 몸체: 새틴 알루미늄 · 텍스처 유지·수치만",
 "look": "robot_warm_workshop", "lookParams": {"ambient": 0.9, "...": 0},
 "looks": [{"id", "name", "note"}], "materials": [{"id", "name", "note", "category", "color", "metallic", "roughness"}],
 "parts": [{"id": "robot_body", "label": "로봇 몸체", "auto": false, "found": true, "count": 20}],
 "states": [{"id": "robot_body", "mode": "keep", "preset": "satin_aluminum", "metallic": 1, "...": 0}]}
```

- `parts`: 이름표 부위(`parts/<씬>.json`) 먼저, 그다음 장면에서 자동으로 찾은 Standard 재질 (`id` = `mat:<재질 이름>`)
- `found: false` = 그 부위가 지금 꺼진 오브젝트에만 있음 (예: 룩을 꺼서 숨은 공장 룸)

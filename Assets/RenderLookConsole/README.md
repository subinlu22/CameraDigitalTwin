# 렌더 룩 콘솔 (Render Look Console)

유니티 장면의 **재질·조명 분위기를 따로 뜬 창에서 실시간으로 바꿔 보는 도구**.
프리셋(JSON)을 골라 바로 비교하고, 슬라이더로 다듬은 값을 새 프리셋으로 저장한다.
재질·조명을 매번 손으로(또는 AI에게) 맞추지 않아도 되게 하는 것이 목적.

- 어느 장면에나 붙음: 부위 목록은 장면에서 자동으로 찾음 (코드 수정 없음)
- 원본 재질 파일은 바꾸지 않음: 복제본을 입혔다가 [원래대로]로 돌림
- 에디터 플레이 중 / 빌드 exe 둘 다 동작 (프리셋은 StreamingAssets에서 읽음)
- 프리셋 형식은 ProductRenderStudio 렌더툴과 같음 → 두 툴이 같은 파일을 씀

## 다른 프로젝트에 넣기 (3단계)

1. 이 폴더(`Assets/RenderLookConsole/`)와 `Assets/StreamingAssets/RenderTool/`을 복사
2. 유니티에서 플레이 (장면에 아무것도 안 넣어도 자동으로 생김)
3. `Tools/render_console/start.bat` 더블클릭 → 콘솔 창이 뜨고 "유니티 연결됨"

필요한 것: Built-in Render Pipeline, Standard 셰이더 재질, Post Processing v2, 새 Input System, Python 3.10+ (표준 라이브러리만).

## 화면

| 영역 | 하는 일 |
|---|---|
| 조명 분위기 | 룩 프리셋 고르기 → 환경광·반사·원래 조명·노출·대비·채도·빛 번짐 다듬기 |
| 부위 | 이름표 부위 + 장면에서 찾은 재질 (검색) |
| 재질 | 적용 방식(원래 재질 / 전부 바꾸기 / 텍스처 유지·수치만) → 재질 프리셋 → 금속·거칠기·결 세기·결 크기·색 |
| 위 버튼 | 원래대로 / 룩 저장 / 재질 저장 |

플레이 화면에서 바로 고르는 작은 창도 있음: **F6** (빌드 exe에서 콘솔 없이 쓸 때).

## 프리셋 파일 (`StreamingAssets/RenderTool/`)

| 폴더 | 내용 |
|---|---|
| `looks/*.json` | 조명 분위기: HDRI, 추가 조명, 반사에만 보이는 소프트박스, 반사 프로브, 후처리 |
| `materials/*.json` | 재질: 색, 금속, 거칠기, 결(디테일 노멀맵) |
| `textures/detail/` | 결 텍스처 |
| `hdri/` | HDRI (용량이 커서 git 제외 → `python Tools/fetch_polyhaven.py`) |
| `parts/<씬 이름>.json` | (선택) 부위 이름표: 재질 여러 개를 "로봇 몸체"처럼 묶고, 조명을 맞출 초점(`focus`) 지정 |

부위 이름표 예:

```json
{ "focus": "so101_new_calib_camera",
  "parts": [ { "id": "robot_body", "label": "로봇 몸체", "root": "so101_new_calib_camera",
               "materials": ["3d_printed", "RobotYellow_Photo"] } ] }
```

## 구조

```
콘솔 창 (Edge 앱, web/)  ──HTTP──  console.py (127.0.0.1:8770)  ──UDP 5070──▶  RenderLookLink ─▶ RenderLookCore ─▶ 장면
                                                               ◀──UDP 5071──  (부위·프리셋 목록, 지금 값)
```

| 파일 | 역할 |
|---|---|
| `Runtime/RenderLookCore.cs` | 본체: 부위 찾기, 룩·재질 적용·원복, 저장, 다른 스크립트가 덮으면 다시 입힘 |
| `Runtime/RenderLookLink.cs` | 콘솔과 UDP로 주고받기 (받기는 별도 스레드 → 메인 스레드에서 적용) |
| `Runtime/RenderLookPanel.cs` | F6 간단 창 |
| `Runtime/RenderLookData.cs` · `RenderLookFiles.cs` | 프리셋 JSON 형식 · 파일·이미지·HDRI 읽기 |
| `Tools/render_console/` | 콘솔 (파이썬 서버 + 웹 화면) |

메시지 형식: `docs/render_look_messages.md`

## 알려진 한계

- Standard 셰이더 재질만 자동 부위로 잡음 (URP/HDRP는 아직 안 됨)
- 렌더툴의 배경지·촬영 카메라·거칠기 변화 맵은 안 씀 (장면과 카메라는 그대로 두는 도구라서)
- 결(디테일 노멀)은 메시에 UV가 있어야 보임

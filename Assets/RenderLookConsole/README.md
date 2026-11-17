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

유니티처럼 생긴 렌더 전용 창: 가운데 **렌더 뷰**(유니티 카메라 그림, UI 없이 실시간) + 왼쪽 유리 패널 두 장.

| 영역 | 하는 일 |
|---|---|
| 렌더 뷰 | 유니티 Main Camera 그림 (후처리 포함, 유니티 UI는 빠짐). 콘솔 칸 크기 그대로 그려서 선명. 휠 = 확대(최대 12배, 유니티가 그 부분을 다시 그림) · 끌기 = 이동 · 더블클릭 = 전체 · Tab = 패널 숨기기. 재질 공을 끌어다 놓으면 고른 부위에 입힘 |
| 룩 | 룩 카드(처음 고를 때 사진이 자동으로 찍힘) → 조명 세부: 환경광·반사·원래 조명·노출·대비·채도·빛 번짐 |
| 부위 재질 | 이름표 부위 + 장면에서 찾은 재질(검색). 공 그림·원본/기본/커스텀 표시 |
| 재질 작업대 | 원본 / 전부 바꾸기 / 텍스처 유지 → 기본 재질 카드(끌어놓기) → 색·금속성·거칠기·결 세기·결 크기 (파란 띠 = 권장 범위) |
| 버튼 | 슬라이더마다 ↺ = 그 값만 프리셋 기본값으로 · 원래대로 / 룩 저장 / 원본으로 / 내 재질로 저장 · 밝은·어두운 테마 |

플레이 화면에서 바로 고르는 작은 창도 있음: **F6** (빌드 exe에서 콘솔 없이 쓸 때).

## 프리셋 파일 (`StreamingAssets/RenderTool/`)

| 폴더 | 내용 |
|---|---|
| `looks/*.json` | 조명 분위기: HDRI, 추가 조명, 반사에만 보이는 소프트박스, 반사 프로브, 후처리 |
| `materials/*.json` | 재질: 색, 금속, 거칠기, 결(디테일 노멀맵) |
| `textures/detail/` | 결 텍스처 |
| `thumbnails/` | 재질 공 사진 (렌더툴에서 구운 것) · `looks/` = 룩 카드 사진 (자동) |
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
                         ◀─MJPEG─                              ◀──TCP 5072──  RenderLookStream (렌더 뷰 JPG)
```

| 파일 | 역할 |
|---|---|
| `Runtime/RenderLookCore.cs` | 본체: 부위 찾기, 룩·재질 적용·원복, 저장, 다른 스크립트가 덮으면 다시 입힘 |
| `Runtime/RenderLookLink.cs` | 콘솔과 UDP로 주고받기 (받기는 별도 스레드 → 메인 스레드에서 적용) |
| `Runtime/RenderLookStream.cs` | 렌더 뷰 보내기 (카메라 한 장 더 그림 → GPU 비동기 읽기 → JPG → TCP) + 룩 썸네일 자동 저장 |
| `Runtime/RenderLookPanel.cs` | F6 간단 창 |
| `Runtime/RenderLookData.cs` · `RenderLookFiles.cs` | 프리셋 JSON 형식 · 파일·이미지·HDRI 읽기 |
| `Tools/render_console/` | 콘솔 (파이썬 서버 + 웹 화면) |

메시지 형식: `docs/render_look_messages.md`

## 알려진 한계

- Standard 셰이더 재질만 자동 부위로 잡음 (URP/HDRP는 아직 안 됨)
- 렌더툴의 배경지·촬영 카메라·거칠기 변화 맵은 안 씀 (장면과 카메라는 그대로 두는 도구라서)
- 결(디테일 노멀)은 메시에 UV가 있어야 보임
- 렌더 뷰는 카메라를 한 번 더 그리므로 콘솔이 켜져 있는 동안 FPS가 조금 내려감 (콘솔을 끄면 안 그림)

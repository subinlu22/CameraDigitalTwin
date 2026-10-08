# CameraDigitalTwin — 컨베이어 검사 디지털트윈 (Unity)

산업용 카메라 검사 프로그램([CameraNumberDetect](https://github.com/subinlu22/CameraNumberDetect))의 판정 결과를 UDP로 받아, **Unity 안의 컨베이어벨트 위에 같은 색·숫자의 뚜껑을 실시간으로 재현**하는 디지털트윈입니다. 벨트·카메라·링라이트·뚜껑은 Maya로 직접 모델링하고 Substance Painter로 텍스처링했습니다.

🔗 [Portfolio](https://subinlu22.github.io)

<p align="center">
  <img src="docs/images/unity-scene.png" alt="Unity 장면"><br>
  <sub>Unity 에디터 Scene 뷰 — 벨트, 카메라 거치대, 링라이트, 뚜껑 9종</sub>
</p>

---

## 데모

| [![제품 스폰 시뮬레이션](https://img.youtube.com/vi/XKPuIZVpcmo/hqdefault.jpg)](https://youtu.be/XKPuIZVpcmo) | [![비전 판정 검사](https://img.youtube.com/vi/QqhlZ-_pYNY/hqdefault.jpg)](https://youtu.be/QqhlZ-_pYNY) |
|---|---|
| 제품 스폰 시뮬레이션 및 이송 과정 | 적색 임계값 기반 비전 판정 검사 |

## 프로젝트 개요

| 항목 | 내용 |
|---|---|
| 기간 | 2026.09 ~ |
| 인원 / 역할 | 1인 — 3D 모델링, Unity 구현, UDP 연동, 로봇팔 가져오기 |
| 목적 | 현실 장비의 검사 결과를 가상 공간에 실시간 반영 (현실 ↔ 가상 동기화) |
| 연동 대상 | [CameraNumberDetect](https://github.com/subinlu22/CameraNumberDetect) (UDP 포트 5053) |

## 구현 현황

| 기능 | 상태 |
|---|---|
| 카메라 프로그램 ↔ Unity UDP 연동 | ✅ |
| 판정 결과대로 뚜껑 생성 → 벨트 이동 → 끝에서 낙하 | ✅ |
| 대시보드 (총 검사, 양품/불량/양품률, 색상별·숫자별 개수) | ✅ |
| 판정 순간 화면 테두리 효과 (양품 초록 / 불량 빨강) | ✅ |
| 컨베이어벨트·카메라 URDF 변환 | ✅ |
| SO-101 로봇팔 URDF 가져오기, 관절 테스트 스크립트 | ✅ |
| 로봇팔 관절 각도를 UDP로 받아 실제 로봇팔과 연동 | 🔲 진행 중 |
| 뚜껑 위치를 매 프레임 동기화 | 🔲 계획 |
| 로봇팔로 뚜껑 집기 | 🔲 계획 |

## 환경

| 구분 | 내용 |
|---|---|
| 엔진 | Unity 6 (6000.3.11f1), 3D Built-in Render Pipeline |
| 언어 | C# |
| 모델링 | Maya (FBX `cam_s_merge`, FBX Scale Factor 2.2), Substance Painter |
| 로봇 | URDF Importer (`Packages` 폴더에 직접 포함), ArticulationBody |
| 통신 | UDP, 포트 5053 |

<p align="center">
  <img src="docs/images/model.png" alt="모델 단독" width="400"><br>
  <sub>직접 모델링한 장비 (뚜껑 9종: 색 3 × 숫자 3)</sub>
</p>

<p align="center">
  <img src="docs/images/real-setup.jpg" alt="실제 장비" width="460"><br>
  <sub>대응하는 실제 장비</sub>
</p>

## 시스템 구조

```
[카메라 프로그램] ──UDP 5053──▶ DigitalTwinReceiver
                                  │  수신은 별도 스레드 → 큐 → Update(메인 스레드)에서 처리
                                  ▼
              같은 색/숫자의 견본 뚜껑을 복제해 SpawnPoint에 생성
                                  ▼
              LidMover: 벨트 위는 위치를 직접 이동 → EndPoint에서 물리를 켜 낙하 → 3초 뒤 삭제
                                  ▼
              DashboardUI 갱신 · 판정 테두리 효과
```

| 구성 요소 | 역할 |
|---|---|
| `DigitalTwinReceiver` | UDP 수신, 메시지 type(`inspection` / `criterion`) 구분, 뚜껑 생성 |
| `LidMover` | 벨트 위 등속 이동, 끝점에서 낙하 처리 |
| `DashboardUI` | 코드로 만드는 우측 글래스 패널 (통계, 검사 기준 배지, 비율 막대) |
| `SpawnPoint` / `EndPoint` | FBX 밖 빈 오브젝트 (모델을 교체해도 연동이 깨지지 않게 분리) |
| `DroopRobotPose` | 로봇팔 중력·저항·충돌 설정 |
| `RobotJointTester` | 인스펙터 슬라이더로 6개 관절 각도 제어 |

<p align="center">
  <img src="docs/images/dashboard.png" alt="대시보드"><br>
  <sub>대시보드 UI (수신 대기 상태)</sub>
</p>

## UDP 메시지 (포트 5053)

| type | 동작 |
|---|---|
| `inspection` | 검사 결과 1건 수신 → 같은 색·숫자의 뚜껑 생성, 통계 갱신, 테두리 효과 |
| `criterion` | 검사 기준 변경 알림 → 양품/불량 집계를 0부터 다시 시작 (총 검사·색상별·숫자별은 유지) |

## 설계 결정과 근거

| 결정 | 근거 |
|---|---|
| 벨트 위 이동은 물리가 아니라 위치를 직접 이동 | 매번 똑같이 움직여 현실 장비와 맞추기 쉬움. 결과가 조금 달라도 되는 낙하 연출에만 물리 사용 |
| 수신 스레드와 처리 분리 (큐 사용) | Unity 오브젝트는 메인 스레드에서만 다룰 수 있음 |
| SpawnPoint / EndPoint를 FBX 밖에 둠, 견본 뚜껑은 이름으로 연결 | 모델을 다시 내보내도 연동이 깨지지 않음 |
| 검사 기준이 바뀌면 양품/불량만 0부터 다시 집계 | 기준(색)마다 양품의 정의가 달라 섞이면 의미가 없음. 총 검사·색상별·숫자별은 유지 |
| FBX Scale Factor 2.2 | 실측 비율보다 화면에서 자연스러운 비율을 우선 |
| URDF Importer를 `Packages` 폴더에 직접 포함 | 프로젝트와 함께 커밋되어 다른 PC에서도 그대로 동작 |

## 시행착오 — 시도했다가 바꾼 것

| 시도 | 문제 | 바꾼 방식 |
|---|---|---|
| 벨트 위 뚜껑을 물리로 이동 | 마찰·충돌 결과가 매번 달라 실제 장비와 동기화가 안 됨 | 위치를 직접 옮겨 등속 이동, 낙하에만 물리 사용 |
| URDF Importer를 git 주소로 설치 | `fatal: not in a git directory` — Unity가 PC의 git을 인식하지 못함 (터미널에서는 정상) | GitHub에서 ZIP으로 받아 패키지 폴더를 `Packages`에 직접 복사 |
| 로봇팔을 충돌 포함 원본 URDF로 가져오기 | 에러 없이 Mesh Filter만 Missing. 충돌용 STL을 `_col` 이름으로 분리하거나 폴더를 새로 만들어도 동일 | `<collision>` 블록 19개만 뺀 visual 전용 URDF로 가져오기 (링크·관절·회전 범위·질량은 유지), 성공 직후 프리팹으로 저장 |

## 트러블슈팅

| 문제 | 원인 | 해결 |
|---|---|---|
| UDP로 받은 결과로 뚜껑을 만들면 에러가 나거나 반영되지 않음 | 수신 루프는 별도 스레드인데 Unity 오브젝트는 메인 스레드에서만 접근 가능 | 수신 스레드는 메시지를 큐에 넣기만 하고, `Update()`에서 꺼내 뚜껑 생성 |
| 뚜껑이 벨트 위에서 매번 다르게 움직여 실제 장비와 타이밍이 어긋남 | 물리(Rigidbody) 마찰·충돌 결과가 매 실행 달라짐 | 벨트 위는 위치를 직접 옮겨 등속 이동, EndPoint 도착 후에만 물리를 켜서 낙하 |
| FBX를 다시 내보내면 생성 위치와 견본 연결이 끊김 | 기준점이 FBX 내부 오브젝트에 묶여 있었음 | SpawnPoint / EndPoint를 FBX 밖 빈 오브젝트로 분리, 견본은 이름(r1, b2 …)으로 연결 |
| 견본 뚜껑의 숫자가 실제와 다름 (r1이 실제로는 숫자 3) | FBX의 뚜껑 이름이 1↔3 뒤바뀌어 export됨 | Unity Hierarchy에서 이름 정정 (Maya 원본은 다음 재export 때 반영) |
| 로봇팔이 힘 풀린 경첩처럼 처지고 바닥을 뚫음 | ArticulationBody의 Stiffness/Damping이 0이고 충돌 모양이 없음 | `DroopRobotPose`: 관절 끝쪽 무게 비율로 저항을 자동 계산(손목은 0.05로 별도 지정), 보이는 메시마다 MeshCollider(convex) 자동 부착 |

## 실행 방법

1. Unity Hub에서 **Unity 6000.3.11f1**로 프로젝트를 엽니다.
2. `SampleScene`을 열고 Play를 누르면 UDP 5053 포트에서 수신을 시작합니다.
3. [CameraNumberDetect](https://github.com/subinlu22/CameraNumberDetect)를 실행해 뚜껑을 판정하면 Unity 벨트 위에 같은 뚜껑이 생성됩니다.
4. 카메라 없이 확인하려면 컴포넌트 `⋮` 메뉴의 **테스트: 빨강1 생성**, **테스트: 파랑2 불량**을 사용합니다.

---

🔗 [Portfolio](https://subinlu22.github.io)

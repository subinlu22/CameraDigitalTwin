# Camera Digital Twin (Unity)

🔗 **Portfolio**: https://subinlu22.github.io

산업용 카메라 검사 프로그램([CameraNumberDetect](https://github.com/subinlu22/CameraNumberDetect))의 판정 결과를 받아,
**Unity 안의 컨베이어벨트 위에 같은 색·숫자의 뚜껑을 실시간으로 재현**하는 디지털트윈입니다.
벨트·카메라·링라이트·뚜껑은 Maya로 직접 모델링하고 Substance Painter로 텍스처링했습니다.

> 📷 데모 영상 / 스크린샷 자리

## 주요 기능
- UDP(포트 5053)로 검사 결과 수신 → 같은 색/숫자 견본 뚜껑을 복제해 벨트 위에 생성
- 벨트 위에서는 일정한 속도로 이동, 끝에 도착하면 물리를 켜서 떨어뜨림
- 글래스 스타일 대시보드: 총 검사 수, 검사 기준, 양품·불량·양품률, 색상별·숫자별 개수
- 판정 순간 화면 테두리 빛 효과 (양품 초록 / 불량 빨강)
- 메시지를 type으로 구분 (inspection / criterion) → 이후 아두이노·로봇팔 메시지 확장 용이
- SO-101 로봇팔 URDF 가져오기 + 관절 테스트 (진행 중)

## 기술 스택
| 구분 | 사용 |
|---|---|
| 엔진 | Unity 6 (Built-in Render Pipeline), C# |
| 3D | Maya, Substance Painter |
| 통신 | UDP |
| 로봇 | URDF Importer, ArticulationBody |

## 구조
```
[카메라 프로그램] --UDP--> DigitalTwinReceiver (별도 스레드에서 수신 → 큐 → 메인 스레드에서 처리)
                              ↓
                        견본 뚜껑 복제 → SpawnPoint 생성 → LidMover로 이동 → EndPoint에서 낙하
                              ↓
                        DashboardUI 갱신
```
- SpawnPoint / EndPoint는 FBX 밖 빈 오브젝트로 분리해서, 모델을 교체해도 연동이 깨지지 않게 함

## 문제 해결 기록
| 문제 | 해결 |
|---|---|
| 수신 스레드에서 Unity 오브젝트를 만지면 에러 | 수신은 별도 스레드, 처리는 큐에 넣어 Update(메인 스레드)에서 |
| 벨트 위 물리 이동이 매번 달라 현실과 안 맞음 | 벨트 위는 위치를 직접 옮기고, 떨어지는 연출에만 물리 사용 |
| URDF Importer를 git 주소로 설치 실패 | 패키지 폴더를 `Packages`에 직접 복사 |
| 로봇팔 충돌 포함 URDF를 가져오면 메시가 끊김 | 충돌 블록만 뺀 visual 전용 URDF로 우회 |
| 로봇팔이 힘 풀린 경첩처럼 처짐 | 관절별 저항(damping)을 코드로 설정, 슬라이더로 각도 제어하는 테스트 스크립트 작성 |

## 앞으로 할 것
- 로봇팔 관절 각도를 UDP로 받아 실제 로봇팔과 연동
- 뚜껑 실시간 위치 동기화

---
🔗 **Portfolio**: https://subinlu22.github.io

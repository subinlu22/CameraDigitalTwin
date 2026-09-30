using System.Collections.Generic;
using UnityEngine;

// [컨베이어벨트 이동기]
// 벨트 윗면 박스(BeltSurface)에 붙이는 스크립트.
// 벨트에 닿아있는 물체(뚜껑)를 벨트 방향으로 같은 속도로 실어 나른다.
public class BeltMover : MonoBehaviour
{
    [Header("벨트 설정 (Play 중에도 Inspector에서 바꿀 수 있음)")]
    public bool isRunning = true;                          // 벨트 켜짐/꺼짐 — 체크박스로 토글
    public float speed = 0.1f;                             // 벨트 속도 (m/s). 너무 빠르면 0.05~0.2 사이로 조절
    public Vector3 localDirection = new Vector3(1, 0, 0);  // 벨트가 흐르는 방향 (이 오브젝트 기준 축). 빨간 선으로 확인 가능

    // [닿아있는 물체 목록] HashSet = 같은 물체가 두 번 들어가지 않는 목록
    // 물리 엔진은 가만히 있는 물체를 "잠들게" 해서 충돌 이벤트를 안 주기 때문에,
    // 닿은 순간(Enter)에 목록에 넣고 떨어질 때(Exit) 빼는 방식으로 관리한다
    private readonly HashSet<Rigidbody> bodiesOnBelt = new HashSet<Rigidbody>();

    // [닿기 시작] 뚜껑이 벨트에 닿는 순간 Unity가 자동으로 호출하는 함수
    void OnCollisionEnter(Collision collision)
    {
        // collision.rigidbody = 부딪힌 상대의 물리 몸. 물리 몸이 없는 물체면 null이라 건너뜀
        if (collision.rigidbody != null) bodiesOnBelt.Add(collision.rigidbody);
    }

    // [떨어짐] 뚜껑이 벨트에서 떨어지는 순간 Unity가 자동으로 호출하는 함수
    void OnCollisionExit(Collision collision)
    {
        if (collision.rigidbody != null) bodiesOnBelt.Remove(collision.rigidbody);
    }

    // FixedUpdate()는 Unity가 물리 계산 주기(기본 0.02초)마다 자동으로 호출하는 함수
    void FixedUpdate()
    {
        if (!isRunning) return;   // 벨트가 꺼져있으면 아무것도 안 함

        bodiesOnBelt.RemoveWhere(body => body == null);   // 삭제된 물체가 목록에 남아있으면 정리

        // [벨트 속도 계산] 로컬 방향을 월드 방향으로 바꾸고, 수평으로만 펴서(위아래 성분 제거) 속도를 곱함
        Vector3 worldDirection = transform.TransformDirection(localDirection);
        Vector3 beltVelocity = Vector3.ProjectOnPlane(worldDirection, Vector3.up).normalized * speed;

        foreach (Rigidbody body in bodiesOnBelt)
        {
            // [속도 교체] 위아래 속도(중력으로 떨어지는 힘)는 그대로 두고, 수평 속도만 벨트 속도로 바꿈
            // linearVelocity는 Unity 6에서 이름이 바뀐 속성 (예전 이름은 velocity)
            Vector3 verticalVelocity = Vector3.Project(body.linearVelocity, Vector3.up);
            body.linearVelocity = beltVelocity + verticalVelocity;
        }
    }

    // [방향 표시] BeltSurface를 선택했을 때 Scene 화면에 빨간 선으로 벨트 진행 방향을 그려줌
    void OnDrawGizmosSelected()
    {
        Collider surface = GetComponent<Collider>();
        if (surface == null) return;

        Gizmos.color = Color.red;
        Vector3 worldDirection = transform.TransformDirection(localDirection);
        Vector3 flatDirection = Vector3.ProjectOnPlane(worldDirection, Vector3.up).normalized;
        Gizmos.DrawRay(surface.bounds.center, flatDirection * 0.5f);   // 박스 중앙에서 방향으로 0.5m짜리 선
    }
}

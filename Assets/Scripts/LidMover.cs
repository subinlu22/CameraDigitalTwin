using UnityEngine;

// =============================================================
// [뚜껑 이동] 생성된 뚜껑을 목표 지점(벨트 끝)까지 일정한 속도로 밀어주고,
// 끝에 도착하면 그때부터 물리를 켜서 벨트 끝에서 떨어지게 하는 스크립트
// - 벨트 위: 물리 대신 위치를 직접 옮기는 방식 → 매번 똑같이 움직임 (디지털트윈은 "예측 가능한 움직임"이 중요)
// - 벨트 끝: 물리(중력)를 켜서 실제처럼 툭 떨어지게 → 몇 초 뒤 자동 삭제
// - 속도는 DigitalTwinReceiver의 beltSpeed를 매 프레임 읽어서 씀 → 플레이 중에 속도를 바꾸면 바로 반영
// =============================================================
public class LidMover : MonoBehaviour
{
    Vector3 targetPosition;         // 도착 지점
    Vector3 moveDirection;          // 이동 방향 (떨어질 때 앞으로 밀어주는 데 씀)
    DigitalTwinReceiver receiver;   // 속도 값을 가져올 곳

    const float destroyDelaySeconds = 3f; // 떨어지기 시작하고 몇 초 뒤에 삭제할지

    // 생성 직후 DigitalTwinReceiver가 한 번 호출해서 목표 지점과 속도 출처를 알려줌
    public void Init(Vector3 target, DigitalTwinReceiver owner)
    {
        targetPosition = target;
        receiver = owner;
        moveDirection = (target - transform.position).normalized; // normalized = 방향만 남기고 길이는 1로
    }

    void Update()
    {
        if (receiver == null) return; // 혹시 수신기가 먼저 사라졌으면 아무것도 안 함

        // MoveTowards = 목표 쪽으로 "최대 이만큼"만 이동 (지나치지 않고 딱 멈춤)
        // Time.deltaTime = 지난 프레임부터 걸린 시간 → 컴퓨터가 빠르든 느리든 초당 같은 거리를 이동
        transform.position = Vector3.MoveTowards(
            transform.position, targetPosition, receiver.beltSpeed * Time.deltaTime);

        // [수정] 벨트 끝에 도착하면 바로 삭제하지 않고, 물리를 켜서 떨어뜨림
        if (Vector3.Distance(transform.position, targetPosition) < 0.001f)
        {
            DropOffBeltEnd();
        }
    }

    // [신규] 벨트 끝에서 떨어지게 하는 함수 - 도착한 순간 딱 한 번 실행됨
    void DropOffBeltEnd()
    {
        // 물리 충돌용 콜라이더가 없으면 하나 붙여줌 (없으면 바닥을 뚫고 계속 떨어짐)
        // MeshCollider는 붙이는 순간 이 오브젝트의 모양(Mesh)을 자동으로 가져다 씀
        Collider col = GetComponent<Collider>();
        if (col == null && GetComponent<MeshFilter>() != null)
        {
            col = gameObject.AddComponent<MeshCollider>();
        }

        // 움직이는 물리 오브젝트의 MeshCollider는 convex(볼록한 단순 모양)여야 함 - 아니면 유니티가 에러를 냄
        MeshCollider meshCol = col as MeshCollider;
        if (meshCol != null)
        {
            meshCol.convex = true;
        }

        // Rigidbody = 유니티 물리(중력, 충돌)의 영향을 받게 해주는 컴포넌트
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        rb.isKinematic = false; // 생성할 때 꺼뒀던 물리를 다시 켬
        rb.useGravity = true;

        // 벨트가 밀던 속도 그대로 앞으로 나가면서 떨어지게 (안 주면 제자리에서 수직으로 뚝 떨어짐)
        // linearVelocity = 초당 이동 속도 (Unity 6에서 예전 이름 velocity가 바뀐 것)
        rb.linearVelocity = moveDirection * receiver.beltSpeed;

        Destroy(gameObject, destroyDelaySeconds); // 몇 초 뒤 자동 삭제
        enabled = false; // 이 스크립트의 Update를 멈춤 → 이제부터는 물리가 알아서 움직임
    }
}

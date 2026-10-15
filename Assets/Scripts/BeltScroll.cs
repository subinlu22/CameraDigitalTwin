using UnityEngine;

// [컨베이어벨트 천 스크롤]
// 벨트 천 메시(Conveyer_Belt2, Conveyer_Belt2_Loop 메시)에 붙이는 스크립트.
// BeltMover의 속도/켜짐 상태를 그대로 읽어서 천 무늬를 같은 속도로 흘려보낸다 (눈으로 보이는 것만, 물리는 BeltMover 담당).
[RequireComponent(typeof(Renderer))]
public class BeltScroll : MonoBehaviour
{
    public BeltMover beltMover;        // 속도/켜짐을 따라갈 BeltMover (BeltSurface)
    public float tileLength = 0.05f;   // 텍스처 한 장이 벨트 위에서 차지하는 길이(m). 메시 UV 만들 때 값과 같아야 속도가 맞음
    public bool reverse = false;       // 무늬가 뚜껑과 반대로 흐르면 체크

    private Renderer rend;
    private MaterialPropertyBlock block;   // 재질 에셋을 건드리지 않고 이 렌더러에만 값을 덮어쓰는 상자
    private float offset;

    void Awake()
    {
        rend = GetComponent<Renderer>();
        block = new MaterialPropertyBlock();
    }

    void Update()
    {
        if (beltMover == null || !beltMover.isRunning || tileLength <= 0f) return;

        // UV의 v가 벨트 진행 방향(+)이므로, 무늬가 앞으로 가려면 offset을 줄인다
        offset -= (reverse ? -1f : 1f) * beltMover.speed / tileLength * Time.deltaTime;
        offset = Mathf.Repeat(offset, 1f);   // 숫자가 끝없이 커지지 않게 0~1로 감기

        // _MainTex_ST = (타일링 x, 타일링 y, 오프셋 x, 오프셋 y)
        rend.GetPropertyBlock(block);
        block.SetVector("_MainTex_ST", new Vector4(1f, 1f, 0f, offset));
        rend.SetPropertyBlock(block);
    }
}

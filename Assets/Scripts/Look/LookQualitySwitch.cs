using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

// =============================================================
// [가벼운 연습 룩 ↔ 데모 룩] + [FPS 표시]
//
// 왜: 데모 룩(거울 반사·후처리·실시간 반사 프로브)은 예쁘지만 무거움 → 연습할 땐 꺼서 가볍게, 촬영할 땐 켬
// 키: F4 = FPS 표시 켜기/끄기 (왼쪽 위 작은 글자)
//     F5 = 가벼운 연습 룩 ↔ 데모 룩
// 가벼운 룩에서 끄는 것 (조명·재질·카메라는 그대로 — LookPresetApplier가 맡음)
//   ① 밑판 거울 반사 (PlanarReflection) : 장면을 한 번 더 찍던 것
//   ② 후처리 (Main Camera의 PostProcessLayer)
//   ③ 실시간 반사 프로브 : 매 프레임 다시 찍기 → 멈춤 (마지막 그림 그대로 씀)
// 다시 F5 = 끄기 전 상태로 되돌림. L 키로 프리셋을 바꾸면 가벼운 룩을 그 프리셋에 다시 적용
// FPS: 1초 평균. 룩을 바꿀 때 "바꾸기 전 룩의 평균 FPS"를 콘솔에 남기고 화면에도 둘 다 보여 줌 (FPS 측정 기록용)
// 씬에 안 넣어도 됨: 플레이를 시작하면 자동으로 하나 만들어짐 (빌드 exe도 같음)
// =============================================================
public class LookQualitySwitch : MonoBehaviour
{
    public Key fpsKey = Key.F4;
    public Key lightKey = Key.F5;
    public bool showFps = false;
    public bool startLight = false;        // 켜면 시작할 때부터 가벼운 룩

    public bool IsLight { get; private set; }
    public float Fps { get; private set; }

    // 가벼운 룩으로 바꾸며 끈 것들 (되돌리기용)
    readonly List<PlanarReflection> offPlanar = new List<PlanarReflection>();
    readonly List<PostProcessLayer> offPost = new List<PostProcessLayer>();
    readonly List<ReflectionProbe> slowedProbes = new List<ReflectionProbe>();

    LookPresetApplier applier;
    int appliedIndex = int.MinValue;       // 가벼운 룩을 적용한 프리셋 번호 (L로 바뀌면 다시 적용)
    int frames; float span;
    float sumFps; int sumCount;            // 지금 룩의 평균
    float lightAvg = -1f, demoAvg = -1f;   // 마지막으로 잰 룩별 평균
    GUIStyle style;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (FindFirstObjectByType<LookQualitySwitch>()) return;
        var go = new GameObject("LookQualitySwitch");
        go.AddComponent<LookQualitySwitch>();
    }

    void Start()
    {
        applier = FindFirstObjectByType<LookPresetApplier>();
        if (startLight) SetLight(true);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[fpsKey].wasPressedThisFrame) showFps = !showFps;
        if (kb != null && kb[lightKey].wasPressedThisFrame) SetLight(!IsLight);

        // L 키로 프리셋이 바뀌면 후처리·반사 프로브가 다시 켜짐 → 가벼운 룩 다시 적용
        if (IsLight && applier && applier.Index != appliedIndex) ApplyLight();

        // FPS: 1초마다 평균
        frames++; span += Time.unscaledDeltaTime;
        if (span >= 1f)
        {
            Fps = frames / span;
            sumFps += Fps; sumCount++;
            frames = 0; span = 0f;
        }
    }

    public void SetLight(bool on)
    {
        if (on == IsLight) return;
        // 바꾸기 전 룩의 평균 FPS 기록
        if (sumCount > 0)
        {
            float avg = sumFps / sumCount;
            if (IsLight) lightAvg = avg; else demoAvg = avg;
            Debug.Log($"[룩] {(IsLight ? "가벼운 연습 룩" : "데모 룩")} 평균 {avg:F0} FPS ({sumCount}초 동안)");
        }
        sumFps = 0f; sumCount = 0;

        IsLight = on;
        if (on) ApplyLight(); else RestoreDemo();
        Debug.Log(on ? "[룩] 가벼운 연습 룩 (거울 반사·후처리·실시간 반사 끔)" : "[룩] 데모 룩으로 되돌림");
    }

    void ApplyLight()
    {
        appliedIndex = applier ? applier.Index : int.MinValue;
        foreach (var p in FindObjectsByType<PlanarReflection>(FindObjectsSortMode.None))
            if (p.enabled) { p.enabled = false; offPlanar.Add(p); }
        foreach (var p in FindObjectsByType<PostProcessLayer>(FindObjectsSortMode.None))
            if (p.enabled) { p.enabled = false; offPost.Add(p); }
        foreach (var p in FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
            if (p.mode == ReflectionProbeMode.Realtime && p.refreshMode == ReflectionProbeRefreshMode.EveryFrame)
            { p.refreshMode = ReflectionProbeRefreshMode.ViaScripting; slowedProbes.Add(p); }
    }

    void RestoreDemo()
    {
        foreach (var p in offPlanar) if (p) p.enabled = true;
        // 후처리는 프리셋이 켜져 있을 때만 다시 켬 (L로 프리셋을 끈 뒤라면 꺼진 채가 맞음)
        foreach (var p in offPost) if (p && (!applier || applier.IsOn)) p.enabled = true;
        foreach (var p in slowedProbes) if (p) p.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
        offPlanar.Clear(); offPost.Clear(); slowedProbes.Clear();
        appliedIndex = int.MinValue;
    }

    void OnGUI()
    {
        if (!showFps) return;
        if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
        string line = $"<b>{Fps:F0} FPS</b> · {(IsLight ? "가벼운 연습 룩" : "데모 룩")}  <size=11>(F5 바꾸기 · F4 숨기기)</size>";
        if (lightAvg > 0f || demoAvg > 0f)
            line += $"\n<size=12>평균 — 연습 룩 {(lightAvg > 0f ? lightAvg.ToString("F0") : "—")} / 데모 룩 {(demoAvg > 0f ? demoAvg.ToString("F0") : "—")}</size>";
        GUI.color = new Color(0, 0, 0, .55f);
        GUI.Box(new Rect(8, 8, 300, lightAvg > 0f || demoAvg > 0f ? 46 : 28), GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(new Rect(14, 11, 290, 40), line, style);
    }
}

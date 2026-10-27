using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 편집 화면에서 프리셋 미리보기 (플레이 안 눌러도 공장·스튜디오를 보면서 색·재질 조절)
// 흐름: 메뉴로 켜면 LookPresetApplier.Select(i) 를 편집 모드에서 호출
//       저장 직전엔 잠깐 끄고(씬 파일에 미리보기 상태가 안 들어가게) 저장 후 다시 켬
//       플레이 누르면 미리보기를 끄고 들어감 (플레이 때는 원래대로 자동 적용)
[InitializeOnLoad]
public static class LookPresetPreview
{
    static int _index = -1;      // 지금 미리보기 중인 프리셋 (-1 = 끔)
    static int _resume = -1;     // 저장 끝나면 다시 켤 번호

    static LookPresetPreview()
    {
        EditorSceneManager.sceneSaving += (s, p) => { _resume = _index; Set(-1); };
        EditorSceneManager.sceneSaved += s => { if (_resume >= 0) Set(_resume); _resume = -1; };
        EditorApplication.playModeStateChanged += st => { if (st == PlayModeStateChange.ExitingEditMode) Set(-1); };
        // 스크립트 다시 컴파일될 때 되돌릴 값이 사라지므로, 그 전에 끄고 끝나면 다시 켬
        AssemblyReloadEvents.beforeAssemblyReload += () => { SessionState.SetInt("LookPreview", _index); Set(-1); };
        EditorApplication.delayCall += () =>
        {
            int i = SessionState.GetInt("LookPreview", -1);
            SessionState.SetInt("LookPreview", -1);
            if (i >= 0 && !EditorApplication.isPlayingOrWillChangePlaymode) Set(i);
        };
    }

    [MenuItem("SO101/룩 미리보기/공장 %#1")] static void Factory() => Set(0);
    [MenuItem("SO101/룩 미리보기/스튜디오 %#2")] static void Studio() => Set(1);
    [MenuItem("SO101/룩 미리보기/끄기 %#0")] static void Off() => Set(-1);

    static void Set(int i)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode && i >= 0) return;
        var ap = Object.FindFirstObjectByType<LookPresetApplier>();
        if (ap == null) return;
        ap.Select(i);
        _index = ap.Index;
        SceneView.RepaintAll();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// =============================================================
// [자세 저장] 유니티 로봇팔의 6관절 자세를 이름 붙여 저장하고 목록으로 관리 (화면은 TwinSidePanel의 "자세" 패널)
//
// 흐름
//   ① SaveCurrent : 유니티 팔에 지금 넣고 있는 6관절 목표 각도를 "자세 N"으로 목록 끝에 추가
//      (리더 모드에서도 됨 → 리더 팔을 손으로 잡아 자세를 만들고 저장)
//      리더로 흰 팔을 실제로 움직이는 중이면 흰 팔 실제 각도로 저장 → 사람이 한 번 집어 보이면 나중에 흰 팔 혼자 재생
//   ② Go / Rename / Swap / Delete : 목록 관리. Go는 유니티 조종 모드에서만
//      → ArmPoseMover로 벨트를 피하는 순서로 이동 (흰 팔이 연동 중이면 같이 움직임)
//   ③ 바뀔 때마다 파일에 바로 저장: StreamingAssets/so101_poses.json (빌드 exe에서도 읽힘)
// =============================================================
public class PoseLibrary : MonoBehaviour
{
    public string fileName = "so101_poses.json";

    [Serializable] public class Pose { public string name; public float[] deg = new float[6]; }
    [Serializable] class PoseFile { public List<Pose> poses = new List<Pose>(); }

    public IReadOnlyList<Pose> Poses => data.poses;
    public int Version { get; private set; }            // 목록이 바뀔 때마다 +1 (화면이 다시 그릴지 판단)
    public string Status { get; private set; } = "";
    public bool CanGo => arm && arm.Ready && arm.CurrentMode != UnityToFollowerGUI.Mode.Leader && !arm.EStopped;

    PoseFile data = new PoseFile();
    UnityToFollowerGUI arm;

    string FilePath => Path.Combine(Application.streamingAssetsPath, fileName);

    void Awake()
    {
        arm = GetComponent<UnityToFollowerGUI>();
        Load();
    }

    // ---------- 파일 ----------
    void Load()
    {
        try
        {
            if (File.Exists(FilePath)) data = JsonUtility.FromJson<PoseFile>(File.ReadAllText(FilePath)) ?? new PoseFile();
            Status = $"자세 {data.poses.Count}개 읽음";
        }
        catch (Exception e) { data = new PoseFile(); Status = "파일 읽기 실패: " + e.Message; }
        Version++;
    }

    void Changed(string status)
    {
        Status = status;
        Version++;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
        }
        catch (Exception e) { Status = "파일 저장 실패: " + e.Message; }
    }

    // ---------- 동작 ----------
    public void SaveCurrent()
    {
        if (!arm || !arm.Ready) return;
        // 리더로 흰 팔을 실제로 움직이는 중이면 흰 팔 "실제" 각도로 저장 (뚜껑을 쥔 집게 각도까지 실물 그대로)
        // 아니면 유니티 팔 목표 각도 (리더 모드면 리더 각도, 유니티 조종 모드면 슬라이더 각도)
        // 단, 집게는 "보낸 명령"(리더/유니티 각도)으로 저장: 뚜껑에 막혀 멈춘 실제 각도로 재생하면 서보가 힘을 거의 안 줘서 놓침
        //     → 같은 명령을 다시 보내야 같은 힘(서보 60% 제한)으로 쥠
        bool real = arm.Live && arm.FollowerMovingReal;
        var p = new Pose { name = $"자세 {data.poses.Count + 1}" };
        for (int i = 0; i < 6; i++)
        {
            float d = real && i < 5 ? arm.FollowerDeg(i) : arm.UnityDeg(i);
            p.deg[i] = Mathf.Round(d * 10f) / 10f;   // 0.1° 단위
        }
        data.poses.Add(p);
        Changed($"'{p.name}' 저장 ({(real ? "흰 팔 실제 각도 + 집게 명령" : "유니티 각도")})");
    }

    public void Go(int i)
    {
        if (!CanGo || i < 0 || i >= data.poses.Count) return;
        arm.MoveToPose(data.poses[i].deg);
        Status = $"'{data.poses[i].name}'(으)로 이동";
    }

    public void Rename(int i, string name)
    {
        if (i < 0 || i >= data.poses.Count || string.IsNullOrWhiteSpace(name) || name == data.poses[i].name) return;
        data.poses[i].name = name.Trim();
        Changed($"이름 바꿈: {data.poses[i].name}");
    }

    public void Swap(int i, int d)
    {
        int j = i + d;
        if (i < 0 || j < 0 || i >= data.poses.Count || j >= data.poses.Count) return;
        (data.poses[i], data.poses[j]) = (data.poses[j], data.poses[i]);
        Changed("순서 바꿈");
    }

    public void Delete(int i)
    {
        if (i < 0 || i >= data.poses.Count) return;
        string n = data.poses[i].name;
        data.poses.RemoveAt(i);
        Changed($"'{n}' 삭제");
    }
}

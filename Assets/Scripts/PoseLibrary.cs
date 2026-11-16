using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// =============================================================
// [자세 저장] 유니티 로봇팔의 6관절 자세를 이름 붙여 저장하고 목록으로 관리 (화면은 TwinSidePanel의 [자세] 탭)
//
// 구조: 그룹(동작 하나, 예 "테이블에서 컨벨 올리기") → 그 안에 자세 목록(순서 = 재생 순서)
//       화면에서 ◀ ▶로 그룹을 고르고, 아래 동작은 모두 "고른 그룹" 기준
// 흐름
//   ① SaveCurrent : 유니티 팔에 지금 넣고 있는 6관절 목표 각도를 "자세 N"으로 목록 끝에 추가
//      (리더 모드에서도 됨 → 리더 팔을 손으로 잡아 자세를 만들고 저장)
//      리더로 흰 팔을 실제로 움직이는 중이면 흰 팔 실제 각도로 저장 → 사람이 한 번 집어 보이면 나중에 흰 팔 혼자 재생
//   ② Go / Rename / Swap / Delete : 목록 관리. Go는 유니티 조종 모드에서만
//      → ArmPoseMover로 벨트를 피하는 순서로 이동 (흰 팔이 연동 중이면 같이 움직임)
//   ③ 바뀔 때마다 파일에 바로 저장: StreamingAssets/so101_poses.json (빌드 exe에서도 읽힘)
//   ④ [순서대로 재생] (유니티 조종 모드에서만): 1번부터 차례로 "그 자세로 이동 → 도착 → holdSec초 머묾" 반복
//      움직임은 [가기]와 같음 (초당 10°, 크게 움직일 땐 위로 들어 벨트를 피함) → 흰 팔이 실제 모드면 흰 팔도 재생
//      [정지] / 비상 정지 / 리더로 돌아가기 → 그 자리에서 멈춤
// =============================================================
public class PoseLibrary : MonoBehaviour
{
    public string fileName = "so101_poses.json";
    public float holdSec = 1.0f;       // 재생: 자세마다 도착 후 머무는 시간 (집게가 다 닫힐 시간)

    [Serializable] public class Pose { public string name; public float[] deg = new float[6]; }
    [Serializable] public class Group { public string name; public List<Pose> poses = new List<Pose>(); }
    [Serializable] class PoseFile
    {
        public List<Group> groups = new List<Group>();
        public List<Pose> poses;       // 예전 형식(그룹 없이 자세만) — 읽을 때 "그룹 1"로 옮기고 비움
    }

    public int GroupCount => data.groups.Count;
    public int GroupIndex { get; private set; }
    public Group Current => data.groups[GroupIndex];
    public IReadOnlyList<Pose> Poses => Current.poses;
    public int Version { get; private set; }            // 목록이 바뀔 때마다 +1 (화면이 다시 그릴지 판단)
    public string Status { get; private set; } = "";
    public bool CanGo => arm && arm.Ready && arm.CurrentMode != UnityToFollowerGUI.Mode.Leader && !arm.EStopped;

    // 재생 상태: PlayIndex = 지금 가는 자세 번호 (-1 = 재생 안 함)
    public int PlayIndex { get; private set; } = -1;
    public bool Playing => PlayIndex >= 0;
    float holdUntil = -1f;              // 도착 후 머무는 중이면 끝나는 시각

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
            if (data.groups == null) data.groups = new List<Group>();
            if (data.poses != null && data.poses.Count > 0) data.groups.Add(new Group { name = "그룹 1", poses = data.poses });
            data.poses = null;
            Status = $"그룹 {data.groups.Count}개 읽음";
        }
        catch (Exception e) { data = new PoseFile(); Status = "파일 읽기 실패: " + e.Message; }
        if (data.groups.Count == 0) data.groups.Add(new Group { name = "그룹 1" });
        GroupIndex = 0;
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
        var p = new Pose { name = $"자세 {Current.poses.Count + 1}" };
        for (int i = 0; i < 6; i++)
        {
            float d = real && i < 5 ? arm.FollowerDeg(i) : arm.UnityDeg(i);
            p.deg[i] = Mathf.Round(d * 10f) / 10f;   // 0.1° 단위
        }
        Current.poses.Add(p);
        Changed($"'{p.name}' 저장 ({(real ? "흰 팔 실제 각도 + 집게 명령" : "유니티 각도")})");
    }

    public void Go(int i)
    {
        if (!CanGo || Playing || i < 0 || i >= Current.poses.Count) return;
        arm.MoveToPose(Current.poses[i].deg);
        Status = $"'{Current.poses[i].name}'(으)로 이동";
    }

    // ---------- 그룹 ----------
    public void NextGroup(int d)
    {
        if (Playing) return;
        GroupIndex = (GroupIndex + d + data.groups.Count) % data.groups.Count;
        Status = $"그룹: {Current.name}";
        Version++;
    }

    public void AddGroup()
    {
        if (Playing) return;
        data.groups.Add(new Group { name = $"그룹 {data.groups.Count + 1}" });
        GroupIndex = data.groups.Count - 1;
        Changed($"새 그룹 '{Current.name}' — 이름을 눌러 바꾸세요");
    }

    public void RenameGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == Current.name) return;
        Current.name = name.Trim();
        Changed($"그룹 이름: {Current.name}");
    }

    // 실수로 지우지 않게: 자세가 하나도 없는 그룹만 지움 (자세를 먼저 다 지워야 함)
    public void DeleteGroupIfEmpty()
    {
        if (Playing) return;
        if (Current.poses.Count > 0) { Status = "자세를 먼저 다 지워야 그룹을 지울 수 있어요"; Version++; return; }
        if (data.groups.Count == 1) { Status = "그룹이 하나뿐이라 못 지워요"; Version++; return; }
        string n = Current.name;
        data.groups.RemoveAt(GroupIndex);
        GroupIndex = Mathf.Clamp(GroupIndex, 0, data.groups.Count - 1);
        Changed($"그룹 '{n}' 삭제");
    }

    // ---------- 재생 ----------
    public void PlayAll()
    {
        if (!CanGo || Current.poses.Count == 0 || Playing) return;
        PlayStep(0);
    }

    public void StopPlay(string why = "정지")
    {
        if (!Playing) return;
        arm.HoldHere();
        Status = $"재생 멈춤 ({why}) — {PlayIndex + 1}번 가는 중이었음";
        PlayIndex = -1;
        holdUntil = -1f;
    }

    void PlayStep(int i)
    {
        PlayIndex = i;
        holdUntil = -1f;
        arm.MoveToPose(Current.poses[i].deg);
        Status = $"재생 {i + 1}/{Current.poses.Count} — '{Current.poses[i].name}'(으)로";
    }

    void Update()
    {
        if (!Playing) return;
        if (!CanGo) { StopPlay(arm.EStopped ? "비상 정지" : "조종 모드 끝남"); return; }
        if (arm.MovingPose) return;                                   // 아직 가는 중
        if (holdUntil < 0f) { holdUntil = Time.time + holdSec; return; }   // 막 도착 → 잠깐 머묾
        if (Time.time < holdUntil) return;
        if (PlayIndex + 1 < Current.poses.Count) PlayStep(PlayIndex + 1);
        else { Status = $"재생 끝 — {Current.poses.Count}개 자세"; PlayIndex = -1; }
    }

    public void Rename(int i, string name)
    {
        if (i < 0 || i >= Current.poses.Count || string.IsNullOrWhiteSpace(name) || name == Current.poses[i].name) return;
        Current.poses[i].name = name.Trim();
        Changed($"이름 바꿈: {Current.poses[i].name}");
    }

    public void Swap(int i, int d)
    {
        int j = i + d;
        if (i < 0 || j < 0 || i >= Current.poses.Count || j >= Current.poses.Count) return;
        (Current.poses[i], Current.poses[j]) = (Current.poses[j], Current.poses[i]);
        Changed("순서 바꿈");
    }

    public void Delete(int i)
    {
        if (Playing || i < 0 || i >= Current.poses.Count) return;
        string n = Current.poses[i].name;
        Current.poses.RemoveAt(i);
        Changed($"'{n}' 삭제");
    }
}

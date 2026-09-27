using UnityEngine;

/// <summary>
/// 돌발 이벤트 스폰 NPC에 붙는 경범죄 표식 — ArrestJudge가 이것을 보고 경범죄로 판정해 보상을 지급한다.
/// 탈옥 후 재개할 소란 행동도 기록한다. 서버 전용 plain MonoBehaviour다.
/// </summary>
public class MisdemeanorOffender : MonoBehaviour
{
    public int Reward { get; set; }

    public ERiotBehavior RiotBehavior { get; private set; }

    public float RiotSeconds { get; private set; }

    public bool HasRiotBehavior { get; private set; }

    /// <summary>소란 행동 기록 — 스폰한 이벤트(<see cref="SpawnedNpcEventBase"/>)가 스폰 직후 1회 호출한다.</summary>
    public void SetRiotBehavior(ERiotBehavior behavior, float seconds)
    {
        RiotBehavior = behavior;
        RiotSeconds = seconds;
        HasRiotBehavior = true;
    }
}

public enum ERiotBehavior
{
    Resist,

    Flee,

    Sprint,
}

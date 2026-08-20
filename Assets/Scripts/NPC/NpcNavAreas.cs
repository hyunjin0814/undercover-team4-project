using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 통행이 도로를 어떻게 다루는지 한 곳에 모은다 (#634 → #634 후속).
///
/// <b>도로는 상태에 따라 갈린다.</b> 평소(배회)에는 아예 못 가는 곳이고, 쫓고 쫓기는 동안에는
/// 그냥 비싼 곳이다. 이 둘을 가르는 것이 <see cref="AllowsRoad"/>이고, 실제 전환은
/// <c>NpcController</c>가 상태 전이 때 <see cref="NavMeshAgent.areaMask"/>에 건다.
///
/// <b>왜 비용 조정으로는 안 되는가.</b> 도로를 빼면 도시가 서로 못 닿는 블록 8개로 갈린다 —
/// 세로 도로가 맵을 세로로, 가로 도로 3개가 가로로 관통하고 맵 가장자리에 우회로가 없다
/// (실측: 블록 간 <c>CalculatePath</c>가 전부 <c>PathPartial</c>). 즉 <b>블록을 잇는 경로는
/// 도로뿐</b>이라, Road 비용을 아무리 올려도 그게 유일한 경로라 그대로 건넌다. 통행을 실제로
/// 막는 방법은 마스크에서 빼는 것 하나뿐이다.
///
/// 그 대신 도로를 못 밟게 하는 상태는 최소로 둔다 (<see cref="AllowsRoad"/> 주석 참고) —
/// 맵을 가로질러야 하는 상태에서 도로를 끊으면 목적지에 영영 닿지 못하고, 무엇보다
/// <b>플레이어가 도로 위에 서면 아무도 쫓아오지 못하는</b> 안전지대가 생긴다.
/// </summary>
public static class NpcNavAreas
{
    /// <summary>영역 이름 — Navigation 설정 Areas 탭의 문자열과 같아야 한다.</summary>
    public const string k_roadAreaName = "Road";

    /// <summary>유치장 셀 바닥 영역 (#415 → #744에서 복귀) — 시민 프리팹의 통행 마스크에서 빠져 있다.</summary>
    public const string k_jailAreaName = "Jail";

    /// <summary>본부 실내 영역 (#722) — 시민 프리팹의 통행 마스크에서 빠져 있다.</summary>
    public const string k_hqAreaName = "HQ";

    private static int s_roadMask = -1; // -1 = 아직 조회 전
    private static int s_jailMask = -1;
    private static int s_hqMask = -1;

    /// <summary>도로 영역 비트마스크. 프로젝트 설정에 그 영역이 없으면 0이라 아래가 전부 무동작이 된다.</summary>
    public static int RoadMask => ResolveMask(k_roadAreaName, ref s_roadMask);

    /// <summary>셀 바닥 영역 비트마스크 — 수감 중에만 열어 준다 (<c>NpcController.SetGrantedAreas</c>).</summary>
    public static int JailMask => ResolveMask(k_jailAreaName, ref s_jailMask);

    /// <summary>본부 실내 영역 비트마스크 — 셀에서 나와 도시로 걸어 나가는 동안만 열어 준다.</summary>
    public static int HqMask => ResolveMask(k_hqAreaName, ref s_hqMask);

    // 이름 → 비트마스크 1회 조회. 없는 영역은 0이라 부르는 쪽이 자연히 무동작이 된다.
    private static int ResolveMask(string areaName, ref int cache)
    {
        if (cache < 0)
        {
            int area = NavMesh.GetAreaFromName(areaName);
            cache = area >= 0 ? 1 << area : 0;
        }
        return cache;
    }

    /// <summary>
    /// 도로를 뺀 통행 마스크 — <b>목적지·스폰 지점을 고를 때만</b> 쓴다.
    /// 에이전트 자신의 <see cref="NavMeshAgent.areaMask"/>는 건드리지 않는다: 그걸 줄이면 경로가
    /// 도로를 건너지 못해 위 클래스 주석의 분단이 그대로 재현된다.
    /// </summary>
    public static int ExcludeRoad(int areaMask)
    {
        int masked = areaMask & ~RoadMask;

        // 통행 가능한 곳이 도로뿐인 구성(테스트 씬 등) — 빈 마스크로 샘플하면 아무 데도 못 뽑아
        // NPC가 그 자리에 굳는다. 그럴 땐 도로라도 쓰게 원래 마스크를 돌려준다.
        return masked != 0 ? masked : areaMask;
    }

    /// <summary>
    /// 이 상태에서 도로를 <b>통행해도 되는가</b> — 아니면 에이전트 마스크에서 Road를 뺀다.
    ///
    /// <b>제외 목록으로 쓰는 이유(화이트리스트가 아니라).</b> 여기 빠뜨린 상태는 "도로를 써도 되는
    /// 쪽"으로 떨어진다 — 새 상태가 추가됐을 때 최악이 <b>도로를 밟는 것</b>이지 <b>목적지에 닿지
    /// 못해 굳는 것</b>이 아니게 하려는 방향이다. 반대로 두면 새 상태가 조용히 블록에 갇힌다.
    ///
    /// 그래서 막는 것은 <b>평소 시민 생활</b>뿐이다. 나머지는 전부 맵을 가로지르는 목적이 있다 —
    /// 추격·도주는 물론이고 침입(유치장 자물쇠)·반출 보행·원한 구역 수용·호송·연행이 그렇다.
    /// </summary>
    public static bool AllowsRoad(NpcState state)
    {
        return state != NpcState.Idle && state != NpcState.Walk;
    }

    /// <summary>
    /// 이 지점에서 <paramref name="clearance"/>(m) 안에 도로가 있는가 — 스폰 자리를 고를 때 쓴다 (#660).
    ///
    /// <b><see cref="IsOnRoad"/>와 묻는 것이 다르다.</b> 저쪽은 "발밑이 도로인가"라 마스크를 좁혀도
    /// 되는지를 가르고, 이쪽은 "도로에서 충분히 떨어졌는가"라 <b>연석에 발을 걸친 자리</b>를 걸러낸다.
    /// 그래서 여기서는 맞은 폴리곤이 아니라 도로 마스크로 <b>직접</b> 샘플하는 쪽이 맞다 —
    /// 반경 안에 도로가 있기만 해도 참이어야 하기 때문이다.
    /// </summary>
    public static bool HasRoadWithin(Vector3 position, float clearance)
    {
        if (RoadMask == 0 || clearance <= 0f)
            return false;

        return NavMesh.SamplePosition(position, out NavMeshHit _, clearance, RoadMask);
    }

    // "지금 이 영역 위인가" 판정 반경(m) — 발밑을 묻는 것이라 좁게 잡는다.
    private const float k_onAreaProbeRadius = 0.5f;

    /// <summary>
    /// 발밑 폴리곤의 영역 비트마스크 — NavMesh 밖이면 0. (#744)
    ///
    /// <b>영역은 마스크가 아니라 맞은 폴리곤에서 읽는다.</b> 특정 영역 마스크로 직접 샘플하면
    /// 반경 안에 그 영역이 있기만 해도 걸려서, 인도에 선 NPC가 도로 위로 잘못 판정된다.
    /// (<see cref="HasRoadWithin"/>은 일부러 그 반대를 묻는 자다)
    /// </summary>
    public static int AreaMaskAt(Vector3 position)
    {
        return NavMesh.SamplePosition(position, out NavMeshHit hit, k_onAreaProbeRadius, NavMesh.AllAreas)
            ? hit.mask
            : 0;
    }

    /// <summary>
    /// 지금 도로 위에 서 있는가 — <b>마스크를 좁혀도 되는지</b>를 가른다.
    ///
    /// 도로 위에서 Road를 빼면 서 있는 폴리곤 자체가 마스크 밖이 되어 경로 계산이 통째로 실패한다
    /// (실측: <c>CalculatePath</c> → <c>PathInvalid</c>, 반환값도 false). 그 자리가 하필 차도
    /// 한복판이라, 좁히는 쪽은 반드시 이걸 먼저 물어야 한다.
    ///
    /// 같은 사정이 Jail·HQ 통행 회수에도 그대로 있어(#744) 판정을 <see cref="AreaMaskAt"/>로 모았다.
    /// </summary>
    public static bool IsOnRoad(Vector3 position)
    {
        if (RoadMask == 0)
            return false;

        return (AreaMaskAt(position) & RoadMask) != 0;
    }
}

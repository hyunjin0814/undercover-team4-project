using UnityEngine;

/// <summary>
/// "이 좌표가 감옥 방 안인가"를 답하는 정적 판정 유틸 — 옛 <c>JailArea</c>의 자리를 잇는다. (#415/#492/#537)
///
/// <b>기준이 NavMesh 영역에서 방 부피로 바뀌었다.</b> 예전에는 유치장 내부 폴리곤이 Jail 영역이라
/// 마스크로 판정했는데, 감옥이 도시에서 떨어진 별도 NavMesh 섬이 되면서(#537) 영역 게이팅 자체가
/// 없어졌다. 이제는 <see cref="JailZone"/>에 배선한 방 부피가 유일한 기준이다.
///
/// <b>여기 있는 것은 "좌표 하나로 물어볼 창구"뿐이다</b> — 감옥 자체는 <see cref="App.Game.Jail"/>이
/// 들고 있고(#592), 이 클래스는 그 위에 좌표 판정만 얹는다.
///
/// 감옥이 없는 프로젝트(단독 테스트 씬 등)에서는 항상 false다 — 호출부가 각자 폴백을 정한다.
/// </summary>
public static class JailRoom
{
    /// <summary>이 좌표가 감옥 방 안인가 — 감옥이 없거나 방 범위가 미배선이면 항상 false.</summary>
    public static bool Contains(Vector3 position)
    {
        return Zone != null && Zone.ContainsPoint(position);
    }

    /// <summary>
    /// 방 안에서 걸어갈 수 있는 임의의 지점을 고른다 — 실패하면 false. 서버(또는 오프라인) 전용. (#537)
    ///
    /// 수감자 배회(<see cref="NpcJailedState"/>)가 쓴다. 방 부피에서 아무 점이나 뽑고
    /// <paramref name="areaMask"/>로 NavMesh에 스냅하므로, 벽 안이나 가구 위가 나와도 걸어갈 수 있는
    /// 자리로 당겨진다. 감옥이 없는 테스트 씬에서는 그냥 false다(그 자리에 서 있는다).
    /// </summary>
    public static bool TryRandomPoint(int areaMask, out Vector3 point)
    {
        point = Vector3.zero;
        if (Zone == null)
            return false;

        Vector3 candidate = Zone.RandomPointInRoom();
        if (!UnityEngine.AI.NavMesh.SamplePosition(candidate, out UnityEngine.AI.NavMeshHit hit, k_snapRadius, areaMask))
            return false;

        point = hit.position;
        return true;
    }

    // 뽑은 점을 NavMesh로 당길 최대 거리(m) — 방 한 칸(2.5m)보다 조금 크게 잡아 벽 안쪽이 나와도 건진다.
    private const float k_snapRadius = 3f;

    // 씬에 감옥이 없으면 null — 씬 전환으로 참조가 죽는 문제는 App 등록/해제가 대신 처리한다 (#592)
    private static JailZone Zone => App.Game.Jail;
}

using UnityEngine;

/// <summary>
/// 이 리그가 구동하는 <b>스킨드 메시 한 벌</b> — 컬링 바운즈만 다룬다.
///
/// <see cref="RagdollRig"/>에서 갈라냈다. 저쪽의 뼈 배열·바인드 포즈·월드 캡처와 <b>필드를 하나도
/// 공유하지 않고</b> 리그 최상단(<c>BoneRoot</c>)만 읽으므로, 같은 클래스에 있을 이유가 없었다.
///
/// 물리도 네트워크도 모른다 — 언제 켜고 끌지는 리그를 소유한 쪽이 정한다.
/// 설계 근거는 <c>docs/ragdoll-rig.md</c> §3(수집 범위)·§7(컬링).
/// </summary>
public sealed class RagdollSkins
{
    /// <summary>리그를 못 찾아 아직 수집하지 못한 상태 — 모든 호출이 무동작이다.</summary>
    public static readonly RagdollSkins Empty = new RagdollSkins(new SkinnedMeshRenderer[0]);

    private readonly SkinnedMeshRenderer[] m_skins;
    private readonly bool[] m_updateWhenOffscreen; // 프리팹 평시 값 — 되돌릴 자리

    private RagdollSkins(SkinnedMeshRenderer[] skins)
    {
        m_skins = skins;
        m_updateWhenOffscreen = new bool[skins.Length];
        for (int i = 0; i < skins.Length; i++)
            m_updateWhenOffscreen[i] = skins[i].updateWhenOffscreen;
    }

    /// <summary>
    /// <paramref name="owner"/> 아래에서 <paramref name="boneRoot"/>가 구동하는 메시만 모은다 —
    /// 계층·이름으로는 못 가른다(docs §3).
    /// </summary>
    public static RagdollSkins Collect(Transform owner, Transform boneRoot)
    {
        if (owner == null || boneRoot == null)
            return Empty;

        SkinnedMeshRenderer[] all = owner.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (DrivenBy(all[i], boneRoot))
                count++;
        }

        var mine = new SkinnedMeshRenderer[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (DrivenBy(all[i], boneRoot))
                mine[next++] = all[i];
        }

        return new RagdollSkins(mine);
    }

    // rootBone이 몸통 리그 안에 있는 메시인가 — 이 리그가 구동하는가의 판정.
    private static bool DrivenBy(SkinnedMeshRenderer skin, Transform boneRoot) =>
        skin.rootBone != null
        && (skin.rootBone == boneRoot || skin.rootBone.IsChildOf(boneRoot));

    /// <summary>
    /// 래그돌 동안 컬링 바운즈를 매 프레임 재계산시킨다 — 안 하면 날아간 시체가 통째로 컬링돼
    /// 화면에서 사라진다. 비용이 있어 래그돌이 켜진 동안만 올린다 (docs §7).
    /// </summary>
    public void SetAlwaysVisible(bool always)
    {
        for (int i = 0; i < m_skins.Length; i++)
            m_skins[i].updateWhenOffscreen = always || m_updateWhenOffscreen[i];
    }
}

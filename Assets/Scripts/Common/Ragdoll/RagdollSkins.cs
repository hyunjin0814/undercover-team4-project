using UnityEngine;

/// <summary>
/// 리그가 구동하는 스킨드 메시들의 컬링 바운즈를 다룬다.
/// </summary>
public sealed class RagdollSkins
{
    public static readonly RagdollSkins Empty = new RagdollSkins(new SkinnedMeshRenderer[0]);

    private readonly SkinnedMeshRenderer[] m_skins;
    private readonly bool[] m_updateWhenOffscreen;

    private RagdollSkins(SkinnedMeshRenderer[] skins)
    {
        m_skins = skins;
        m_updateWhenOffscreen = new bool[skins.Length];
        for (int i = 0; i < skins.Length; i++)
            m_updateWhenOffscreen[i] = skins[i].updateWhenOffscreen;
    }

    /// <summary>owner 아래에서 boneRoot가 구동하는 스킨드 메시만 모은다.</summary>
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

    private static bool DrivenBy(SkinnedMeshRenderer skin, Transform boneRoot) =>
        skin.rootBone != null
        && (skin.rootBone == boneRoot || skin.rootBone.IsChildOf(boneRoot));

    /// <summary>래그돌 동안 컬링 바운즈를 매 프레임 재계산할지 설정한다.</summary>
    public void SetAlwaysVisible(bool always)
    {
        for (int i = 0; i < m_skins.Length; i++)
            m_skins[i].updateWhenOffscreen = always || m_updateWhenOffscreen[i];
    }
}

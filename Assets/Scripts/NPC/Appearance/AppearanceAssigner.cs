using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 범인 확정 후 전 NPC에 외형 조합을 배정한다 — 각 몽타주에 부합하는 NPC가 정확히 k명이 되도록 디코이를 배치한다(GDD 6-5).
/// 배정은 서버 권위이며, 공개된 수배의 몽타주만 OnMontageGenerated로 발행한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class AppearanceAssigner : CommonManagerBase
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    [Header("외형 축별 옵션 정의")]
    [SerializeField] private AppearanceDatabase m_appearanceDatabase;

    [Header("몽타주 공개 특징 수")]
    [Tooltip("범인 1명당 몽타주로 공개할 외형 축 수 (기획 2~3개). 공개 축은 범인마다 따로 뽑는다 — 이 값은 전 범인이 나눠 갖는 총량이 아니라 각자의 개수다")]
    [Range(1, AppearanceProfile.k_axisCount)]
    [SerializeField] private int m_revealedAxisCount = 2;

    private const int k_nonMatchingAttempts = 16;

    [Header("바디 성별")]
    [Tooltip("수염이 붙은 NPC가 여성 바디를 받을 확률. Generic 바디 13개 중 8개가 여성이라, 수염 값이 흔하면 도시가 남성으로 쏠린다 — 이 확률만큼은 여성에게도 수염을 남긴다. 0이면 수염=여성이 절대 안 겹친다")]
    [Range(0f, 1f)]
    [SerializeField] private float m_femaleFacialHairChance = 0.03f;

    [Header("몽타주 부합 인원 k (범인 포함)")]
    [Tooltip("몽타주 1건당 공개 특징에 부합하는 NPC 수 — 범인 1명 + 디코이 k−1명. 클수록 스캔 검증 부담이 커진다 (난이도)")]
    [SerializeField] private int m_montageMatchCount = 3;

    private readonly List<AppearanceProfile> m_criminalProfiles = new List<AppearanceProfile>();
    private readonly List<RevealedAxisSet> m_criminalRevealedAxes = new List<RevealedAxisSet>();

    public IReadOnlyList<RevealedAxisSet> CriminalRevealedAxes => m_criminalRevealedAxes;

    public IReadOnlyList<AppearanceProfile> CriminalProfiles => m_criminalProfiles;

    public AppearanceDatabase Database => m_appearanceDatabase;

    public event Action<NpcController, AppearanceProfile, RevealedAxisSet> OnMontageGenerated;

    private void Start()
    {
        if (Assigner == null)
        {
            Debug.LogWarning("AppearanceAssigner: CriminalAssigner를 찾지 못해 외형 배정 불가", this);
            return;
        }

        Assigner.OnCriminalAssigned += AssignAll;

        if (Assigner.CriminalNpcs.Count > 0 && m_criminalProfiles.Count == 0)
            AssignAll(Assigner.CriminalNpcs);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        if (Assigner != null)
            Assigner.OnCriminalAssigned -= AssignAll;
    }

    private void AssignAll(IReadOnlyList<NpcController> criminals)
    {
        if (m_appearanceDatabase == null)
        {
            Debug.LogWarning("[AppearanceAssigner] AppearanceDatabase가 지정되지 않아 외형 배정 불가", this);
            return;
        }
        if (Spawner == null)
        {
            Debug.LogWarning("[AppearanceAssigner] NpcSpawner를 찾지 못해 외형 배정 불가", this);
            return;
        }
        IReadOnlyList<NpcController> npcs = Spawner.SpawnedNpcs;
        if (npcs.Count == 0 || criminals.Count == 0) return;

        AppearanceModelCatalog catalog = FindCatalog(npcs);

        m_criminalProfiles.Clear();
        foreach (NpcController c in criminals)
        {
            NpcCatalogAppearance cat = c.GetComponent<NpcCatalogAppearance>();
            if (cat != null && catalog != null)
            {
                int modelIndex = cat.ModelIndex;
                if (!catalog.CanDepict(modelIndex))
                {
                    modelIndex = PickDepictableModel(catalog, modelIndex);
                    cat.SetModelIndex(modelIndex);
                }
                m_criminalProfiles.Add(catalog.GetProfile(modelIndex));
            }
            else
                m_criminalProfiles.Add(m_appearanceDatabase.CreateRandomProfile());
        }
        PickRevealedAxes();

        Dictionary<NpcController, int> decoyOwners = PickDecoys(npcs, criminals);
        var criminalIndexOf = new Dictionary<NpcController, int>();
        for (int i = 0; i < criminals.Count; i++) criminalIndexOf[criminals[i]] = i;

        var logBuilder = new StringBuilder();
        foreach (var npc in npcs)
        {
            AppearanceProfile applied;
            string role;
            if (criminalIndexOf.TryGetValue(npc, out int ci))
            {
                applied = RealizeCriminal(npc, ci, catalog);
                role = $"  ← 범인 #{ci + 1}";
            }
            else if (decoyOwners.TryGetValue(npc, out int oi))
            {
                applied = RealizeDecoy(npc, oi, catalog);
                role = $"  ← 디코이 (범인 #{oi + 1})";
            }
            else
            {
                applied = RealizeNonMatching(npc, catalog);
                role = string.Empty;
            }
            logBuilder.AppendLine($"  {DescribeProfile(applied)}{role}");
        }

        int revealedCount = 0;
        var montageLog = new StringBuilder();
        for (int i = 0; i < criminals.Count; i++)
        {
            AppearanceProfile p = m_criminalProfiles[i];

            if (montageLog.Length > 0)
                montageLog.Append(" / ");
            montageLog.Append('"').Append(m_appearanceDatabase.BuildMontageText(p, m_criminalRevealedAxes[i])).Append('"');

            CitizenIdentity identity = criminals[i].GetComponent<CitizenIdentity>();
            if (identity == null || !identity.IsCriminal)
                continue;

            revealedCount++;
            OnMontageGenerated?.Invoke(criminals[i], p, m_criminalRevealedAxes[i]);
        }
        Debug.Log($"외형 배정 완료 ({npcs.Count}명, 용의자 {criminals.Count}명 중 공개 {revealedCount}명) | 몽타주: {montageLog}\n{logBuilder}");
    }

    /// <summary>대기 중이던 용의자의 몽타주를 지금 발행한다(제보 전화 승격). 서버(또는 오프라인) 전용.</summary>
    public void RevealMontage(NpcController npc)
    {
        if (npc == null)
            return;

        IReadOnlyList<NpcController> criminals = Assigner != null ? Assigner.CriminalNpcs : null;
        if (criminals == null)
        {
            Debug.LogWarning("AppearanceAssigner: CriminalAssigner를 찾지 못해 몽타주를 공개할 수 없다", this);
            return;
        }

        for (int i = 0; i < criminals.Count; i++)
        {
            if (criminals[i] != npc)
                continue;

            if (i >= m_criminalProfiles.Count || i >= m_criminalRevealedAxes.Count)
            {
                Debug.LogWarning($"AppearanceAssigner: {npc.name}의 외형이 아직 배정되지 않아 몽타주를 공개할 수 없다", npc);
                return;
            }

            OnMontageGenerated?.Invoke(npc, m_criminalProfiles[i], m_criminalRevealedAxes[i]);
            return;
        }

        Debug.LogWarning($"AppearanceAssigner: {npc.name}은(는) 용의자 목록에 없어 공개 대상이 아니다", npc);
    }

    /// <summary>범인: SciFi는 1단계에서 확정한 모델을 그대로 두고, Generic은 확정 프로필 배정. 신원엔 실제 프로필 반영.</summary>
    private AppearanceProfile RealizeCriminal(NpcController npc, int criminalIndex, AppearanceModelCatalog catalog)
    {
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
        {
            AppearanceProfile p = m_criminalProfiles[criminalIndex];
            AssignIdentity(npc, p);
            return p;
        }
        return RealizeGeneric(npc, m_criminalProfiles[criminalIndex]);
    }

    /// <summary>디코이: 담당 범인과 그 범인의 공개 축에서 일치. SciFi는 일치 모델 선택, Generic은 프로필 배정.</summary>
    private AppearanceProfile RealizeDecoy(NpcController npc, int ownerIndex, AppearanceModelCatalog catalog)
    {
        AppearanceProfile owner = m_criminalProfiles[ownerIndex];
        RevealedAxisSet ownerAxes = m_criminalRevealedAxes[ownerIndex];
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
        {
            int idx = FindModelMatchingRevealed(catalog, owner, ownerAxes);
            if (idx >= 0)
            {
                cat.SetModelIndex(idx);
                AppearanceProfile p = catalog.GetProfile(idx);
                AssignIdentity(npc, p);
                return p;
            }
            Debug.LogWarning($"[AppearanceAsigner] SciFi 디코이 {npc.name}에 공개축 일치 모델이 없어 비부합 처리 - 몽타주 부합 인원 부족 가능", npc);
            return RealizeSciFiNonMatching(npc, cat, catalog);
        }
        return RealizeGeneric(npc, CreateDecoyProfile(owner, ownerAxes));
    }

    /// <summary>비부합: 전 범인과 공개 축이 최소 1개 다르게.</summary>
    private AppearanceProfile RealizeNonMatching(NpcController npc, AppearanceModelCatalog catalog)
    {
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
            return RealizeSciFiNonMatching(npc, cat, catalog);
        return RealizeGeneric(npc, CreateNonMatchingProfile());
    }

    private AppearanceProfile RealizeSciFiNonMatching(NpcController npc, NpcCatalogAppearance cat, AppearanceModelCatalog catalog)
    {
        int idx = PickNonMatchingModel(catalog);
        cat.SetModelIndex(idx);
        AppearanceProfile p = catalog.GetProfile(idx);
        AssignIdentity(npc, p);
        return p;
    }

    /// <summary>Generic: 프롭 배정 + 신원. NpcAppearance.SetProfile이 인덱스만 전 클라에 동기화.</summary>
    private AppearanceProfile RealizeGeneric(NpcController npc, AppearanceProfile profile)
    {
        NpcAppearance app = npc.GetComponent<NpcAppearance>();
        if (app != null)
        {
            bool hasFacialHair = profile.FacialHairIndex > 0;
            app.ServerPickBody(!hasFacialHair || Random.value < m_femaleFacialHairChance);
            app.SetProfile(profile);
        }
        else Debug.LogWarning($"AppearanceAssigner: {npc.name}에 외형 컴포넌트가 없어 시각 적용 생략", npc);
        AssignIdentity(npc, profile);
        return profile;
    }

    private void AssignIdentity(NpcController npc, AppearanceProfile profile)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity != null) identity.AssignAppearance(profile);
    }

    /// <summary>주어진 공개 축에서 criminalProfile과 모두 같은 모델 인덱스(셔플 후 첫). 없으면 -1.</summary>
    private int FindModelMatchingRevealed(AppearanceModelCatalog catalog, AppearanceProfile criminalProfile, RevealedAxisSet axes)
    {
        foreach (int m in ShuffledIndices(catalog.Count))
        {
            if (!catalog.CanDepict(m)) continue;

            AppearanceProfile p = catalog.GetProfile(m);
            if (p.MatchesOn(criminalProfile, axes)) return m;
        }
        return -1;
    }

    /// <summary>그림 몽타주로 그릴 수 있는 모델 하나(셔플 후 첫). 하나도 없으면 fallback을 그대로 쓴다.</summary>
    private int PickDepictableModel(AppearanceModelCatalog catalog, int fallback)
    {
        foreach (int m in ShuffledIndices(catalog.Count))
        {
            if (catalog.CanDepict(m)) return m;
        }

        Debug.LogWarning("[AppearanceAssigner] 그림 몽타주로 그릴 수 있는 모델이 없다 — 카탈로그의 NonHumanoid 체크를 확인할 것", this);
        return fallback;
    }

    private static List<int> ShuffledIndices(int count)
    {
        var order = new List<int>(count);
        for (int i = 0; i < count; i++) order.Add(i);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    private AppearanceModelCatalog FindCatalog(IReadOnlyList<NpcController> npcs)
    {
        foreach (var n in npcs)
        {
            var cat = n.GetComponent<NpcCatalogAppearance>();
            if (cat != null && cat.Catalog != null) return cat.Catalog;
        }
        return null;
    }

    private int PickNonMatchingModel(AppearanceModelCatalog catalog)
    {
        foreach (int m in ShuffledIndices(catalog.Count))
        {
            AppearanceProfile p = catalog.GetProfile(m);
            if (!MatchesAnyCriminal(p)) return m;
        }
        Debug.LogWarning("[AppearanceAssigner] 전 범인과 비부합인 모델이 없음 — 공개 축 수나 모델 다양성 확인", this);
        return Random.Range(0, catalog.Count);
    }

    /// <summary>범인마다 공개 축을 따로 뽑는다 — <see cref="CriminalRevealedAxes"/> 참고.</summary>
    private void PickRevealedAxes()
    {
        m_criminalRevealedAxes.Clear();
        for (int i = 0; i < m_criminalProfiles.Count; i++)
            m_criminalRevealedAxes.Add(PickRevealedAxesFor(m_criminalProfiles[i], i));
    }

    /// <summary>이 범인이 공개할 수 있는 축을 셔플해 공개 수만큼 고른다.</summary>
    private RevealedAxisSet PickRevealedAxesFor(in AppearanceProfile profile, int criminalIndex)
    {
        bool hairVisible = m_appearanceDatabase.HasVisibleHair(profile);

        var order = new List<AppearanceAxis>(AppearanceProfile.k_axisCount);
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            if (axis == AppearanceAxis.HairColor && !hairVisible)
                continue;
            if (!m_appearanceDatabase.CanDepict(axis, profile.GetIndex(axis)))
                continue;
            order.Add(axis);
        }

        if (order.Count < m_revealedAxisCount)
            Debug.LogWarning(
                $"AppearanceAssigner: 범인 #{criminalIndex + 1}이 말할 수 있는 축이 {order.Count}개뿐이라 몽타주가 목표({m_revealedAxisCount}개)보다 적다 — 그릴 수 없는 값의 레이어(AppearanceDatabase.MontageLayer)를 채울 것",
                this
            );

        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        var revealed = new RevealedAxisSet();
        int count = Mathf.Min(m_revealedAxisCount, order.Count);
        for (int i = 0; i < count; i++)
            revealed.Add(order[i]);
        return revealed;
    }

    /// <summary>범인 외 NPC를 셔플해 범인마다 k−1명씩 디코이로 배분한다. 디코이 → 담당 범인 인덱스를 돌려준다.</summary>
    private Dictionary<NpcController, int> PickDecoys(IReadOnlyList<NpcController> npcs, IReadOnlyList<NpcController> criminals)
    {
        var criminalSet = new HashSet<NpcController>(criminals);
        var candidates = new List<NpcController>(npcs.Count);
        foreach (NpcController npc in npcs)
        {
            if (!criminalSet.Contains(npc))
                candidates.Add(npc);
        }

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int decoyPerCriminal = Mathf.Max(m_montageMatchCount - 1, 0);
        var decoyOwners = new Dictionary<NpcController, int>();
        int next = 0;
        for (int criminalIndex = 0; criminalIndex < criminals.Count; criminalIndex++)
        {
            for (int i = 0; i < decoyPerCriminal && next < candidates.Count; i++)
                decoyOwners[candidates[next++]] = criminalIndex;
        }

        if (next >= candidates.Count && decoyPerCriminal * criminals.Count > candidates.Count)
            Debug.LogWarning(
                $"AppearanceAssigner: 디코이 후보({candidates.Count}명)가 필요 수({decoyPerCriminal * criminals.Count}명)보다 적어 일부 몽타주의 부합 인원이 목표 k에 못 미친다",
                this
            );
        return decoyOwners;
    }

    /// <summary>담당 범인의 공개 축은 그대로 베끼고, 나머지 축은 랜덤인 디코이 프로필을 만든다.</summary>
    private AppearanceProfile CreateDecoyProfile(in AppearanceProfile criminalProfile, RevealedAxisSet ownerAxes)
    {
        AppearanceProfile profile = m_appearanceDatabase.CreateRandomProfile();
        foreach (AppearanceAxis axis in ownerAxes)
            profile.SetIndex(axis, criminalProfile.GetIndex(axis));
        return profile;
    }

    /// <summary>모든 범인의 몽타주에 부합하지 않는 프로필을 만든다.</summary>
    private AppearanceProfile CreateNonMatchingProfile()
    {
        AppearanceProfile profile = default;
        for (int attempt = 0; attempt < k_nonMatchingAttempts; attempt++)
        {
            profile = m_appearanceDatabase.CreateRandomProfile();
            if (!MatchesAnyCriminal(profile))
                return profile;

            for (int i = 0; i < m_criminalProfiles.Count; i++)
            {
                if (profile.MatchesOn(m_criminalProfiles[i], m_criminalRevealedAxes[i]))
                    BreakMatch(ref profile, i);
            }

            if (!MatchesAnyCriminal(profile))
                return profile;
        }

        Debug.LogWarning("AppearanceAssigner: 공개 축 옵션이 범인 외형 값들로 가득 차 비부합 프로필을 만들 수 없다 — AppearanceDatabase 옵션 수나 진범 수를 조정할 것", this);
        return profile;
    }

    /// <summary>이 범인의 공개 축 하나를 그 범인과 다른 값으로 바꾼다 — 부합은 공개 축 전부 일치일 때만 성립한다.</summary>
    private void BreakMatch(ref AppearanceProfile profile, int criminalIndex)
    {
        AppearanceProfile criminal = m_criminalProfiles[criminalIndex];
        foreach (AppearanceAxis axis in m_criminalRevealedAxes[criminalIndex])
        {
            List<int> selectable = m_appearanceDatabase.GetGenericSelectableIndices(axis);
            selectable.Remove(criminal.GetIndex(axis));
            if (selectable.Count == 0)
                continue;

            profile.SetIndex(axis, selectable[Random.Range(0, selectable.Count)]);
            return;
        }
    }

    /// <summary>어느 한 범인이라도 그 범인의 공개 축이 전부 일치하는지 — 몽타주 부합 여부.</summary>
    private bool MatchesAnyCriminal(in AppearanceProfile profile)
    {
        for (int i = 0; i < m_criminalProfiles.Count; i++)
        {
            if (profile.MatchesOn(m_criminalProfiles[i], m_criminalRevealedAxes[i]))
                return true;
        }
        return false;
    }

    private string DescribeProfile(in AppearanceProfile profile)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(axis, profile.GetIndex(axis));
            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(AppearanceDatabase.GetOptionName(option));
        }
        return builder.ToString();
    }
}

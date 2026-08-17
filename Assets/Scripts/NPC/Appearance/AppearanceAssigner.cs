using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 범인 확정 후 전 NPC에 외형 특징 조합을 배정한다. (#74, #669)
/// 정답은 개체가 아니라 <b>조건</b>이다 — 범인별 외형 확정 → 범인별 공개 축(수배 조건) 선택 →
/// 나머지 NPC는 전부 자유 랜덤 배정(우연 부합을 막지 않는다) → 조건별 부합 인원을 세어 목표
/// k(하한)에 못 미치면 모자란 만큼만 비부합 NPC를 조건에 맞게 덮어쓴다. k값으로 난이도를 조절한다
/// (GDD 6-5 분포=난이도) — 다만 이제는 상한이 아니라 하한이라, 값이 흔할수록 부합 인원이 더 늘 수 있다.
/// 진범이 여러 명이면(#127) 몽타주도 범인 수만큼 생성된다. 다만 발행(OnMontageGenerated)은
/// 이미 공개된 수배만 — 대기 중인 예비 용의자(#102)는 RevealMontage로 나중에 발행된다.
/// 배정은 서버 권위 — NpcAppearance.SetProfile이 인덱스만 전 클라이언트에 동기화한다 (#56).
/// 몽타주(GDD 10-3 글 방식)의 원본은 범인 프로필 + 공개 축이고, 문장 조립은 표시하는 쪽
/// (본부 수배 UI #58)이 자기 언어로 한다 — 여기서 문장을 만들어 보관하지 않는다 (#497).
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class AppearanceAssigner : CommonManagerBase
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    [Header("외형 축별 옵션 정의")]
    [SerializeField]
    private AppearanceDatabase m_appearanceDatabase;

    [Header("몽타주 공개 특징 수")]
    [Tooltip(
        "범인 외형 축 중 몽타주로 공개할 축 수 (기획 2~3개). 진범이 여러 명이어도 공개 축은 공통이다"
    )]
    [Range(1, AppearanceProfile.k_axisCount)]
    [SerializeField]
    private int m_revealedAxisCount = 2;

    [Header("바디 성별")]
    [Tooltip(
        "수염이 붙은 NPC가 여성 바디를 받을 확률. Generic 바디 13개 중 8개가 여성이라, 수염 값이 흔하면 도시가 남성으로 쏠린다 — 이 확률만큼은 여성에게도 수염을 남긴다. 0이면 수염=여성이 절대 안 겹친다"
    )]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_femaleFacialHairChance = 0.03f;

    [Header("몽타주 부합 인원 k (하한, 범인 포함)")]
    [Tooltip(
        "몽타주 1건당 조건에 부합하는 NPC 수의 최소값 — 우연 부합이 이미 이 수를 채우면 보정하지 않는다 (#669). 클수록 스캔 검증 부담이 커진다 (난이도)"
    )]
    [SerializeField]
    private int m_montageMatchCount = 3;

    private readonly List<AppearanceProfile> m_criminalProfiles = new List<AppearanceProfile>();
    private readonly List<RevealedAxisSet> m_criminalRevealedAxes = new List<RevealedAxisSet>();

    /// <summary>
    /// 범인별 몽타주 공개 축 — <see cref="CriminalProfiles"/>와 같은 순서. 배정 전에는 비어 있다.
    /// 공개 축은 <b>범인마다 따로</b> 뽑는다. 한 몽타주가 말할 수 있는 축은 그 범인의 외형에 달렸는데
    /// (대머리면 머리색을 말할 수 없고 #556, 그림 없는 값은 포트레이트로 그릴 수 없다 #607),
    /// 전 범인 공통으로 두면 그 제약이 전부 합쳐져 아무도 안 걸린 축까지 함께 사라진다 —
    /// 범인이 늘수록 몽타주가 조용히 빈약해진다.
    /// </summary>
    public IReadOnlyList<RevealedAxisSet> CriminalRevealedAxes => m_criminalRevealedAxes;

    /// <summary>범인별 외형 특징 조합 — CriminalAssigner.CriminalNpcs와 같은 순서. 배정 전에는 비어 있다. (#127)</summary>
    public IReadOnlyList<AppearanceProfile> CriminalProfiles => m_criminalProfiles;

    /// <summary>
    /// 외형 축별 옵션 정의 — 인덱스를 표시 이름으로 옮길 때 쓴다. 읽기 전용.
    /// 수배 UI(#58)가 몽타주 문장을 조립하는 출처이기도 하다 — 클라이언트에도 씬에 배선돼 있다 (#497).
    /// </summary>
    public AppearanceDatabase Database => m_appearanceDatabase;

    /// <summary>
    /// 몽타주 공개 이벤트 — 수배 UI(#58)가 구독한다. 범인마다 한 번씩 발행된다. (#127)
    /// 문장이 아니라 프로필을 넘긴다 — 표시하는 피어가 자기 언어로 조립하기 때문이다 (#497).
    /// 공개 축을 함께 넘기는 것은 그것이 범인마다 다르기 때문이다 — 받는 쪽이 따로 조회하면
    /// 어느 범인의 축인지 짝을 다시 맞춰야 한다.
    /// </summary>
    public event Action<NpcController, AppearanceProfile, RevealedAxisSet> OnMontageGenerated;

    private void Start()
    {
        if (Assigner == null)
        {
            Debug.LogWarning(
                "AppearanceAssigner: CriminalAssigner를 찾지 못해 외형 배정 불가",
                this
            );
            return;
        }

        // 범인 확정 이벤트 구독 — 매니저 간 구독은 모든 매니저의 Awake(App 등록)가 끝난 Start에서 한다.
        // 실제 배정은 스폰 완료(수 프레임 뒤) 이후에 발화하므로 Start 구독으로 놓치지 않는다.
        Assigner.OnCriminalAssigned += AssignAll;

        // 구독 전에 배정이 이미 끝난 경우 보정
        if (Assigner.CriminalNpcs.Count > 0 && m_criminalProfiles.Count == 0)
            AssignAll(Assigner.CriminalNpcs);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // App 등록 해제

        if (Assigner != null)
            Assigner.OnCriminalAssigned -= AssignAll;
    }

    private void AssignAll(IReadOnlyList<NpcController> criminals)
    {
        if (m_appearanceDatabase == null)
        {
            Debug.LogWarning(
                "[AppearanceAssigner] AppearanceDatabase가 지정되지 않아 외형 배정 불가",
                this
            );
            return;
        }
        if (Spawner == null)
        {
            Debug.LogWarning("[AppearanceAssigner] NpcSpawner를 찾지 못해 외형 배정 불가", this);
            return;
        }
        IReadOnlyList<NpcController> npcs = Spawner.SpawnedNpcs;
        if (npcs.Count == 0 || criminals.Count == 0)
            return;

        AppearanceModelCatalog catalog = FindCatalog(npcs);

        // 1. 범인 배정
        m_criminalProfiles.Clear();
        foreach (NpcController c in criminals)
        {
            NpcCatalogAppearance cat = c.GetComponent<NpcCatalogAppearance>();
            if (cat != null && catalog != null)
            {
                // 그림 몽타주가 가진 두상은 인간형 하나뿐이다 — 그 두상으로 안 읽히는 모델이 범인으로
                // 걸리면 여기서 갈아끼운다. 프로필을 담기 전이라야 아래 실현 단계와 어긋나지 않는다.
                int modelIndex = cat.ModelIndex;
                if (!catalog.CanDepict(modelIndex))
                {
                    modelIndex = PickDepictableModel(catalog, modelIndex);
                    cat.SetModelIndex(modelIndex);
                }
                m_criminalProfiles.Add(catalog.GetProfile(modelIndex)); // Sci-fi
            }
            else
                m_criminalProfiles.Add(m_appearanceDatabase.CreateRandomProfile()); // Generic
        }
        PickRevealedAxes();

        var criminalSet = new HashSet<NpcController>(criminals);
        var applied = new Dictionary<NpcController, AppearanceProfile>();

        // 2. 비범인 전원을 자유 배정 — 우연 부합을 막지 않는다 (#669). 정답이 조건이 된 이상
        //    조건에 우연히 맞는 시민도 정답이어야 하기 때문이다. NonHumanoid만 예외로 막는다
        //    (PickFreeSciFiModel, §7-1) — 인간 두상으로 안 읽히는 모델이 우연히 맞으면 그림이 화면과 어긋난다.
        var nonCriminalNpcs = new List<NpcController>(npcs.Count);
        foreach (NpcController npc in npcs)
        {
            if (criminalSet.Contains(npc))
                continue;
            nonCriminalNpcs.Add(npc);

            NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
            if (cat != null && catalog != null)
            {
                int idx = PickFreeSciFiModel(catalog);
                cat.SetModelIndex(idx);
                AppearanceProfile p = catalog.GetProfile(idx);
                AssignIdentity(npc, p);
                applied[npc] = p;
            }
            else
                applied[npc] = RealizeGeneric(npc, m_appearanceDatabase.CreateRandomProfile());
        }

        // 3. 범인 실현
        for (int i = 0; i < criminals.Count; i++)
            applied[criminals[i]] = RealizeCriminal(criminals[i], i, catalog);

        // 4. 하한 보정 — 조건별 부합 인원이 목표 k에 못 미치면 비부합 NPC를 덮어써 채운다 (#669)
        string tuningLog = CorrectMatchCounts(criminals, catalog, nonCriminalNpcs, applied);

        var logBuilder = new StringBuilder();
        foreach (NpcController npc in npcs)
            logBuilder.AppendLine($"  {DescribeProfile(applied[npc])}");

        // 5. 몽타주 공개 — 이미 공개된 수배만 발행한다. 예비 용의자(#102)는 IsCriminal = false로
        //    대기하다가 승격 시 RevealMontage가 m_criminalProfiles의 같은 프로필을 발행한다.
        //    조건 축은 범인마다 라운드 내내 고정이라 그 시점에 다시 뽑지 않는다 — 다시 뽑으면
        //    본부에 이미 떠 있던 몽타주가 무효가 된다. 문장을 보관하지 않는 이유도 같다:
        //    보관할 원본은 프로필이고, 문장은 표시하는 쪽이 자기 언어로 조립한다 (#497).
        int revealedCount = 0;
        var montageLog = new StringBuilder();
        for (int i = 0; i < criminals.Count; i++)
        {
            AppearanceProfile p = m_criminalProfiles[i];

            if (montageLog.Length > 0)
                montageLog.Append(" / ");
            montageLog
                .Append('"')
                .Append(m_appearanceDatabase.BuildMontageText(p, m_criminalRevealedAxes[i]))
                .Append('"');

            CitizenIdentity identity = criminals[i].GetComponent<CitizenIdentity>();
            if (identity == null || !identity.IsCriminal)
                continue;

            revealedCount++;
            OnMontageGenerated?.Invoke(criminals[i], p, m_criminalRevealedAxes[i]);
        }
        Debug.Log(
            $"외형 배정 완료 ({npcs.Count}명, 용의자 {criminals.Count}명 중 공개 {revealedCount}명) | 몽타주: {montageLog}\n조건별 부합: {tuningLog}\n{logBuilder}"
        );
    }

    /// <summary>
    /// 대기 중이던 용의자의 몽타주를 지금 발행한다 — 제보 전화 승격(#102) 전용. 서버(또는 오프라인) 전용.
    /// 라운드 시작에 확정해 둔 프로필을 그대로 쓰므로 공개 축은 바뀌지 않는다.
    /// 발행 자체는 라운드 시작 때와 같은 경로(OnMontageGenerated)라, WantedListManager의
    /// NetworkList 추가와 TotalWanted++ 가 그대로 따라온다 — 별도 등록 경로를 만들지 않는 이유다.
    /// </summary>
    public void RevealMontage(NpcController npc)
    {
        if (npc == null)
            return;

        IReadOnlyList<NpcController> criminals = Assigner != null ? Assigner.CriminalNpcs : null;
        if (criminals == null)
        {
            Debug.LogWarning(
                "AppearanceAssigner: CriminalAssigner를 찾지 못해 몽타주를 공개할 수 없다",
                this
            );
            return;
        }

        // CriminalNpcs와 m_criminalProfiles는 같은 순서다 — 인덱스로 짝을 찾는다
        for (int i = 0; i < criminals.Count; i++)
        {
            if (criminals[i] != npc)
                continue;

            if (i >= m_criminalProfiles.Count || i >= m_criminalRevealedAxes.Count)
            {
                Debug.LogWarning(
                    $"AppearanceAssigner: {npc.name}의 외형이 아직 배정되지 않아 몽타주를 공개할 수 없다",
                    npc
                );
                return;
            }

            OnMontageGenerated?.Invoke(npc, m_criminalProfiles[i], m_criminalRevealedAxes[i]);
            return;
        }

        Debug.LogWarning(
            $"AppearanceAssigner: {npc.name}은(는) 용의자 목록에 없어 공개 대상이 아니다",
            npc
        );
    }

    /// <summary>범인: SciFi는 1단계에서 확정한 모델을 그대로 두고, Generic은 확정 프로필 배정. 신원엔 실제 프로필 반영.</summary>
    private AppearanceProfile RealizeCriminal(
        NpcController npc,
        int criminalIndex,
        AppearanceModelCatalog catalog
    )
    {
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
        {
            // 1단계에서 담아 둔 것을 그대로 쓴다 — 모델을 갈아끼운 경우 여기서 다시 읽으면 갈라진다
            AppearanceProfile p = m_criminalProfiles[criminalIndex];
            AssignIdentity(npc, p);
            return p;
        }
        return RealizeGeneric(npc, m_criminalProfiles[criminalIndex]);
    }

    /// <summary>Generic: 프롭 배정 + 신원. NpcAppearance.SetProfile이 인덱스만 전 클라에 동기화.</summary>
    private AppearanceProfile RealizeGeneric(NpcController npc, AppearanceProfile profile)
    {
        NpcAppearance app = npc.GetComponent<NpcAppearance>();
        if (app != null)
        {
            // 바디를 프로필에 맞춘다 — 수염이 붙었으면 남성 바디에서 뽑는다 (#619).
            // 반대로 바디를 보고 수염을 지우면 프로필이 바뀌어, 디코이가 범인의 공개 축을 복사해 두는
            // 몽타주 부합 보장이 깨진다. 프로필은 그대로 두고 바디를 맞추는 쪽은 그 보장을 안 건드린다.
            bool hasFacialHair = profile.FacialHairIndex > 0;
            app.ServerPickBody(!hasFacialHair || Random.value < m_femaleFacialHairChance);
            app.SetProfile(profile);
        }
        else
            Debug.LogWarning(
                $"AppearanceAssigner: {npc.name}에 외형 컴포넌트가 없어 시각 적용 생략",
                npc
            );
        AssignIdentity(npc, profile);
        return profile;
    }

    private void AssignIdentity(NpcController npc, AppearanceProfile profile)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity != null)
            identity.AssignAppearance(profile);
    }

    /// <summary>주어진 공개 축에서 criminalProfile과 모두 같은 모델 인덱스(셔플 후 첫). 없으면 -1.</summary>
    private int FindModelMatchingRevealed(
        AppearanceModelCatalog catalog,
        AppearanceProfile criminalProfile,
        RevealedAxisSet axes
    )
    {
        foreach (int m in ShuffledIndices(catalog.Count))
        {
            // 그림 몽타주로 안 그려지는 모델은 디코이가 못 된다 — 현장에서 후보로 안 보여 k가 헛돈다
            if (!catalog.CanDepict(m))
                continue;

            AppearanceProfile p = catalog.GetProfile(m);
            if (p.MatchesOn(criminalProfile, axes))
                return m;
        }
        return -1;
    }

    /// <summary>그림 몽타주로 그릴 수 있는 모델 하나(셔플 후 첫). 하나도 없으면 fallback을 그대로 쓴다.</summary>
    private int PickDepictableModel(AppearanceModelCatalog catalog, int fallback)
    {
        foreach (int m in ShuffledIndices(catalog.Count))
        {
            if (catalog.CanDepict(m))
                return m;
        }

        Debug.LogWarning(
            "[AppearanceAssigner] 그림 몽타주로 그릴 수 있는 모델이 없다 — 카탈로그의 NonHumanoid 체크를 확인할 것",
            this
        );
        return fallback;
    }

    private static List<int> ShuffledIndices(int count)
    {
        var order = new List<int>(count);
        for (int i = 0; i < count; i++)
            order.Add(i);
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
            if (cat != null && cat.Catalog != null)
                return cat.Catalog;
        }
        return null;
    }

    // 자유 배정 SciFi 모델 — 휴머노이드는 우연 부합을 막지 않는다(#669, 조건 기반 판정의 핵심).
    // NonHumanoid만 예외로 막는다(§7-1) — 인간 두상으로 안 읽히는 모델이 조건에 우연히 맞으면 그림이 화면과 어긋난다.
    private int PickFreeSciFiModel(AppearanceModelCatalog catalog)
    {
        foreach (int m in ShuffledIndices(catalog.Count))
        {
            if (catalog.CanDepict(m) || !MatchesAnyCriminal(catalog.GetProfile(m)))
                return m;
        }
        Debug.LogWarning(
            "[AppearanceAssigner] 조건과 우연히 부합하지 않는 NonHumanoid 모델이 없음 — 모델 다양성 확인",
            this
        );
        return Random.Range(0, catalog.Count);
    }

    /// <summary>범인마다 공개 축을 따로 뽑는다 — <see cref="CriminalRevealedAxes"/> 참고.</summary>
    private void PickRevealedAxes()
    {
        m_criminalRevealedAxes.Clear();
        for (int i = 0; i < m_criminalProfiles.Count; i++)
            m_criminalRevealedAxes.Add(PickRevealedAxesFor(m_criminalProfiles[i], i));
    }

    /// <summary>
    /// 이 범인이 말할 수 있는 축을 셔플해 앞에서 공개 수만큼 고른다. 담기는 순서는 뜻이 없다 — 집합이다.
    /// 판정은 <b>이 범인의 값만</b> 본다 — 다른 범인이 헬멧을 썼다고 이 범인의 모자 축까지 버릴 이유가 없다.
    ///
    /// 빠지는 축은 둘이다:
    /// - 머리색 — 이 범인의 머리가 화면에 안 보이면(대머리·가림) 뺀다 (#556). 머리 프롭이 없으면
    ///   머리색은 화면에 나타날 자리가 없어 몽타주에만 남고, 현장에서 눈으로 대조할 수 없는 특징이
    ///   무전에 실리면 대조가 성립하지 않은 채 오검거로 이어진다.
    /// - 포트레이트로 그릴 수 없는 값이 걸린 축 (#607, <see cref="AppearanceDatabase.CanDepict"/>).
    /// </summary>
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

    /// <summary>
    /// 조건별 부합 인원(범인 자신 포함)이 목표 k(하한)에 못 미치면, 어떤 조건과도 우연히 부합하지
    /// 않은 NPC를 그 수만큼 골라 조건에 맞게 덮어쓴다. (#669)
    ///
    /// 이미 부합 중인 NPC는 보정 후보에서 뺀다 — 다른 조건으로 강제로 돌리면 그 부합이 깨질 수 있다.
    /// 커서를 범인 간에 공유해 순서대로 배분한다 — 후보가 모자라면 뒤쪽 범인부터 못 채운다.
    /// </summary>
    /// <returns>조건별 "축 수 / 우연 부합 → 보정 후" 요약 — 완료 기준 검증용 로그.</returns>
    private string CorrectMatchCounts(
        IReadOnlyList<NpcController> criminals,
        AppearanceModelCatalog catalog,
        List<NpcController> nonCriminalNpcs,
        Dictionary<NpcController, AppearanceProfile> applied
    )
    {
        var rawCounts = new int[criminals.Count];

        // 범인끼리의 우연 부합도 센다 — 범인 B의 외형이 조건 A를 만족하면 B도 A의 정답이다.
        // 자기 자신은 항상 자기 조건에 부합하므로 이 이중 루프가 대각선에서 1을 채운다.
        for (int i = 0; i < criminals.Count; i++)
        {
            for (int ci = 0; ci < criminals.Count; ci++)
            {
                if (
                    m_criminalProfiles[i]
                        .MatchesOn(m_criminalProfiles[ci], m_criminalRevealedAxes[ci])
                )
                    rawCounts[ci]++;
            }
        }

        var freePool = new List<NpcController>();
        foreach (NpcController npc in nonCriminalNpcs)
        {
            AppearanceProfile p = applied[npc];
            bool matchedAny = false;
            for (int i = 0; i < criminals.Count; i++)
            {
                if (!p.MatchesOn(m_criminalProfiles[i], m_criminalRevealedAxes[i]))
                    continue;
                rawCounts[i]++;
                matchedAny = true;
            }
            if (!matchedAny)
                freePool.Add(npc);
        }

        for (int i = freePool.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (freePool[i], freePool[j]) = (freePool[j], freePool[i]);
        }

        var summary = new StringBuilder();
        for (int ci = 0; ci < criminals.Count; ci++)
        {
            int need = m_montageMatchCount - rawCounts[ci];
            int filled = 0;

            // SciFi 후보가 이 조건을 만족할 수 있는지는 <b>후보와 무관하게</b> 카탈로그가 정한다 —
            // 한 번 -1이면 어느 후보를 넣어도 -1이다. 루프 안에서 판정하면 실패한 후보를 계속 소모해
            // 뒤쪽 조건들이 보정 기회를 통째로 잃는다. 그래서 여기서 한 번만 묻는다.
            int sciFiModel =
                catalog != null
                    ? FindModelMatchingRevealed(
                        catalog,
                        m_criminalProfiles[ci],
                        m_criminalRevealedAxes[ci]
                    )
                    : -1;

            // 실제로 덮어쓴 후보만 풀에서 뺀다 — 이 조건으로 못 바꾸는 후보(모델 없는 SciFi)는
            // 남겨 둬야 다음 조건이 쓸 수 있다. 그래서 조건 간 공유 커서 대신 소모식으로 훑는다.
            int scan = 0;
            while (filled < need && scan < freePool.Count)
            {
                NpcController candidate = freePool[scan];

                if (sciFiModel < 0 && candidate.GetComponent<NpcCatalogAppearance>() != null)
                {
                    scan++; // 이 조건으로는 못 쓴다 — 건너뛰고 남겨 둔다
                    continue;
                }

                AppearanceProfile corrected = ForceMatch(candidate, catalog, ci, sciFiModel);
                applied[candidate] = corrected;
                freePool.RemoveAt(scan); // 덮어썼으니 더는 '어느 조건과도 무관'이 아니다
                if (corrected.MatchesOn(m_criminalProfiles[ci], m_criminalRevealedAxes[ci]))
                    filled++;
            }

            if (filled < need)
                Debug.Log(
                    $"[AppearanceAssigner] 조건 #{ci + 1} 부합 인원이 하한({m_montageMatchCount})에 {need - filled}명 못 미친다 — "
                        + (
                            sciFiModel < 0
                                ? "이 조건을 만족하는 SciFi 모델이 카탈로그에 없다 (튜닝 정보)"
                                : "자유 배정 후보 부족 (튜닝 정보)"
                        )
                );

            if (summary.Length > 0)
                summary.Append(" / ");
            summary.Append(
                $"조건#{ci + 1} 축{m_criminalRevealedAxes[ci].Count}개 우연{rawCounts[ci]}명→보정후{rawCounts[ci] + filled}명"
            );
        }

        return summary.ToString();
    }

    /// <summary>담당 조건의 축은 그대로 베끼고, 나머지 축은 랜덤인 프로필을 만든다.</summary>
    private AppearanceProfile CreateDecoyProfile(
        in AppearanceProfile criminalProfile,
        RevealedAxisSet ownerAxes
    )
    {
        AppearanceProfile profile = m_appearanceDatabase.CreateRandomProfile();
        foreach (AppearanceAxis axis in ownerAxes)
            profile.SetIndex(axis, criminalProfile.GetIndex(axis));
        return profile;
    }

    /// <summary>이 NPC를 지정한 조건에 맞게 강제로 덮어쓴다. (#669)</summary>
    /// <param name="sciFiModel">이 조건을 만족하는 카탈로그 모델 인덱스 — 부르는 쪽이
    /// <see cref="FindModelMatchingRevealed"/>로 조건당 한 번만 구해 넘긴다(후보와 무관한 값이라
    /// 후보마다 다시 물으면 실패한 후보를 헛되이 소모한다). -1이면 SciFi 후보는 손대지 않는다.</param>
    private AppearanceProfile ForceMatch(
        NpcController npc,
        AppearanceModelCatalog catalog,
        int criminalIndex,
        int sciFiModel
    )
    {
        AppearanceProfile criminal = m_criminalProfiles[criminalIndex];
        RevealedAxisSet axes = m_criminalRevealedAxes[criminalIndex];
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
        {
            if (sciFiModel < 0)
            {
                CitizenIdentity current = npc.GetComponent<CitizenIdentity>();
                return current != null ? current.Appearance : AppearanceProfile.Unassigned;
            }
            cat.SetModelIndex(sciFiModel);
            AppearanceProfile p = catalog.GetProfile(sciFiModel);
            AssignIdentity(npc, p);
            return p;
        }
        return RealizeGeneric(npc, CreateDecoyProfile(criminal, axes));
    }

    /// <summary>어느 한 범인이라도 그 범인의 조건 축이 전부 일치하는지 — 몽타주 부합 여부.</summary>
    private bool MatchesAnyCriminal(in AppearanceProfile profile)
    {
        for (int i = 0; i < m_criminalProfiles.Count; i++)
        {
            if (profile.MatchesOn(m_criminalProfiles[i], m_criminalRevealedAxes[i]))
                return true;
        }
        return false;
    }

    // 배정 결과 로그용 — 전 축의 표시 이름을 나열한다
    private string DescribeProfile(in AppearanceProfile profile)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
                axis,
                profile.GetIndex(axis)
            );
            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(AppearanceDatabase.GetOptionName(option));
        }
        return builder.ToString();
    }
}

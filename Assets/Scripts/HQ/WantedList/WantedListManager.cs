using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 수배 리스트(#58) — 라운드 검거 대상을 서버 권위로 채우고, 검거되면 해당 항목을 지운다.
/// 리스트는 NetworkList로 전 클라이언트에 동기화되며, 본부 UI는 이 컴포넌트의 <see cref="Wanted"/>를
/// 구독해 표시한다. 진범이 여러 명이면(#127) OnMontageGenerated가 범인마다 발행되어 항목도 그만큼 쌓인다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class WantedListManager : NetworkedManagerBase
{
    private AppearanceAssigner Appearance => App.Game.Appearance;
    private ArrestJudge Judge => App.Game.ArrestJudge;

    // 서버만 쓰기, 전 클라이언트 읽기. UI(#58)는 Wanted.OnListChanged로 갱신을 받는다.
    private readonly NetworkList<WantedEntry> m_wanted = new NetworkList<WantedEntry>();

    // 검거로 리스트에서 내린 항목 보관함 — 범인 탈출(#231) 시 몽타주를 그대로 되살리기 위해 남긴다.
    // 몽타주를 재생성하면 본부가 기억하던 인상착의와 달라져 "아까 그 놈"이 성립하지 않는다.
    // 서버에서만 쓰므로 동기화하지 않는다(재등재도 서버 권위).
    private readonly Dictionary<ulong, WantedEntry> m_arrestedEntries =
        new Dictionary<ulong, WantedEntry>();

    // 동기화된 수배 리스트 — 본부 UI(#58)가 구독·열람한다. 서버 외에는 읽기 전용으로 취급.
    public NetworkList<WantedEntry> Wanted => m_wanted;

    // 이번 라운드에 등록된 진범 총수 — 검거/탈출로 남은 수가 줄고 늘어도 바뀌지 않는다(신규 몽타주 등록 시에만 +1).
    // HUD "남은/전체" 표시(#331)를 위해 서버 권위로 전 클라이언트에 동기화한다.
    private readonly NetworkVariable<int> m_totalWanted = new NetworkVariable<int>();
    public int TotalWanted => m_totalWanted.Value;
    public event Action OnTotalWantedChanged;

    /// <summary>
    /// 이 피어에서 리스트가 스폰·초기 동기화된 시점 — 뷰가 최초 표시를 위해 구독한다.
    /// NetworkList는 뒤늦게 접속한 클라이언트에 초기 내용을 OnListChanged로 알리지 않으므로,
    /// 이 이벤트로 "지금 상태 그대로 한 번 그려라"를 알려 late-join 빈 화면을 막는다.
    /// </summary>
    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        // 전체 수(#331) 변경을 전 피어에서 구독 — 클라 HUD 재갱신용
        m_totalWanted.OnValueChanged += HandleTotalWantedChanged;

        // 서버만 리스트를 채우고 지운다 — 배정·판정이 서버 권위이므로 (#56 패턴)
        if (IsServer)
        {
            m_wanted.Clear();
            m_totalWanted.Value = 0; // 재시작 시 이전 세션 총수도 함께 초기화 (#209와 동일 취지)
            m_arrestedEntries.Clear(); // 탈출 재등재용 보관함도 함께 — 이전 세션 항목이 새 라운드 NetworkObjectId와 겹치면 엉뚱한 몽타주가 되살아난다 (#231)

            // 등록은 외형·몽타주까지 확정된 시점(OnMontageGenerated)에 한다.
            // OnCriminalAssigned 시점엔 외형이 아직 배정 전이라 몽타주가 비어 있다 (AppearanceAssigner).
            if (Appearance != null)
                Appearance.OnMontageGenerated += HandleMontageGenerated;
            else
                Debug.LogWarning(
                    "WantedListManager: AppearanceAssigner를 찾지 못해 수배 항목을 등록할 수 없다",
                    this
                );

            if (Judge != null)
            {
                Judge.OnArrestJudged += HandleArrestJudged;
                // 시체 인계도 같은 처리다 (#616) — 문 앞 판정으로 현상금이 계상되므로 수배 항목도
                // 함께 내려가야 남은 수가 맞는다. 이 구독자는 리스트 항목만 지우고 NPC 상태를
                // 건드리지 않아 OnCorpseJudged의 제약(상태를 바꾸지 말 것)에 걸리지 않는다.
                Judge.OnCorpseJudged += HandleArrestJudged;
            }
            else
                Debug.LogWarning(
                    "WantedListManager: ArrestJudge를 찾지 못해 검거 시 항목을 지울 수 없다",
                    this
                );
        }

        // 모든 피어(호스트·클라이언트) 공통: 이 시점엔 NetworkList가 초기 동기화된 상태다.
        // 뒤늦게 접속한 클라이언트도 여기서 현재 수배 내용을 처음 한 번 그리게 된다.
        OnListReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_totalWanted.OnValueChanged -= HandleTotalWantedChanged;

        if (Appearance != null)
            Appearance.OnMontageGenerated -= HandleMontageGenerated;

        if (Judge != null)
        {
            Judge.OnArrestJudged -= HandleArrestJudged;
            Judge.OnCorpseJudged -= HandleArrestJudged;
        }
    }

    // 몽타주 공개 = 범인·외형 모두 확정된 시점. 수배 항목을 리스트에 추가한다. (서버 전용)
    // 진범이 여러 명이면(#127) 범인마다 한 번씩 호출되어 항목이 그만큼 추가된다.
    // 항목의 NpcId는 이 범인(조건의 기준 개체)을 가리킨다 — 조건 부합 판정(#669)이 열린 항목을 찾을 때
    // 쓰는 키이자, 이 개체가 탈옥했을 때 재등재(ReinstateByNpcId)로 되찾는 키다.
    private void HandleMontageGenerated(
        NpcController criminal,
        AppearanceProfile appearance,
        RevealedAxisSet revealedAxes
    )
    {
        if (criminal == null)
        {
            Debug.LogWarning(
                "WantedListManager: 범인 NPC가 없어 수배 항목을 등록하지 못했다",
                this
            );
            return;
        }

        CitizenIdentity identity = criminal.GetComponent<CitizenIdentity>();

        // 몽타주는 문장이 아니라 원본(수배 조건 축 + 그 축의 값)으로 싣는다 — 표시하는 피어가 자기 언어로
        // 조립하므로 호스트·클라 언어가 갈려도 각자 언어로 보인다 (#497). 조건 아닌 축은 Masked로 지운다.
        // 조건 축은 범인마다 다르므로 이벤트가 실어 준 것을 그대로 쓴다 — 여기서 다시 조회하지 않는다.
        AppearanceAssigner assigner = Appearance;

        m_wanted.Add(
            new WantedEntry
            {
                NpcId = criminal.NetworkObjectId,
                Appearance = appearance.Masked(revealedAxes),
                RevealedAxes = revealedAxes,
                // 현상금은 서버 전용 값이라 항목에 실어야 본부에서 볼 수 있다 (#395)
                Bounty = identity != null ? identity.Bounty : 0,
            }
        );
        // 전체 진범 수 누적(#331) — 검거/탈출로는 줄지 않는다.
        // ⚠ 라운드 도중 수배 리스트에 진범을 새로 추가하는 다른 경로(#102 제보 전화 '승격' 등)가 생기면,
        //   그 경로에서도 반드시 m_totalWanted를 함께 증가시켜야 HUD 전체 진범 수가 어긋나지 않는다.
        m_totalWanted.Value++;
        // 로그용 문장은 여기서(서버 언어로) 한 번 만든다 — 콘솔은 개발자용이라 번역 대상이 아니다
        AppearanceDatabase database = assigner != null ? assigner.Database : null;
        string montageText =
            database != null ? database.BuildMontageText(appearance, revealedAxes) : "?";
        Debug.Log(
            $"[수배] 등록: \"{montageText}\" / 현상금 {(identity != null ? identity.Bounty : 0)}원 (현재 {m_wanted.Count}건)"
        );
    }

    /// <summary>
    /// 열려 있는 수배 중 이 외형이 부합하는 첫 항목을 찾는다 — 조건 부합 판정(#669)의 유일한 창구.
    /// 서버 전용. <see cref="ArrestJudge.TryResolveVerdict"/>가 인계 NPC의 실제 외형으로 조회한다.
    ///
    /// 부합 판정은 <see cref="AppearanceProfile.MatchesOn"/>을 그대로 쓴다 — <see cref="RevealedAxisSet"/>은
    /// 뜻만 '공개 축'에서 '수배 조건 축'으로 갈아끼웠을 뿐 직렬화 형태가 안 바뀌었다.
    /// 항목이 둘 이상 부합해도(조건이 겹치는 라운드) 앞에서부터 첫 항목만 닫는다 — 나머지는 다음 검거를 기다린다.
    /// </summary>
    public bool TryMatchOpen(in AppearanceProfile appearance, out WantedEntry entry)
    {
        for (int i = 0; i < m_wanted.Count; i++)
        {
            WantedEntry candidate = m_wanted[i];
            if (!appearance.MatchesOn(candidate.Appearance, candidate.RevealedAxes))
                continue;

            entry = candidate;
            return true;
        }

        entry = default;
        return false;
    }

    // 검거·시체 판정 수신 — 진범 판정일 때만 부합한 수배 항목을 지운다. (서버 전용)
    // 산 신병이든 시체든 유치장에 들어간 이상 더 찾을 대상이 아니다 (#616).
    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WantedCriminal || result.Npc == null)
            return;

        // 닫을 항목(MatchedWantedId)과 잡힌 개체(result.Npc)가 다를 수 있다 (#669) — 조건 부합이라
        // 수배판의 기준 개체가 아닌 다른 부합자를 잡아도 정답이기 때문이다. 보관은 반드시 잡힌 개체
        // 기준이어야 한다 — 그래야 이 개체가 탈옥했을 때 자기가 닫은 항목이 되살아난다.
        ServerCloseEntry(result.MatchedWantedId, result.Npc.NetworkObjectId);
    }

    // 전체 진범 수(#331) 변경을 얇은 C# 이벤트로 재발행 — HUD가 값에 관심만 있고 이전/이후 값은 불필요.
    private void HandleTotalWantedChanged(int previous, int current) =>
        OnTotalWantedChanged?.Invoke();

    // entryNpcId(조건의 기준 개체)로 항목을 찾아 지우고, arrestedNpcId(실제로 잡힌 개체) 기준으로 보관한다.
    private void ServerCloseEntry(ulong entryNpcId, ulong arrestedNpcId)
    {
        for (int i = 0; i < m_wanted.Count; i++)
        {
            if (m_wanted[i].NpcId != entryNpcId)
                continue;

            WantedEntry removed = m_wanted[i];
            m_wanted.RemoveAt(i);
            // 탈출(#231) 시 되살릴 수 있게 보관 — 지워버리면 몽타주를 복원할 방법이 없다
            m_arrestedEntries[arrestedNpcId] = removed;
            Debug.Log($"[수배] 검거 완료로 제거 (남은 {m_wanted.Count}건)");
            return;
        }
    }

    /// <summary>
    /// 검거로 내렸던 수배 항목을 되살린다 — 범인 탈출 이벤트(#231) 전용. 서버에서만 호출된다.
    /// 몽타주는 검거 시점에 보관해 둔 것을 그대로 쓴다 — 본부가 기억하던 인상착의와 일치해야
    /// "아까 그 놈"을 다시 찾는 재미가 성립한다.
    /// </summary>
    public void ReinstateByNpcId(ulong npcId)
    {
        // 등록·제거 구독이 OnNetworkSpawn(IsServer)에서만 걸리므로, 네트워크 세션 밖에서는
        // 수배 리스트 자체가 돌지 않는다 — 오프라인 단독 Play에서 되살릴 항목이 없는 것은 정상이라
        // 경고 없이 조용히 빠진다(아래 '보관 항목 없음' 경고는 진짜 이상 상황에만 뜨게 한다).
        if (!IsSpawned || !IsServer)
            return;

        if (!m_arrestedEntries.TryGetValue(npcId, out WantedEntry entry))
        {
            Debug.LogWarning(
                $"WantedListManager: 보관된 수배 항목이 없어 재등재하지 못했다 (NpcId {npcId})",
                this
            );
            return;
        }

        m_arrestedEntries.Remove(npcId);
        m_wanted.Add(entry);
        Debug.Log($"[수배] 탈출로 재등재 (NpcId {npcId}, 현재 {m_wanted.Count}건)");
    }
}

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 수배 리스트(#58) — 라운드 검거 조건을 서버 권위로 채우고, 충족되면 해당 항목을 지운다.
/// 진범이 여러 명이면(#127) OnMontageGenerated가 범인마다 발행되어 항목도 그만큼 쌓인다.
///
/// <b>저장소가 둘이다</b> (#669): 판정(<see cref="TryMatchOpen"/>)이 보는 원본은 세션과 무관한
/// <c>m_open</c>이고, <see cref="Wanted"/>(NetworkList)는 그것을 세션에서만 비추는 <b>표시용 사본</b>이다.
/// 정답이 조건 부합으로 바뀌면서 판정이 이 목록에 의존하게 됐는데, NetworkList는 세션 밖에서 채워지지
/// 않아 오프라인 Play가 통째로 오검거가 됐기 때문이다. 뷰는 <see cref="OpenCount"/>/<see cref="GetOpen"/>로
/// 읽고 <see cref="OnListReady"/>로 다시 그린다 — 양쪽 모드에서 같은 값이 나온다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class WantedListManager : NetworkedManagerBase
{
    private AppearanceAssigner Appearance => App.Game.Appearance;
    private ArrestJudge Judge => App.Game.ArrestJudge;

    // 서버만 쓰기, 전 클라이언트 읽기. UI(#58)는 Wanted.OnListChanged로 갱신을 받는다.
    // <b>표시용 사본이다</b> — 판정이 보는 원본은 아래 m_open이다 (#669).
    private readonly NetworkList<WantedEntry> m_wanted = new NetworkList<WantedEntry>();

    /// <summary>
    /// 열려 있는 수배 조건 — <b>판정(<see cref="TryMatchOpen"/>)이 보는 원본이고, 세션 유무와 무관하게 채워진다.</b> (#669)
    ///
    /// NetworkList를 원본으로 쓰지 않는 이유는 그것이 <see cref="OnNetworkSpawn"/> 안에서만 채워지기
    /// 때문이다. 정답이 개체(<c>CitizenIdentity.IsCriminal</c>)이던 시절에는 판정이 이 리스트를 안 봐서
    /// 문제가 없었지만, 조건 부합으로 바뀌면서 <b>오프라인 Play에서 리스트가 비어 전원 오검거</b>가 됐다.
    /// 이제 등록·제거는 <see cref="Start"/>에서 건 구독이 하고, NetworkList는 세션이 있을 때만 따라 움직인다.
    /// </summary>
    private readonly List<WantedEntry> m_open = new List<WantedEntry>();

    // 누적 등록 수 — m_totalWanted(NetworkVariable)의 세션 밖 짝. 검거·탈출로 줄지 않는다.
    private int m_totalRegistered;

    // 구독을 두 번 걸지 않기 위한 표식 — Start와 OnNetworkSpawn의 선후가 씬 로드 시점에 따라 갈린다.
    private bool m_subscribed;

    // 세션이 열려 있고 이 피어가 서버인가 — NetworkList·NetworkVariable에 쓸 수 있는 조건.
    // IsSpawned가 참이면 세션이 있으므로 IsServer 접근이 안전하다(단축 평가).
    private bool CanWriteNetworkState => IsSpawned && IsServer;

    // 검거로 리스트에서 내린 항목 보관함 — 범인 탈출(#231) 시 몽타주를 그대로 되살리기 위해 남긴다.
    // 몽타주를 재생성하면 본부가 기억하던 인상착의와 달라져 "아까 그 놈"이 성립하지 않는다.
    // 서버에서만 쓰므로 동기화하지 않는다(재등재도 서버 권위).
    private readonly Dictionary<ulong, WantedEntry> m_arrestedEntries =
        new Dictionary<ulong, WantedEntry>();

    // 동기화된 수배 리스트 — 본부 UI(#58)가 변경 알림(OnListChanged)을 구독하는 자리다.
    // <b>내용을 읽을 때는 아래 OpenCount/GetOpen을 쓸 것</b> — 세션 밖에서는 이 리스트가 비어 있다.
    public NetworkList<WantedEntry> Wanted => m_wanted;

    /// <summary>지금 열려 있는 수배 건수 — 세션 유무와 무관하게 맞는 값을 돌려준다. (#669)</summary>
    public int OpenCount => IsSpawned ? m_wanted.Count : m_open.Count;

    /// <summary>열려 있는 수배 항목 하나 — <see cref="OpenCount"/>와 짝이다.</summary>
    public WantedEntry GetOpen(int index) => IsSpawned ? m_wanted[index] : m_open[index];

    // 이번 라운드에 등록된 진범 총수 — 검거/탈출로 남은 수가 줄고 늘어도 바뀌지 않는다(신규 몽타주 등록 시에만 +1).
    // HUD "남은/전체" 표시(#331)를 위해 서버 권위로 전 클라이언트에 동기화한다.
    private readonly NetworkVariable<int> m_totalWanted = new NetworkVariable<int>();

    // 세션 밖에서는 NetworkVariable이 채워지지 않으므로 평범한 카운터를 읽는다 (#669).
    public int TotalWanted => IsSpawned ? m_totalWanted.Value : m_totalRegistered;
    public event Action OnTotalWantedChanged;

    /// <summary>
    /// <b>"지금 상태 그대로 다시 그려라"</b> — 뷰가 구독한다. 두 자리에서 발생한다:
    ///
    /// <list type="bullet">
    ///   <item>스폰·초기 동기화 직후 — NetworkList는 뒤늦게 접속한 클라이언트에 초기 내용을
    ///   <c>OnListChanged</c>로 알리지 않아, 이 신호가 없으면 late-join이 빈 화면을 본다.</item>
    ///   <item><b>세션 밖(오프라인 Play)에서 목록이 바뀔 때마다</b> (#669) — 그때는 NetworkList가
    ///   돌지 않아 <c>OnListChanged</c>가 영영 오지 않는다.</item>
    /// </list>
    /// </summary>
    public event Action OnListReady;

    /// <summary>
    /// 등록·제거 구독은 <b>세션 유무와 무관하게</b> 여기서 건다 (#669).
    ///
    /// 예전에는 <see cref="OnNetworkSpawn"/>의 <c>IsServer</c> 블록 안에서만 걸었다. 정답이 개체이던
    /// 시절에는 판정이 이 리스트를 읽지 않아 오프라인에서도 성립했지만, 조건 부합으로 바뀌면서
    /// 오프라인 Play에서 리스트가 빈 채로 남아 <b>전원 오검거</b>가 됐다.
    ///
    /// 클라이언트에서 걸어도 안전하다 — 배정(<c>OnMontageGenerated</c>)도 판정(<c>OnArrestJudged</c>)도
    /// 서버 권위라 클라에서는 발생하지 않는다. 매니저 간 구독을 Start에서 하는 것은 아키텍처 규칙 R6
    /// (모든 매니저의 Awake=App 등록이 끝난 뒤)이기도 하다.
    /// </summary>
    private void Start() => Subscribe();

    private void Subscribe()
    {
        if (m_subscribed)
            return;
        m_subscribed = true;

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

    public override void OnNetworkSpawn()
    {
        // 전체 수(#331) 변경을 전 피어에서 구독 — 클라 HUD 재갱신용
        m_totalWanted.OnValueChanged += HandleTotalWantedChanged;

        // 스폰이 Start보다 먼저 올 수 있다(씬 로드 시 이미 세션이 도는 경우) — 구독은 멱등이다.
        Subscribe();

        // 표시용 사본을 원본에 맞춘다 — 서버만 쓴다 (#56 패턴).
        // 스폰 전에 이미 조건이 등록됐을 수 있으므로 비우지 않고 원본을 그대로 옮긴다.
        if (IsServer)
        {
            m_wanted.Clear();
            foreach (WantedEntry entry in m_open)
                m_wanted.Add(entry);
            m_totalWanted.Value = m_totalRegistered;
        }

        // 모든 피어(호스트·클라이언트) 공통: 이 시점엔 NetworkList가 초기 동기화된 상태다.
        // 뒤늦게 접속한 클라이언트도 여기서 현재 수배 내용을 처음 한 번 그리게 된다.
        OnListReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        // 구독은 여기서 걷지 않는다 — 세션이 끝나도 오프라인 판정은 계속 돌아야 한다.
        // 실제 해제는 OnDestroy가 한다.
        m_totalWanted.OnValueChanged -= HandleTotalWantedChanged;
    }

    public override void OnDestroy()
    {
        if (m_subscribed)
        {
            if (Appearance != null)
                Appearance.OnMontageGenerated -= HandleMontageGenerated;

            if (Judge != null)
            {
                Judge.OnArrestJudged -= HandleArrestJudged;
                Judge.OnCorpseJudged -= HandleArrestJudged;
            }
            m_subscribed = false;
        }

        base.OnDestroy(); // App 등록 해제
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

        var entry = new WantedEntry
        {
            NpcId = criminal.NetworkObjectId,
            Appearance = appearance.Masked(revealedAxes),
            RevealedAxes = revealedAxes,
            // 현상금은 서버 전용 값이라 항목에 실어야 본부에서 볼 수 있다 (#395)
            Bounty = identity != null ? identity.Bounty : 0,
        };

        m_open.Add(entry); // 판정이 보는 원본
        // 전체 진범 수 누적(#331) — 검거/탈출로는 줄지 않는다.
        // ⚠ 라운드 도중 수배 리스트에 진범을 새로 추가하는 다른 경로(#102 제보 전화 '승격' 등)가 생기면,
        //   그 경로에서도 반드시 이 카운터를 함께 증가시켜야 HUD 전체 진범 수가 어긋나지 않는다.
        m_totalRegistered++;

        if (CanWriteNetworkState)
        {
            m_wanted.Add(entry);
            m_totalWanted.Value = m_totalRegistered;
        }
        else
            NotifyOfflineChange();

        // 로그용 문장은 여기서(서버 언어로) 한 번 만든다 — 콘솔은 개발자용이라 번역 대상이 아니다
        AppearanceDatabase database = assigner != null ? assigner.Database : null;
        string montageText =
            database != null ? database.BuildMontageText(appearance, revealedAxes) : "?";
        Debug.Log(
            $"[수배] 등록: \"{montageText}\" / 현상금 {(identity != null ? identity.Bounty : 0)}원 (현재 {m_open.Count}건)"
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
        for (int i = 0; i < m_open.Count; i++)
        {
            WantedEntry candidate = m_open[i];
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
        for (int i = 0; i < m_open.Count; i++)
        {
            if (m_open[i].NpcId != entryNpcId)
                continue;

            WantedEntry removed = m_open[i];
            m_open.RemoveAt(i);
            // 탈출(#231)·반출(#517) 시 되살릴 수 있게 보관 — 지워버리면 몽타주를 복원할 방법이 없다
            m_arrestedEntries[arrestedNpcId] = removed;
            if (CanWriteNetworkState)
                MirrorRemove(entryNpcId);
            else
                NotifyOfflineChange();
            Debug.Log($"[수배] 검거 완료로 제거 (남은 {m_open.Count}건)");
            return;
        }
    }

    /// <summary>
    /// 이 개체가 부합하는 조건을 다시 닫는다 — <b>판정을 거치지 않는 재수감</b> 전용. 서버·오프라인.
    ///
    /// 반출한 대상을 감옥 안에서 그대로 다시 앉히는 경로(<c>JailIntake.ServerReturnToJail</c>, #517)가
    /// 부른다. 그쪽은 문 앞 게이트를 지나지 않아 <see cref="ArrestJudge"/>가 돌지 않으므로, 반출 때
    /// <see cref="ReinstateByNpcId"/>로 열어 둔 조건을 여기서 닫아 줘야 짝이 맞는다.
    /// </summary>
    public void ServerRecloseFor(NpcController npc)
    {
        CitizenIdentity identity = npc != null ? npc.GetComponent<CitizenIdentity>() : null;
        if (identity == null)
            return;

        if (TryMatchOpen(identity.Appearance, out WantedEntry hit))
            ServerCloseEntry(hit.NpcId, npc.NetworkObjectId);
    }

    /// <summary>
    /// 검거로 내렸던 수배 항목을 되살린다 — 범인 탈출(#231)·반출(#517)이 부른다. 서버·오프라인 전용.
    /// 몽타주는 검거 시점에 보관해 둔 것을 그대로 쓴다 — 본부가 기억하던 인상착의와 일치해야
    /// "아까 그 놈"을 다시 찾는 재미가 성립한다.
    /// </summary>
    public void ReinstateByNpcId(ulong npcId)
    {
        if (!m_arrestedEntries.TryGetValue(npcId, out WantedEntry entry))
            return; // 진범으로 계상된 적 없는 대상 — 되살릴 것이 없는 것이 정상이다

        m_arrestedEntries.Remove(npcId);
        m_open.Add(entry);
        if (CanWriteNetworkState)
            m_wanted.Add(entry);
        else
            NotifyOfflineChange();
        Debug.Log($"[수배] 재등재 (NpcId {npcId}, 현재 {m_open.Count}건)");
    }

    // 세션 밖에서는 NetworkList의 OnListChanged가 영영 오지 않는다 — 뷰에 직접 "다시 그려라"를 보낸다. (#669)
    private void NotifyOfflineChange() => OnListReady?.Invoke();

    // 표시용 사본에서 같은 항목을 지운다.
    private void MirrorRemove(ulong entryNpcId)
    {
        for (int i = 0; i < m_wanted.Count; i++)
        {
            if (m_wanted[i].NpcId != entryNpcId)
                continue;

            m_wanted.RemoveAt(i);
            return;
        }
    }
}

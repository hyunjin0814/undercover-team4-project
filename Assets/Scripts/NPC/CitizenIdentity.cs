using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 한 명의 신원 — 런타임에 배정되는 시민 프로필과 범인 여부를 들고 있다. (이슈 #38)
/// 배정은 서버의 CriminalAssigner가 담당하고, 공개 가능한 신원(이름·타입·세력)은
/// NetworkVariable로 전 클라이언트에 동기화된다 (#52) — 뒤늦게 접속한 클라이언트도
/// 스폰 시점 초기 동기화로 현재 값을 받는다. 스캐너(#39)·진범 판정(#41) 등이 읽는다.
/// IsCriminal(정답)·Reaction(검거 반응)·Appearance(몽타주 기준)는 서버 전용 — 동기화하지 않는다.
/// NetworkBehaviour이므로 런타임 AddComponent 불가 — NPC 프리팹에 미리 부착돼 있어야 한다.
/// </summary>
public class CitizenIdentity : NetworkBehaviour
{
    [Header("공식 기록 (세력 심볼 조회용)")]
    [Tooltip("클라이언트가 동기화 수신값으로 프로필을 재조립할 때 쓴다 — NPC 프리팹에서 할당")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    // 서버 권위 신원 동기화 — 서버만 쓰고 전 피어가 읽는다.
    // 프로필 인스턴스(런타임 ScriptableObject)는 복제되지 않으므로 원시 데이터만 실어 나른다 (#52)
    private readonly NetworkVariable<CitizenData> m_syncedData = new NetworkVariable<CitizenData>();

    /// <summary>
    /// 이 NPC의 시민 프로필. 서버(또는 오프라인)는 배정 시, 클라이언트는 동기화 수신 시 채워진다.
    /// 배정 전에는 null — 스캐너는 이 경우를 "프로필 미배정"으로 안내한다.
    /// </summary>
    public CitizenProfile Profile { get; private set; }

    /// <summary>
    /// 몸체가 기계인가 — <b>타격 표현이 갈리는 기준</b>이다. 진압봉 타격음(깡/퍽, <see cref="EFx"/>)과
    /// 피격 신음(<see cref="NpcHurtVoice"/>)이 이 하나를 본다. 둘이 각자 판정하면 같은 NPC가
    /// 깡 소리를 내면서 신음하는 어긋남이 생긴다.
    ///
    /// <see cref="Profile"/>이 전 피어 동기화라(#52) 클라이언트에서도 같은 답이 나온다.
    /// 배정 전(스폰 직후 잠깐)에는 false — 갈래를 남기지 않으려고 사람 쪽으로 고정한다.
    /// </summary>
    public bool IsAndroidBody =>
        Profile != null && Profile.CitizenType == OfficialRecords.CitizenType.Android;

    /// <summary>
    /// 이 NPC가 수배 조건의 기준(출제자)인가 — 정답 자체는 아니다. 서버 전용 (클라이언트에서는 항상 false).
    /// 판정은 조건 부합만 본다(#669, <c>WantedListManager.TryMatchOpen</c>) — 이 값은 몽타주 발행
    /// 게이트이자(#127), 제보 전화 승격(#102)으로 false→true가 되는 공개 플래그로만 쓰인다.
    /// </summary>
    public bool IsCriminal { get; private set; }

    /// <summary>
    /// 위조범 여부 — 스캔 표시 정보(m_nameView 등)가 인명부 정본과 어긋나는 NPC. 위조 검거 판정(#320)의 기준.
    /// IsCriminal과 동일하게 서버 전용이며 동기화하지 않는다 (클라이언트에서는 항상 false).
    /// 진범과 독립 배정되어 겹칠 수 있고, 판정 시 진범(IsCriminal)이 우선한다.
    /// </summary>
    public bool IsForger { get; private set; }

    /// <summary>외형 특징 조합(#74) — 몽타주 부합 판정의 기준. AppearanceAssigner가 채워준다. 서버 전용.</summary>
    public AppearanceProfile Appearance { get; private set; } = AppearanceProfile.Unassigned;

    /// <summary>반응 유형(#76) — 스캔당하거나 플레이어에게 맞을 때 보이는 반응 (#400).
    /// CriminalAssigner가 배정하고, 순응형이 타격으로 뽑은 결과는 NpcController가 확정한다. 서버 전용.</summary>
    public ReactionType Reaction { get; private set; } = ReactionType.Compliant;

    /// <summary>
    /// 이 NPC를 검거했을 때의 현상금 (#395) — ArrestJudge가 판정 보상으로 그대로 쓴다. 서버 전용.
    /// 라운드 시작 배정 시점에 CriminalAssigner가 범위에서 뽑아 확정한다. 판정 시점에 뽑지 않는 이유는
    /// 재판정(#358)·탈옥 후 재검거(#231)가 허용되어 있어, 그때마다 새로 뽑으면 잡았다 풀었다 하며
    /// 높은 금액을 노리는 리롤이 가능해지기 때문이다.
    /// 오검거 대상(무고 시민)은 0이며, 난동꾼은 이 값을 쓰지 않는다(MisdemeanorOffender.Reward).
    /// </summary>
    public int Bounty { get; private set; }

    // ---- 동기화 수신 (클라이언트) ----

    public override void OnNetworkSpawn()
    {
        // 서버 본인은 배정 시 Profile을 직접 세팅하므로 재조립이 필요 없다 — 클라이언트만 수신한다
        if (IsServer)
            return;

        m_syncedData.OnValueChanged += HandleSyncedDataChanged;

        // 뒤늦게 접속한 클라이언트는 스폰 시점에 이미 배정된 값이 실려 온다 — 즉시 반영.
        // (스폰이 배정보다 먼저인 첫 접속 클라는 비어 있다가 OnValueChanged로 받는다)
        if (m_syncedData.Value.IsAssigned)
            RebuildProfile(m_syncedData.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_syncedData.OnValueChanged -= HandleSyncedDataChanged;
    }

    private void HandleSyncedDataChanged(CitizenData previous, CitizenData current)
    {
        if (current.IsAssigned)
            RebuildProfile(current);
    }

    // 동기화 수신값으로 로컬 프로필 인스턴스를 재조립한다 — 클라이언트 전용.
    // 서버의 원본과 같은 공개 데이터를 갖는 별도 인스턴스이며, 스캐너/UI는 차이를 모른다.
    private void RebuildProfile(CitizenData data)
    {
        CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
        profile.Initialize(
            data.Name.ToString(),
            data.Type,
            data.Faction,
            data.SymbolIndex,
            m_officialRecords
        );
        profile.m_nameView = data.NameView.ToString(); // 위조된 표시 이름 반영 — 정상 시민은 정본과 동일 (#223)
        Profile = profile;
    }

    // ---- 배정 (서버 · 오프라인 전용) ----

    /// <summary>
    /// 프로필 없이 살아났으면 신원 배정을 요청한다 — 라운드 시작 이후에 스폰된 NPC 전용 경로. (#505)
    ///
    /// <see cref="CriminalAssigner"/>의 배정은 라운드 시작 1회이고 스폰 목록(NpcSpawner.SpawnedNpcs)만
    /// 훑으므로, 라운드 중에 만들어지는 NPC(돌발 이벤트 난동꾼 #106 · 탈옥 침입자 #231)는 그 배정을
    /// 놓쳐 <see cref="Profile"/>이 계속 null이었다. 그러면 스캐너가 "프로필 미배정"으로 튕기는데,
    /// 침입자의 경우 <b>스캔 실패 자체가 정체를 알려 주는 tell</b>이 되어 "시민과 구분되지 않는다"(GDD 6-4 · #311)가 깨진다.
    ///
    /// <b>스폰하는 쪽이 부르지 않고 여기서 요청하는 이유</b>는 스폰 경로마다 배선하면 새 경로가 생길 때
    /// 잊기 쉽고, 잊었을 때 증상이 조용해서(스캔만 실패) 원인을 찾기 어렵기 때문이다.
    ///
    /// <b>Start인 이유는 둘이다.</b> ① 세션 중에는 이 시점에 <c>Spawn()</c>이 이미 끝나 있어(IsSpawned)
    /// AssignProfile이 동기화 변수에 써 전 클라이언트로 전파된다 — Spawn 전에 배정하면 호스트에만 보인다.
    /// ② 오프라인(미스폰)에서는 <see cref="OnNetworkSpawn"/>이 아예 불리지 않으므로 그쪽에 두면 동작하지 않는다.
    ///
    /// 라운드 시작 스폰분은 여기서 아무 일도 하지 않는다 — 배정이 먼저면 Profile이 채워져 있고,
    /// 아직이면 CriminalAssigner가 요청을 무시한다(그 배정이 곧 이 NPC까지 덮는다). 순서에 기대지 않는다.
    /// </summary>
    private void Start()
    {
        // 배정은 서버 권위 — 클라이언트는 m_syncedData로 받는다 (AssignProfile과 같은 기준)
        if (IsSpawned && !IsServer)
            return;

        if (Profile != null)
            return;

        CriminalAssigner assigner = App.Game.CriminalAssigner;
        if (assigner != null)
            assigner.AssignLateSpawned(this);
    }

    /// <summary>
    /// 프로필과 범인 여부를 배정한다. 서버(또는 오프라인)의 CriminalAssigner 전용.
    /// 공개 가능한 부분은 동기화 변수에 함께 기록되어 전 클라이언트에 전파된다 —
    /// 오프라인(미스폰)에서는 로컬 프로퍼티만으로 동작한다 (NpcController.m_networkState와 동일 이중 구조).
    /// </summary>
    public void AssignProfile(CitizenProfile profile, bool isCriminal)
    {
        Profile = profile;
        IsCriminal = isCriminal;

        if (IsSpawned && IsServer)
            m_syncedData.Value = CitizenData.FromProfile(profile);
    }

    /// <summary>
    /// 범인 여부만 바꾼다 — 프로필은 그대로 둔다. 제보 전화 승격(#102) 전용.
    /// 프로필까지 다시 배정하면 CitizenData 동기화 스냅샷이 재전송되어, 본부가 보고 있던
    /// 스캔 표시값이 이유 없이 깜빡인다. IsCriminal은 서버 전용이라 동기화할 것이 없다.
    /// </summary>
    public void SetCriminal(bool isCriminal)
    {
        IsCriminal = isCriminal;
    }

    /// <summary>외형 특징 조합을 배정한다. AppearanceAssigner 전용. (서버 전용 — 동기화 없음)</summary>
    public void AssignAppearance(AppearanceProfile appearance)
    {
        Appearance = appearance;
    }

    /// <summary>위조범 여부를 배정한다. CriminalAssigner 전용. (서버 전용 — 동기화 없음, #320)</summary>
    public void AssignForgery(bool isForger)
    {
        IsForger = isForger;
    }

    /// <summary>
    /// 검거 반응 유형을 배정한다. (서버 전용 — 동기화 없음)
    /// CriminalAssigner가 라운드 시작에 배정하고, 순응형이 피격으로 도주·저항을 뽑았을 때
    /// NpcController가 그 결과를 여기에 1회 확정한다 (#400).
    /// </summary>
    public void AssignReaction(ReactionType reaction)
    {
        Reaction = reaction;
    }

    /// <summary>현상금을 배정한다. CriminalAssigner 전용. (서버 전용 — 동기화 없음, #395)</summary>
    public void AssignBounty(int bounty)
    {
        Bounty = bounty;
    }
}

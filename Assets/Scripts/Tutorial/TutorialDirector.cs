using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 튜토리얼 진행 — 로컬 호스트를 띄우고 단계별 안내 문구를 바꾼다. 이 컴포넌트의 존재가 튜토리얼 판별 기준이다.
/// 단계 판정은 이벤트 깃발과 매 프레임 상태 폴링으로 한다.
/// </summary>
public class TutorialDirector : MonoBehaviour
{
    private const string k_table = "HudTable";
    private const string k_keyPrefix = "Hud.Tutorial.";

    private const string k_slotsKey = "Slots";
    private const string k_fieldStartKey = "Scan";

    private readonly struct Step
    {
        public readonly string Key;
        public readonly Func<TutorialDirector, bool> IsDone;

        public Step(string key, Func<TutorialDirector, bool> isDone)
        {
            Key = key;
            IsDone = isDone;
        }
    }

    private static readonly Step[] s_steps =
    {
        new Step("Move", d => d.m_movedDistance >= d.m_moveDistance),
        new Step(k_slotsKey, d => d.m_slotChanged),
        new Step("Aim", d => d.m_interactor != null && d.m_interactor.CurrentInteractable != null),
        new Step("TeamTab", d => d.m_teamPanelSeen),
        new Step("Cctv", d => d.m_cctvSwitched),
        new Step(k_fieldStartKey, d => d.m_scanned),
        new Step("Subdue", d => d.AnyNpcStunned),
        new Step("Rope", d => d.m_escorter != null && d.m_escorter.IsDraggingAny),
        new Step("Jail", d => d.m_judged),
        new Step("Verdict", d => d.m_judged),
    };

    [Header("배선")]
    [Tooltip("본부 CCTV 콘솔의 모니터 — 채널을 한 번 돌렸는지 본다. HQ/Interior/CCTVConsole/CCTVMonitor")]
    [SerializeField]
    private CCTVSwitcher m_cctv;

    [Tooltip("본부 대문 전부 — 본부 학습이 끝나면 자동으로 연다. HQ/Doors/* (남·북 두 짝)")]
    [SerializeField]
    private DoubleDoor[] m_hqGates;

    [Header("단계 기준값")]
    [Tooltip("이동 단계를 통과시킬 누적 이동 거리(m)")]
    [Min(0f)]
    [SerializeField]
    private float m_moveDistance = 6f;

    [Tooltip("문구가 최소한 이만큼은 떠 있는다 — 조건이 곧바로 충족돼도 읽을 시간을 준다(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_minPromptSeconds = 3f;

    [Tooltip("시체 경고 토스트가 떠 있는 시간(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_corpseToastSeconds = 6f;

    [Tooltip("마지막 문구를 읽을 시간 — 이 시간이 지나면 타이틀로 돌아간다(초)")]
    [Min(0f)]
    [SerializeField]
    private float m_finishDelaySeconds = 8f;

    private int m_stepIndex = -1;
    private LocalizedString m_shownPrompt;
    private float m_movedDistance;
    private Vector3 m_lastPlayerPosition;
    private bool m_finished;

    private float m_stepShownTime;

    private bool m_slotChanged;
    private bool m_cctvSwitched;
    private bool m_scanned;
    private bool m_judged;
    private bool m_teamPanelSeen;

    private bool m_corpseWarned;

    private Transform m_playerTransform;
    private PlayerInteractor m_interactor;
    private PlayerEscorter m_escorter;
    private PlayerItemUser m_itemUser;
    private Scanner m_boundScanner;

    private ArrestJudge m_boundJudge;
    private bool m_cctvBound;

    private static bool AnyNpc(Func<NpcController, bool> test)
    {
        NpcSpawner spawner = App.Game.NpcSpawner;
        if (spawner == null)
            return false;

        foreach (NpcController npc in spawner.SpawnedNpcs)
            if (npc != null && test(npc))
                return true;

        return false;
    }

    private bool AnyNpcStunned => AnyNpc(n => n.Stun != null && n.Stun.IsStunned);

    private bool AnyNpcDead => AnyNpc(n => n.Death != null && n.Death.IsDead);

    public static bool IsActive { get; private set; }

    private void Awake() => IsActive = true;

    private void Start()
    {
        TutorialFlow.MarkOffered();
        HostAsync().Forget();
    }

    private void OnDestroy()
    {
        IsActive = false;
        HidePrompt();
        UnbindAll();
    }

    /// <summary>Relay 없이 로컬 호스트를 띄우고 라운드를 준비한다(빌드에도 포함).</summary>
    private async UniTaskVoid HostAsync()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null)
        {
            Debug.LogWarning("[튜토리얼] NetworkManager가 없어 시작할 수 없다", this);
            return;
        }

        if (net.IsListening)
            return;

        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        if (App.Net.Session != null)
        {
            ConnectionApprovalGate.StampLocalPayload(net);
            App.Net.Session.Approval.Install(net);
        }

        net.StartHost();

        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        App.Game.ReadyGate?.ReportSelfReady();

        App.Game.Round?.BeginRoundPreparation();

        Advance();
    }

    private void Update()
    {
        BindLate();
        TrackMovement();
        TrackTeamPanel();
        TrackCorpse();

        if (m_finished || m_stepIndex < 0 || m_stepIndex >= s_steps.Length)
            return;

        if (Time.unscaledTime - m_stepShownTime < m_minPromptSeconds)
            return;

        if (s_steps[m_stepIndex].IsDone(this))
            Advance();
    }

    private void Advance()
    {
        m_stepIndex++;
        m_stepShownTime = Time.unscaledTime;

        if (m_stepIndex >= s_steps.Length)
        {
            m_finished = true;
            ShowPrompt("Done");
            FinishAsync().Forget();
            return;
        }

        if (s_steps[m_stepIndex].Key == k_fieldStartKey && m_hqGates != null)
            foreach (DoubleDoor gate in m_hqGates)
                if (gate != null)
                    gate.ServerSetOpen(true);

        ShowPrompt(s_steps[m_stepIndex].Key);
    }

    private string CurrentStepKey =>
        m_stepIndex >= 0 && m_stepIndex < s_steps.Length ? s_steps[m_stepIndex].Key : null;

    private async UniTaskVoid FinishAsync()
    {
        await UniTask.Delay(
            TimeSpan.FromSeconds(m_finishDelaySeconds),
            cancellationToken: this.GetCancellationTokenOnDestroy()
        );

        TutorialFlow.Exit();
    }

    private void ShowPrompt(string key)
    {
        HidePrompt();

        if (App.UI.Prompt == null)
            return;

        m_shownPrompt = new LocalizedString(k_table, k_keyPrefix + key);
        App.UI.Prompt.Show(m_shownPrompt);
    }

    private void HidePrompt()
    {
        if (m_shownPrompt == null)
            return;

        App.UI.Prompt?.Hide(m_shownPrompt);
        m_shownPrompt = null;
    }

    private void BindLate()
    {
        if (m_playerTransform == null)
        {
            NetworkObject player = NetworkManager.Singleton?.LocalClient?.PlayerObject;
            if (player != null)
            {
                m_playerTransform = player.transform;
                m_lastPlayerPosition = m_playerTransform.position;
                m_interactor = player.GetComponent<PlayerInteractor>();
                m_escorter = player.GetComponent<PlayerEscorter>();

                m_itemUser = player.GetComponent<PlayerItemUser>();
                if (m_itemUser != null)
                    m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
            }
        }

        if (!m_cctvBound && m_cctv != null)
        {
            m_cctv.OnDisplayChanged += HandleCctvChanged;
            m_cctvBound = true;
        }

        if (m_boundJudge == null && App.Game.ArrestJudge != null)
        {
            m_boundJudge = App.Game.ArrestJudge;
            m_boundJudge.OnArrestJudged += HandleArrestJudged;
            m_boundJudge.OnCorpseJudged += HandleArrestJudged;
        }
    }

    private void UnbindAll()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;
        if (m_boundScanner != null)
            m_boundScanner.OnScanCompleted -= HandleScanCompleted;
        if (m_cctvBound && m_cctv != null)
            m_cctv.OnDisplayChanged -= HandleCctvChanged;
        if (m_boundJudge != null)
        {
            m_boundJudge.OnArrestJudged -= HandleArrestJudged;
            m_boundJudge.OnCorpseJudged -= HandleArrestJudged;
        }
    }

    private void TrackMovement()
    {
        if (m_playerTransform == null)
            return;

        Vector3 now = m_playerTransform.position;
        m_movedDistance += Vector3.Distance(now, m_lastPlayerPosition);
        m_lastPlayerPosition = now;
    }

    private void TrackCorpse()
    {
        if (m_corpseWarned || !AnyNpcDead)
            return;

        m_corpseWarned = true;
        App.UI.Toast?.Show(new LocalizedString(k_table, k_keyPrefix + "Corpse"), m_corpseToastSeconds);
    }

    private void TrackTeamPanel()
    {
        if (m_teamPanelSeen || App.UI.Game == null)
            return;

        if (App.UI.Game.TryGetPanel(out TeamStatusPanel panel) && panel.IsOpened)
            m_teamPanelSeen = true;
    }

    private void HandleEquippedItemChanged(ItemBase item)
    {
        if (CurrentStepKey == k_slotsKey)
            m_slotChanged = true;

        if (m_boundScanner != null)
        {
            m_boundScanner.OnScanCompleted -= HandleScanCompleted;
            m_boundScanner = null;
        }

        m_boundScanner = item as Scanner;
        if (m_boundScanner != null)
            m_boundScanner.OnScanCompleted += HandleScanCompleted;
    }

    private void HandleScanCompleted(CitizenProfile profile, ulong npcId) => m_scanned = true;

    private void HandleCctvChanged() => m_cctvSwitched = true;

    private void HandleArrestJudged(ArrestResult result) => m_judged = true;
}

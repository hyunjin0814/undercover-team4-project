using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상점의 치장 자판기 (#818 D) — E를 누르면 토큰 1개로 치장 하나를 무작위로 뽑는다.
/// 뽑은 것은 계정 보유함에 쌓이고 커스터마이징 창에서 잠금이 풀린다.
///
/// <b>뽑기는 로컬 권위다.</b> 토큰이 계정 소유(각자의 Cloud Save)라 호스트가 남의 잔량을 읽거나
/// 깎을 수 없다 — 서버 권위로 만들려면 UGS Cloud Code가 필요하고, 순수 코스메틱이라 조작해도
/// 조작한 사람 모자만 늘어난다. <see cref="CosmeticLocker"/>가 '순수 로컬 동작'인 것과 같은 자리다.
///
/// <b>연출이 뽑은 사람과 남에게 다르다.</b> 뽑은 사람은 아이콘이 흘러가다 가운데에 멈추는 릴
/// (<see cref="CosmeticGachaPanel"/>)을 보고, 같이 서 있는 사람은 기계 위에 그 치장이 뜨는 것만
/// 본다 — 남이 돌릴 때마다 내 화면이 릴에 덮이면 안 된다. 그래서 결과를 서버에 한 번 올려
/// 나머지에게만 뿌린다. 값이 위조돼도 남의 화면에 잠깐 뜨는 모형이 바뀔 뿐이라 검증하지 않는다.
///
/// <b>한 번에 한 사람만 돌린다.</b> 기계는 서버가 쥐는 자리표 하나(<see cref="m_holder"/>)를 두고,
/// 누가 돌리는 동안 남이 누르면 사유를 띄우고 돌려보낸다. 예전에는 동시에 눌릴 수 있었고, 그러면
/// 기계 위 모형 연출 두 개가 <see cref="m_shown"/> 한 칸을 두고 겹쳐 나중 것이 앞의 것을 덮어써
/// 앞 모형이 지워지지 않고 남았다. 자리표가 그 경합 자체를 없앤다.
/// 뽑기·장부는 여전히 로컬이 한다 — 서버가 주고받는 것은 <b>차례</b>뿐이다.
///
/// 콜라이더는 반드시 <c>Interactable</c> 레이어에 둘 것 — PlayerInteractor의 조준 마스크가 그 레이어만 본다.
/// </summary>
public class CosmeticGachaMachine : NetworkBehaviour, IInteractable
{
    private const string k_table = "ShopTable";

    [Header("뽑기")]
    [Tooltip("뽑을 치장 목록 — Player 프리팹·커스터마이징 창과 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Header("연출")]
    [Tooltip("투입구로 빨려 들어갈 토큰 그림. 비우면 투입 연출을 건너뛴다")]
    [SerializeField] private Sprite m_coinSprite;

    [Tooltip("투입구 위치 — 자판기 기준 로컬 오프셋(m). 인스펙터에서 눈으로 맞출 것")]
    [SerializeField] private Vector3 m_coinSlotOffset = new Vector3(0.42f, 0.4f, 0.2f);

    [Tooltip("토큰 지름(m) — 투입구 폭에 맞춘다")]
    [SerializeField] private float m_coinSize = 0.055f;

    [Tooltip("토큰이 들어가는 데 걸리는 시간(초) — 이 뒤에 룰렛이 돈다")]
    [SerializeField] private float m_coinInsertSeconds = 0.5f;

    [Tooltip("캡슐·치장이 뜰 자리 — 자판기 위 공중. 비우면 자판기 자신의 위치를 쓴다")]
    [SerializeField] private Transform m_dispenseAnchor;

    [Tooltip("먼저 굴러 나오는 캡슐. 비우면 캡슐 없이 치장부터 뜬다")]
    [SerializeField] private GameObject m_capsulePrefab;

    [Tooltip("치장 프리팹은 머리에 붙는 크기라 그대로 두면 너무 작다 — 연출용 배율")]
    [SerializeField] private float m_revealScale = 4f;

    [Tooltip("캡슐이 나와 있는 시간(초)")]
    [SerializeField] private float m_capsuleSeconds = 0.8f;

    [Tooltip("치장이 커지며 나타나는 시간(초)")]
    [SerializeField] private float m_popSeconds = 0.35f;

    [Tooltip("다 커진 뒤 돌며 머무는 시간(초)")]
    [SerializeField] private float m_holdSeconds = 2.5f;

    [Tooltip("도는 속도(도/초)")]
    [SerializeField] private float m_spinDegreesPerSecond = 90f;

    [Tooltip("뽑은 사람에게만 들리는 소리")]
    [SerializeField] private EAudioClip m_drawSound = EAudioClip.ShopPurchase;

    [Tooltip("결과 문구가 떠 있는 시간(초)")]
    [SerializeField] private float m_messageSeconds = 4f;

    // 자리표가 비어 있음 — 클라이언트 아이디는 0부터라 0을 '없음'으로 쓸 수 없다
    private const ulong k_noHolder = ulong.MaxValue;

    // 서버 답을 기다리는 한도(초) — 왕복 한 번이라 넉넉하다
    private const float k_turnTimeoutSeconds = 3f;

    // 서버만 읽고 쓴다. NetworkVariable이 아닌 것은 클라이언트가 이 값을 볼 일이 없어서다 —
    // 누를 수 있는지는 서버가 답으로 알려 주고, 프롬프트는 남의 차례에도 그대로 뜬다(사유를 눌러서 본다).
    private ulong m_holder = k_noHolder;

    // 기계 위 모형 연출이 겹치지 않게 — 남의 결과를 잇달아 받아도 하나만 돈다
    private bool m_playing;

    // 내 뽑기가 코인 투입~릴 오픈 사이를 지나는 중 — 그 사이는 IsReelSpinning()이 아직 false라
    // 이 플래그가 없으면 그 틈에 다시 눌러 토큰을 이중으로 쓸 수 있다.
    // 서버에 차례를 물어보는 왕복 동안에도 켜 둔다.
    private bool m_drawing;

    // 차례를 물어보고 답을 기다리는 중 — 답이 영영 안 오면(호스트 이탈) 워치독이 푼다
    private bool m_awaitingTurn;

    // 지금 떠 있는 모형 — 연출이 끊기면(씬 전환·파괴) 같이 지운다
    private GameObject m_shown;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Gacha;

    // 내 릴이 도는 중이거나 내 뽑기가 진행 중일 때만 막는다. 토큰이 없다고, 또 남이 뽑는 중이라고
    // 미리 막지 않는 것은 눌러서 사유를 볼 수 있게 하려는 것이고, 표시 방식은 ShopStand와 같다.
    // 기계 위 모형(m_playing)도 여기서 보지 않는다 — 남의 연출이 도는 동안 프롬프트가 사라지면
    // 왜 못 누르는지 읽히지 않는다.
    public bool CanInteract(GameObject interactor) => !m_drawing && !IsReelSpinning();

    public void Interact(GameObject interactor)
    {
        if (m_drawing || IsReelSpinning())
            return;

        if (m_catalog == null)
        {
            Debug.LogWarning($"[{nameof(CosmeticGachaMachine)}] 카탈로그가 연결되지 않았습니다 (#818 D)", this);
            return;
        }

        if (CosmeticInventory.Tokens <= 0)
        {
            ShowMessage("Shop.Gacha.NoToken");
            return;
        }

        // 세션이 아니면(씬 단독 Play) 차례를 다툴 상대가 없다
        if (!IsSpawned)
        {
            BeginDraw();
            return;
        }

        // <b>토큰은 차례를 받은 뒤에 쓴다.</b> 먼저 쓰고 서버가 거절하면 낸 토큰만 사라진다.
        m_drawing = true;
        m_awaitingTurn = true;
        RequestTurnRpc();
        WatchTurnAsync().Forget();
    }

    /// <summary>
    /// 차례를 받았다 — 여기서부터가 예전의 뽑기 본문이다(뽑고, 장부를 적고, 연출을 돌린다).
    /// </summary>
    private void BeginDraw()
    {
        if (!Roll(out EAccessorySlot slot, out int index))
        {
            Debug.LogWarning($"[{nameof(CosmeticGachaMachine)}] 뽑을 항목이 없습니다 — 카탈로그가 비었습니다 (#818 D)", this);
            EndDraw(); // 받은 차례를 도로 놓는다 — 안 그러면 기계가 잠긴 채로 남는다
            return;
        }

        // <b>장부는 먼저, 연출은 나중이다.</b> 연출 끝에 담으면 그 사이에 씬을 나가거나 게임을
        // 끄면 토큰만 사라진다. 화면은 늦어도 되지만 잔량은 그렇지 않다.
        if (!CosmeticInventory.TrySpendToken())
        {
            EndDraw(); // 뽑기 사이에 토큰이 없어졌다(다른 창에서 소모 등)
            return;
        }

        // 환급 판정은 <b>보유함이 아니라 IsOwned</b>로 한다 — 기본 지급 세트는 보유함에 비트가
        // 없으므로(카탈로그가 판정한다) Grant의 반환값만 보면 처음부터 갖고 있던 8개를 뽑을 때마다
        // "새로 얻었다"가 되어 토큰이 환급 없이 사라졌다.
        bool alreadyOwned = CosmeticInventory.IsOwned(m_catalog, slot, index);
        CosmeticInventory.Grant(slot, index); // 기본 세트여도 비트는 켜 둔다 — 판정 출처를 하나로 모은다
        bool gained = !alreadyOwned;

        if (alreadyOwned)
            CosmeticInventory.AddTokens(1); // 중복은 환급이다 (팀 결정 #818 D)

        App.Sound?.PlaySfx2D(m_drawSound);

        // 토큰이 들어가는 것을 보여 준 뒤에 돌린다 — 넣지도 않았는데 릴부터 돌면 무엇을 내고
        // 받는 것인지 읽히지 않는다. 장부는 이미 위에서 끝냈으므로 이 연출이 끊겨도 잔량은 옳다.
        DrawAsync(slot, index, gained).Forget();

        // 같이 서 있는 사람도 보게 한다 — 세션이 아니면(씬 단독 Play) 보낼 곳이 없다
        if (IsSpawned)
            ReportDrawRpc((byte)slot, index);
    }

    /// <summary>
    /// 뽑은 사람의 연출 — 토큰이 투입구로 들어가고, 그 뒤에 릴이 돈다.
    ///
    /// 릴이 결과 문구까지 띄운다 — 창이 화면을 덮으므로 토스트는 그 뒤에 가린다.
    /// 릴이 없는 씬(패널 미배치)에서는 기계 위 모형과 토스트로 물러난다.
    ///
    /// <b>연출이 다 끝나야 차례를 놓는다</b> — 릴이 도는 중에 놓으면 그 사이 남이 돌릴 수 있고,
    /// 그러면 겹친 연출이 다시 모형을 덮어쓴다(클래스 주석 참고).
    /// </summary>
    private async UniTaskVoid DrawAsync(EAccessorySlot slot, int index, bool gained)
    {
        // 여기까지는 Interact와 같은 프레임에 동기로 실행된다 — 첫 await 앞에서 켜야 틈이 없다
        m_drawing = true;
        try
        {
            await PlayCoinInsertAsync();

            CosmeticGachaPanel reel = FindReel();
            if (reel != null)
            {
                // Play는 첫 await 앞에서 IsSpinning을 켜므로 이 줄 다음에 이미 참이다
                reel.Play(slot, index, gained);
                await UniTask.WaitWhile(
                    () => reel != null && reel.IsSpinning,
                    cancellationToken: destroyCancellationToken
                );
            }
            else
            {
                ShowResultMessage(slot, index, gained);
                await PlayRevealAsync(slot, index);
            }
        }
        catch (OperationCanceledException)
        {
            // 자판기가 사라졌다(씬 전환)
        }
        finally
        {
            EndDraw();
        }
    }

    /// <summary>
    /// 토큰 한 닢이 투입구 위에 떠서 돌다가 빨려 들어간다 (#850) — 그림이 없으면 아무것도 하지 않는다.
    ///
    /// 스프라이트로 그리는 이유는 이 토큰이 <b>UI에만 있던 그림</b>이라서다 — 같은 그림을 쓰면
    /// 상점 HUD의 보유 개수와 여기서 사라지는 한 닢이 같은 물건으로 읽힌다.
    /// 늘 보는 사람 쪽을 향하게 세운다 — 자판기 앞면이 어느 축인지는 프롭마다 다르다.
    /// </summary>
    private async UniTask PlayCoinInsertAsync()
    {
        if (m_coinSprite == null || m_coinInsertSeconds <= 0f)
            return;

        Vector3 slot = transform.TransformPoint(m_coinSlotOffset);
        var coin = new GameObject("GachaCoin");
        var renderer = coin.AddComponent<SpriteRenderer>();
        renderer.sprite = m_coinSprite;

        // 스프라이트는 스케일 1이 곧 1m가 아니다(PPU에 따라 십수 m가 되기도 한다) — 지름을 m로
        // 받으려면 그림의 실제 크기로 나눠야 한다. 안 그러면 동전이 자판기만 해진다.
        float spriteWidth = m_coinSprite.bounds.size.x;
        float scale = spriteWidth > 0f ? m_coinSize / spriteWidth : m_coinSize;

        // 자판기 정면(로컬 +Z) 바깥에서 출발해 투입구 <b>안쪽</b>까지 들어간다 — 표면에서 멈추면
        // 넣다 만 것으로 보인다
        Vector3 start = slot + transform.forward * (m_coinSize * 2.5f);
        Vector3 end = slot - transform.forward * (m_coinSize * 3f);

        try
        {
            float elapsed = 0f;
            while (elapsed < m_coinInsertSeconds)
            {
                await UniTask.NextFrame(destroyCancellationToken);
                elapsed += Time.deltaTime;

                float t = Mathf.Clamp01(elapsed / m_coinInsertSeconds);
                // 위에서 떨어뜨리지 않고 <b>앞에서 밀어 넣는다</b> — 투입구는 세로 홈이라 동전은
                // 정면에서 들어간다. 나오는 거리는 토큰 크기를 따라간다.
                coin.transform.position = Vector3.Lerp(start, end, t * t);

                // 마지막 구간에서만 사라진다 — 처음부터 줄이면 들어가는 것이 아니라 녹는 것으로 보인다
                float shrink = t < 0.85f ? 1f : 1f - ((t - 0.85f) / 0.15f);
                coin.transform.localScale = Vector3.one * (scale * shrink);

                Camera view = Camera.main;
                if (view != null)
                    coin.transform.rotation = Quaternion.LookRotation(
                        coin.transform.position - view.transform.position
                    );
            }
        }
        finally
        {
            if (coin != null)
                Destroy(coin);
        }
    }

    /// <summary>
    /// 카탈로그 전체에서 균등하게 하나 고른다 (#818 D) — <b>슬롯을 먼저 고르지 않는다</b>.
    /// 슬롯을 고른 뒤 그 안에서 고르면 항목이 5개인 마스크와 31개인 머리카락이 같은 확률을 받아,
    /// 머리카락 한 개가 나올 확률이 마스크 한 개의 6분의 1이 된다.
    ///
    /// "안 씀"(0)은 뽑히지 않는다 — 뽑을 물건이 아니다.
    /// </summary>
    private bool Roll(out EAccessorySlot slot, out int index)
    {
        var pool = new List<(EAccessorySlot Slot, int Index)>();
        foreach (EAccessorySlot candidate in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int count = m_catalog.CountOf(candidate);
            for (int i = 1; i < count; i++)
                if (m_catalog.Get(candidate, i) != null)
                    pool.Add((candidate, i));
        }

        if (pool.Count == 0)
        {
            slot = default;
            index = 0;
            return false;
        }

        (slot, index) = pool[UnityEngine.Random.Range(0, pool.Count)];
        return true;
    }

    /// <summary>
    /// 차례를 달라고 서버에 묻는다. 비어 있으면 자리표를 쥐여 주고, 남이 쥐고 있으면 사유를 돌려보낸다.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestTurnRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;

        // 이미 이 사람이 쥐고 있다 — 답이 늦어 다시 물은 것이다. 새로 돌리게 하면 토큰이 두 번 나간다.
        if (m_holder == sender)
            return;

        if (m_holder != k_noHolder)
        {
            DenyTurnRpc(RpcTarget.Single(sender, RpcTargetUse.Temp));
            return;
        }

        m_holder = sender;
        GrantTurnRpc(RpcTarget.Single(sender, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void GrantTurnRpc(RpcParams rpcParams)
    {
        // 워치독이 이미 포기한 뒤 늦게 도착했다 — 서버는 내 이름으로 자리표를 쥐고 있으므로 놓아 준다
        if (!m_awaitingTurn)
        {
            ReleaseTurnRpc();
            return;
        }

        m_awaitingTurn = false;
        BeginDraw();
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void DenyTurnRpc(RpcParams rpcParams)
    {
        if (!m_awaitingTurn)
            return;

        m_awaitingTurn = false;
        m_drawing = false;
        ShowMessage("Shop.Gacha.Busy");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReleaseTurnRpc(RpcParams rpcParams = default)
    {
        // 쥔 사람만 놓을 수 있다 — 늦게 도착한 놓기가 다음 사람 차례를 뺏지 않게
        if (m_holder == rpcParams.Receive.SenderClientId)
            m_holder = k_noHolder;
    }

    /// <summary>
    /// 뽑기 한 판이 끝났다(성공·중단 무관) — 로컬 플래그를 내리고 서버 자리표를 놓는다.
    /// </summary>
    private void EndDraw()
    {
        m_drawing = false;
        m_awaitingTurn = false;

        if (IsSpawned)
            ReleaseTurnRpc();
    }

    /// <summary>
    /// 답이 오지 않는 경우를 푼다 — 호스트가 나가면 Grant도 Deny도 오지 않아 기계가 영영 잠긴다.
    /// </summary>
    private async UniTaskVoid WatchTurnAsync()
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(k_turnTimeoutSeconds),
                DelayType.UnscaledDeltaTime,
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return; // 자판기가 사라졌다
        }

        if (!m_awaitingTurn)
            return; // 답이 왔다 — 정상

        m_awaitingTurn = false;
        m_drawing = false;
    }

    // 쥔 채로 나간 사람의 자리표를 푼다 — 안 그러면 그 세션 내내 아무도 못 뽑는다
    private void OnClientDisconnected(ulong clientId)
    {
        if (m_holder == clientId)
            m_holder = k_noHolder;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;

        m_holder = k_noHolder;
        base.OnNetworkDespawn();
    }

    // 로컬 권위라 서버는 검사하지 않고 그대로 중계한다 — 연출 값이다 (클래스 주석 참고).
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportDrawRpc(byte slot, int index, RpcParams rpcParams = default)
    {
        // 뽑은 사람은 이미 로컬에서 돌렸다 — 되보내면 두 번 돈다
        PlayDrawRpc(
            slot,
            index,
            RpcTarget.Not(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void PlayDrawRpc(byte slot, int index, RpcParams rpcParams)
    {
        // 내 릴이 돌고 있으면 <b>내가 뽑은 것</b>이다 — 발신자 제외가 어긋나도 두 연출이 겹치지 않게 막는다.
        // 보내는 쪽에서 이미 발신자를 빼지만, 그 판정이 틀리면 뽑은 사람 화면에 릴과 모형이 함께 뜬다.
        if (m_playing || m_catalog == null || IsReelSpinning())
            return;

        PlayRemoteDrawAsync((EAccessorySlot)slot, index).Forget();
    }

    /// <summary>
    /// 남이 돌리는 것도 토큰 투입부터 보인다 — 기계 위 모형만 뜨면 무엇 때문에 나온 것인지 모른다.
    ///
    /// <b>모형은 릴이 멈출 때까지 기다린다</b> (#850) — 뽑은 사람은 릴이 다 돌아야 결과를 아는데,
    /// 옆 사람 화면에 물건이 먼저 뜨면 당첨을 남이 먼저 보는 셈이 된다. 기다리는 시간은 이 씬의
    /// 릴에서 읽는다(모두 같은 프리팹이라 값이 같다).
    /// </summary>
    private async UniTaskVoid PlayRemoteDrawAsync(EAccessorySlot slot, int index)
    {
        try
        {
            await PlayCoinInsertAsync();

            CosmeticGachaPanel reel = FindReel();
            float wait = (reel != null ? reel.SpinSeconds : 0f) - m_coinInsertSeconds;
            if (wait > 0f)
                await UniTask.Delay(
                    TimeSpan.FromSeconds(wait),
                    DelayType.UnscaledDeltaTime,
                    cancellationToken: destroyCancellationToken
                );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 기다리는 사이에 다른 연출이 시작됐다 — 덮어쓰면 앞 모형이 남는다.
        // 위 PlayDrawRpc의 검사는 <b>기다리기 전</b> 것이라 이 틈을 못 본다.
        if (m_playing)
            return;

        PlayRevealAsync(slot, index).Forget();
    }

    // 릴은 스스로 UI 매니저에 등록한다 — 자판기가 인스펙터로 물고 있지 않는 이유는
    // CosmeticLocker가 커스터마이징 창을 찾는 것과 같다(씬에 자판기가 늘면 배선이 여러 벌 된다).
    private static CosmeticGachaPanel FindReel() =>
        App.UI.Current != null && App.UI.Current.TryGetPanel(out CosmeticGachaPanel panel)
            ? panel
            : null;

    private static bool IsReelSpinning()
    {
        CosmeticGachaPanel reel = FindReel();
        return reel != null && reel.IsSpinning;
    }

    /// <summary>
    /// 캡슐이 나오고, 뽑힌 치장이 커지며 돌다 사라진다. 모형은 카탈로그 프리팹을 그대로 쓴다 —
    /// 머리에 붙는 것과 같은 물건이 나와야 무엇을 얻었는지 알아볼 수 있다.
    /// </summary>
    private async UniTask PlayRevealAsync(EAccessorySlot slot, int index)
    {
        // <b>m_shown은 한 칸뿐이라 겹치면 나중 것을 버린다.</b> 예전에는 나중 것이 앞의 것을
        // 덮어써 앞 모형이 영영 남았다. 여기서 지우고 시작하면 그 누수는 막히지만 이번엔 반대로
        // 도는 연출의 모형을 남이 뺏어 가, 그쪽 SpinAsync가 제자리에서 끝나며 <b>내 모형</b>을
        // 치운다(모형이 먼저 사라진다). 차례표 덕에 정상 흐름에서는 여기 겹쳐 들지 않고,
        // 남는 것은 릴이 없는 씬(패널 미배치)에서 남의 연출 꼬리와 겹치는 경우뿐이다.
        if (m_playing)
            return;

        m_playing = true;
        try
        {
            Transform anchor = m_dispenseAnchor != null ? m_dispenseAnchor : transform;

            if (m_capsulePrefab != null)
            {
                m_shown = Instantiate(m_capsulePrefab, anchor.position, anchor.rotation);
                await UniTask.Delay(
                    TimeSpan.FromSeconds(m_capsuleSeconds),
                    cancellationToken: destroyCancellationToken
                );
                Clear();
            }

            GameObject prefab = m_catalog.Get(slot, index);
            if (prefab == null)
                return;

            m_shown = Instantiate(prefab, anchor.position, anchor.rotation);
            await SpinAsync(m_shown.transform);
        }
        catch (OperationCanceledException)
        {
            // 자판기가 사라졌다(씬 전환) — 모형은 아래 finally가 치운다
        }
        finally
        {
            Clear();
            m_playing = false;
        }
    }

    // 커지며 나타나고, 다 커진 뒤 제자리에서 돈다. 시간 기반이라 프레임률과 무관하다.
    private async UniTask SpinAsync(Transform shown)
    {
        float elapsed = 0f;
        while (elapsed < m_popSeconds + m_holdSeconds)
        {
            await UniTask.NextFrame(destroyCancellationToken);
            if (shown == null)
                return;

            elapsed += Time.deltaTime;
            float grow = m_popSeconds <= 0f ? 1f : Mathf.Clamp01(elapsed / m_popSeconds);
            shown.localScale = Vector3.one * (m_revealScale * grow);
            shown.Rotate(Vector3.up, m_spinDegreesPerSecond * Time.deltaTime, Space.World);
        }
    }

    private void Clear()
    {
        if (m_shown != null)
            Destroy(m_shown);

        m_shown = null;
    }

    // 연출이 도는 중에 파괴되면 UniTask가 취소되지만 모형은 별개 오브젝트라 남는다
    public override void OnDestroy()
    {
        Clear();
        base.OnDestroy();
    }

    // 투입구 자리는 눈으로 맞춰야 한다 — 고를 때 그 자리에 토큰 크기의 원을 그려 준다
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.82f, 0.25f);
        Gizmos.DrawWireSphere(transform.TransformPoint(m_coinSlotOffset), m_coinSize * 0.5f);
    }

    private void ShowResultMessage(EAccessorySlot slot, int index, bool gained)
    {
        var message = new LocalizedString(
            k_table,
            gained ? "Shop.Gacha.Result" : "Shop.Gacha.Duplicate"
        )
        {
            Arguments = new object[] { CosmeticNames.Of(m_catalog.Get(slot, index)) },
        };

        App.UI.Toast?.Show(message, m_messageSeconds);
    }

    // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작 — JailDoor와 같은 방침
    private void ShowMessage(string key) =>
        App.UI.Toast?.Show(new LocalizedString(k_table, key), m_messageSeconds);
}

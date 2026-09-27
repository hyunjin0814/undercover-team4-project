using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상점의 치장 자판기 — 토큰 1개로 치장 하나를 무작위로 뽑아 계정 보유함에 넣는다.
/// 뽑기는 로컬 권위이고, 서버는 한 번에 한 사람만 돌리도록 차례만 관리한다.
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

    private const ulong k_noHolder = ulong.MaxValue;

    private const float k_turnTimeoutSeconds = 3f;

    private ulong m_holder = k_noHolder;

    private bool m_playing;

    private bool m_drawing;

    private bool m_awaitingTurn;

    private GameObject m_shown;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Gacha;

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

        if (!IsSpawned)
        {
            BeginDraw();
            return;
        }

        m_drawing = true;
        m_awaitingTurn = true;
        RequestTurnRpc();
        WatchTurnAsync().Forget();
    }

    /// <summary>차례를 받았다 — 여기서부터가 예전의 뽑기 본문이다(뽑고, 장부를 적고, 연출을 돌린다).</summary>
    private void BeginDraw()
    {
        if (!Roll(out EAccessorySlot slot, out int index))
        {
            Debug.LogWarning($"[{nameof(CosmeticGachaMachine)}] 뽑을 항목이 없습니다 — 카탈로그가 비었습니다 (#818 D)", this);
            EndDraw();
            return;
        }

        if (!CosmeticInventory.TrySpendToken())
        {
            EndDraw();
            return;
        }

        bool alreadyOwned = CosmeticInventory.IsOwned(m_catalog, slot, index);
        CosmeticInventory.Grant(slot, index);
        bool gained = !alreadyOwned;

        if (alreadyOwned)
            CosmeticInventory.AddTokens(1);

        App.Sound?.PlaySfx2D(m_drawSound);

        DrawAsync(slot, index, gained).Forget();

        if (IsSpawned)
            ReportDrawRpc((byte)slot, index);
    }

    /// <summary>뽑은 사람의 연출(토큰 투입 → 릴)을 재생하고, 끝나면 차례를 놓는다.</summary>
    private async UniTaskVoid DrawAsync(EAccessorySlot slot, int index, bool gained)
    {
        m_drawing = true;
        try
        {
            await PlayCoinInsertAsync();

            CosmeticGachaPanel reel = FindReel();
            if (reel != null)
            {
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
        }
        finally
        {
            EndDraw();
        }
    }

    /// <summary>토큰 한 닢이 투입구 위에서 돌다가 빨려 들어가는 연출을 재생한다.</summary>
    private async UniTask PlayCoinInsertAsync()
    {
        if (m_coinSprite == null || m_coinInsertSeconds <= 0f)
            return;

        Vector3 slot = transform.TransformPoint(m_coinSlotOffset);
        var coin = new GameObject("GachaCoin");
        var renderer = coin.AddComponent<SpriteRenderer>();
        renderer.sprite = m_coinSprite;

        float spriteWidth = m_coinSprite.bounds.size.x;
        float scale = spriteWidth > 0f ? m_coinSize / spriteWidth : m_coinSize;

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
                coin.transform.position = Vector3.Lerp(start, end, t * t);

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

    /// <summary>카탈로그 전체에서 균등하게 치장 하나를 고른다("안 씀" 제외).</summary>
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

    /// <summary>차례를 달라고 서버에 묻는다. 비어 있으면 자리표를 쥐여 주고, 남이 쥐고 있으면 사유를 돌려보낸다.</summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestTurnRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;

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
        if (m_holder == rpcParams.Receive.SenderClientId)
            m_holder = k_noHolder;
    }

    /// <summary>뽑기 한 판이 끝났다(성공·중단 무관) — 로컬 플래그를 내리고 서버 자리표를 놓는다.</summary>
    private void EndDraw()
    {
        m_drawing = false;
        m_awaitingTurn = false;

        if (IsSpawned)
            ReleaseTurnRpc();
    }

    /// <summary>답이 오지 않는 경우를 푼다 — 호스트가 나가면 Grant도 Deny도 오지 않아 기계가 영영 잠긴다.</summary>
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
            return;
        }

        if (!m_awaitingTurn)
            return;

        m_awaitingTurn = false;
        m_drawing = false;
    }

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

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportDrawRpc(byte slot, int index, RpcParams rpcParams = default)
    {
        PlayDrawRpc(
            slot,
            index,
            RpcTarget.Not(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void PlayDrawRpc(byte slot, int index, RpcParams rpcParams)
    {
        if (m_playing || m_catalog == null || IsReelSpinning())
            return;

        PlayRemoteDrawAsync((EAccessorySlot)slot, index).Forget();
    }

    /// <summary>남이 돌린 뽑기 연출을 재생하고, 릴이 멈출 시간만큼 기다렸다 모형을 띄운다.</summary>
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

        if (m_playing)
            return;

        PlayRevealAsync(slot, index).Forget();
    }

    private static CosmeticGachaPanel FindReel() =>
        App.UI.Current != null && App.UI.Current.TryGetPanel(out CosmeticGachaPanel panel)
            ? panel
            : null;

    private static bool IsReelSpinning()
    {
        CosmeticGachaPanel reel = FindReel();
        return reel != null && reel.IsSpinning;
    }

    /// <summary>캡슐이 나오고 뽑힌 치장 모형이 커지며 돌다 사라지는 연출을 재생한다.</summary>
    private async UniTask PlayRevealAsync(EAccessorySlot slot, int index)
    {
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
        }
        finally
        {
            Clear();
            m_playing = false;
        }
    }

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

    public override void OnDestroy()
    {
        Clear();
        base.OnDestroy();
    }

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

    private void ShowMessage(string key) =>
        App.UI.Toast?.Show(new LocalizedString(k_table, key), m_messageSeconds);
}

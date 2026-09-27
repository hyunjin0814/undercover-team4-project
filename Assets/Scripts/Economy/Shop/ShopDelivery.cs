using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 게임 씬 진입 시 서버가 팀 구매 목록의 이번 라운드 몫을 배달한다.
/// 소지형은 배달 상자(또는 본부 지점)로, 설치형은 본부 씬 인스턴스를 켠다.
/// </summary>
public class ShopDelivery : MonoBehaviour
{
    [Header("소지형 배달 (실내 폴백)")]
    [Tooltip("여러 개가 겹치지 않게 흩뿌리는 반경(m)")]
    [SerializeField] private float m_spreadRadius = 0.4f;

    [Tooltip("착지면으로 인정할 레이어 — 기본 Default")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("DeliveryPad가 없을 때 쓸 본부 내부 지점 — 비워 두면 이 오브젝트 위치를 쓴다")]
    [SerializeField] private Transform m_deliveryZone;

    [Header("배달 상자 (DeliveryPad용, #824)")]
    [Tooltip("DeliveryPad가 있을 때 그 위에 놓는 상자 프리팹")]
    [SerializeField] private DeliveryCrate m_cratePrefab;

    [Tooltip("라운드 시작 후 소지형 배달까지의 지연(초)")]
    [SerializeField] private float m_carriedDeliveryDelaySeconds = 5f;

    [Header("설치형 접합점 (#108)")]
    [Tooltip("본부에 배치된 설치형 씬 인스턴스들 — 구매한 것만 켜진다")]
    [SerializeField] private InstallableItem[] m_installables;

    private void Start()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsServer)
            return;

        DeliverAsync().Forget();
    }

    private async UniTaskVoid DeliverAsync()
    {
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null)
        {
            Debug.Log("[상점 배달] 구매 목록 홀더가 없어 배달 없음 (세션 밖 직접 Play)");
            return;
        }

        DeliverInstallables(purchases);

        await WaitForRoundStart();
        DeliverCarried(purchases);
    }

    private async UniTask WaitForRoundStart()
    {
        RoundManager round = App.Game.Round;
        if (round != null)
        {
            await UniTask.WaitUntil(
                () => round.Phase == RoundPhase.InProgress,
                cancellationToken: this.GetCancellationTokenOnDestroy()
            );
        }

        await UniTask.Delay(
            TimeSpan.FromSeconds(m_carriedDeliveryDelaySeconds),
            ignoreTimeScale: true,
            cancellationToken: this.GetCancellationTokenOnDestroy()
        );
    }

    private void DeliverCarried(ShopPurchases purchases)
    {
        if (purchases.Carried.Count == 0)
            return;

        if (DeliveryPad.All.Count > 0)
        {
            SpawnCrate(DeliveryPad.All[0]);
            return;
        }

        DeliverCarriedDirect(purchases);
    }

    private void SpawnCrate(DeliveryPad pad)
    {
        if (m_cratePrefab == null)
        {
            Debug.LogWarning("[상점 배달] 배달 상자 프리팹이 배정되지 않았다 — 배달 취소", this);
            return;
        }

        DeliveryCrate crate = Instantiate(m_cratePrefab, pad.ResolveCratePosition(), Quaternion.identity);
        crate.ServerScheduleLanding(NetworkManager.Singleton.ServerTime.Time + crate.DescentSeconds);
        crate.NetworkObject.Spawn(destroyWithScene: true);

        Debug.Log("[상점 배달] 배달 상자 스폰");
    }

    private void DeliverCarriedDirect(ShopPurchases purchases)
    {
        if (m_deliveryZone == null)
        {
            Debug.LogWarning("[상점 배달] 배달 지점이 배정되지 않았다 — 이 오브젝트 위치에 떨어뜨린다", this);
            m_deliveryZone = transform;
        }

        int index = 0;
        foreach (ItemBase itemPrefab in purchases.Carried)
        {
            if (itemPrefab == null)
                continue;

            Vector3 position = DeliveryScatter.Resolve(m_deliveryZone.position, index++, m_spreadRadius, m_groundMask);
            ItemBase item = Instantiate(itemPrefab, position, Quaternion.identity);
            WorldItemPickup.SettleOnGround(item.gameObject, position.y);

            item.gameObject.AddComponent<ShopDeliveredItem>().SourcePrefab = itemPrefab;

            item.NetworkObject.Spawn(destroyWithScene: false);
        }

        if (index > 0)
            Debug.Log($"[상점 배달] 소지형 {index}개를 배달 지점에 배달");
    }

    private void DeliverInstallables(ShopPurchases purchases)
    {
        foreach (EInstallable purchased in purchases.Installables)
        {
            InstallableItem instance = FindInstance(purchased);
            if (instance == null)
            {
                Debug.LogWarning($"[상점 배달] {purchased}을(를) 샀지만 씬 인스턴스가 배정되지 않았다", this);
                continue;
            }

            instance.SetInstalled(true);
            Debug.Log($"[상점 배달] 설치형 설치: {purchased}");
        }
    }

    private InstallableItem FindInstance(EInstallable id)
    {
        foreach (InstallableItem installable in m_installables)
        {
            if (installable != null && installable.Id == id)
                return installable;
        }

        return null;
    }
}

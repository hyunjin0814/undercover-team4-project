using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 힐팩 — 사용자의 체력을 회복시키는 아이템.
/// </summary>
public class HealPack : ItemBase
{
    private const int k_healAmount = 50; // 회복량

    public override void Use(GameObject target)
    {
        if (HolderHealth == null)
        {
            Debug.LogWarning("힐팩 사용 실패: 체력 컴포넌트를 찾을 수 없음", this);
            return;
        }

        if (HolderHealth.CurrentHp >= HolderHealth.MaxHp)
        {
            Debug.Log("힐팩 사용 실패: 체력이 이미 최대임", this);
            return;
        }

        if (this.HasServerAuthority())
        {
            ServerTryHeal();
            return;
        }

        if (!IsOwner) return;   // 남의 아이템에서 온 호출 방지

        RequestHealRpc();
    }

    private PlayerHealth HolderHealth   // 이 아이템을 든 플레이어의 체력 컴포넌트.
    {
        get
        {
            PlayerInteractor holder = Holder;
            return holder != null ? holder.GetComponent<PlayerHealth>() : null;
        }
    }

    private void ServerTryHeal()
    {
        if (!this.HasServerAuthority()) return;

        // HolderHealth는 접근할 때마다 다시 찾는 프로퍼티라 한 번만 읽어 쓴다.
        PlayerHealth health = HolderHealth;

        if (health == null)
        {
            Debug.LogWarning("힐팩 사용 실패: 체력 컴포넌트를 찾을 수 없음", this);
            return;
        }

        if (health.CurrentHp >= health.MaxHp)
        {
            Debug.Log("힐팩 사용 실패: 체력이 이미 최대임", this);
            return;
        }

        health.ModifyHp(k_healAmount);

        // 회복이 실제로 일어난 뒤에만 낸다 — 위 두 실패 경로(홀더 없음·체력 만땅)에서 울리면
        // 아무 일도 없었는데 회복된 것처럼 들린다. 3D로 전 피어에 나가므로 옆 사람도 듣는다.
        App.Game.Fx?.PlayEverywhere(EFx.HealPackUse, health.transform.position);

        ServerConsume();
    }

    [Rpc(SendTo.Server)]
    private void RequestHealRpc()
    {
        ServerTryHeal();
    }
}

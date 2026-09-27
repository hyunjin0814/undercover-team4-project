using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 힐팩 — 사용자의 체력을 회복시키는 아이템.
/// </summary>
public class HealPack : ItemBase
{
    private const int k_healAmount = 50;

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

        if (!IsOwner) return;

        RequestHealRpc();
    }

    private PlayerHealth HolderHealth
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

        App.Game.Fx?.PlayEverywhere(EFx.HealPackUse, health.transform.position);

        ServerConsume();
    }

    [Rpc(SendTo.Server)]
    private void RequestHealRpc()
    {
        ServerTryHeal();
    }
}

using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오너 로컬 전용 — PlayerInteractor.ProbeTarget으로 조준한 NPC의 체력 바만 켠다.
/// 싸움이 끝난 대상(사망·밧줄 묶임)은 제외한다.
/// </summary>
public class NpcHealthBarPresenter : NetworkBehaviour
{
    private PlayerInteractor m_interactor;

    private NpcHealthBarView m_current;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_interactor = GetComponentInParent<PlayerInteractor>();
        if (m_interactor == null)
            Debug.LogWarning("NpcHealthBarPresenter: PlayerInteractor를 찾지 못해 체력 바를 띄울 수 없다", this);
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
            HideCurrent();
    }

    private void OnDisable() => HideCurrent();

    private void Update()
    {
        Show(Resolve(m_interactor != null ? m_interactor.ProbeTarget : null));
    }

    private NpcHealthBarView Resolve(GameObject target)
    {
        if (target == null)
            return null;

        NpcController npc = target.GetComponentInParent<NpcController>();
        if (npc == null)
            return null;

        if (IsOutOfFight(npc))
            return null;

        return npc.GetComponentInChildren<NpcHealthBarView>(true);
    }

    /// <summary>죽었거나 밧줄에 묶여 더는 싸우지 않는 대상인지 판정한다(기절은 제외).</summary>
    private bool IsOutOfFight(NpcController npc) => npc.Death.IsDead || npc.Rope.IsRoped;

    private void Show(NpcHealthBarView view)
    {
        if (view == m_current)
            return;

        HideCurrent();

        if (view == null)
            return;

        if (m_interactor.AimCamera != null)
            view.SetCamera(m_interactor.AimCamera.transform);

        view.Show();
        m_current = view;
    }

    private void HideCurrent()
    {
        if (m_current != null)
            m_current.Hide();

        m_current = null;
    }
}

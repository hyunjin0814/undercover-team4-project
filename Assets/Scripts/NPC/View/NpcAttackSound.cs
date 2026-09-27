using System.Collections;
using UnityEngine;

/// <summary>
/// NPC 근접 공격의 스윙음(타격 시점에 맞춰 지연)과 명중음을 3D로 재생한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcAttackSound : MonoBehaviour
{
    [Tooltip("공격이 닿기 이만큼(초) 전에 스윙음을 낸다 — 휙 소리가 타격음으로 이어지게 하는 값. " +
             "0이면 타격과 동시, 크게 잡으면 모션 시작 쪽으로 당겨진다")]
    [Min(0f)]
    [SerializeField] private float m_swingLeadSeconds = 0.18f;

    private NpcController m_controller;

    private void Awake() => m_controller = GetComponent<NpcController>();

    private void OnEnable()
    {
        m_controller.Reaction.OnAttackSwing += HandleSwing;
        m_controller.Reaction.OnAttackHit += HandleHit;
    }

    private void OnDisable()
    {
        m_controller.Reaction.OnAttackSwing -= HandleSwing;
        m_controller.Reaction.OnAttackHit -= HandleHit;

        StopAllCoroutines();
    }

    private void HandleSwing(int variant)
    {
        float delay = m_controller.ResistConfig.SwingImpactOffset(variant) - m_swingLeadSeconds;
        if (delay <= 0f)
        {
            PlaySwing();
            return;
        }

        StartCoroutine(PlaySwingAfter(delay));
    }

    private IEnumerator PlaySwingAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        PlaySwing();
    }

    private void PlaySwing() => App.Sound?.PlaySfxAt(EAudioClip.NpcAttackSwing, transform.position);

    private void HandleHit() => App.Sound?.PlaySfxAt(EAudioClip.NpcAttackHitRobot, transform.position);
}

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 본부 밖 배달 지점 마커. ShopDelivery가 씬에서 찾아 배달 상자를 놓는다.
/// </summary>
public class DeliveryPad : MonoBehaviour
{
    public static readonly List<DeliveryPad> All = new List<DeliveryPad>();

    [Tooltip("아이템이 놓일 기준점 — 비우면 이 오브젝트 위치를 쓴다")]
    [SerializeField]
    private Transform m_deliveryPoint;

    [Tooltip("여러 개가 겹치지 않게 흩뿌리는 반경(m)")]
    [SerializeField]
    private float m_spreadRadius = 0.5f;

    [Tooltip("착지면으로 인정할 레이어 — 기본 Default")]
    [SerializeField]
    private LayerMask m_groundMask = 1;

    [Header("강하 연출 (#824)")]
    [Tooltip("상자가 내려오기 시작하는 높이(m)")]
    [SerializeField]
    private float m_descentHeight = 6f;

    [Tooltip("연출용 드론 모델 — 비우면 드론 없이 상자만 내려온다")]
    [SerializeField]
    private GameObject m_droneModel;

    [Tooltip("드론이 상자를 매단 채 떠 있는 높이(m, 상자 기준)")]
    [SerializeField]
    private float m_droneHoverOffset = 1.5f;

    [Tooltip("상자를 내려놓은 뒤 드론이 다시 떠올라 사라지는 시간(초)")]
    [SerializeField]
    private float m_ascendSeconds = 1f;

    [Tooltip("하강·상승 진행 곡선 — 기본은 부드럽게 가속했다 감속한다")]
    [SerializeField]
    private AnimationCurve m_ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    public Transform DeliveryPoint => m_deliveryPoint != null ? m_deliveryPoint : transform;

    public Vector3 ResolveItemPosition(int index) =>
        DeliveryScatter.Resolve(DeliveryPoint.position, index, m_spreadRadius, m_groundMask);

    public Vector3 ResolveCratePosition() =>
        DeliveryScatter.SnapToGround(DeliveryPoint.position, m_groundMask);

    public void PlayDroneDelivery(Transform crate, float landSeconds) =>
        PlayDroneDeliveryAsync(crate, landSeconds).Forget();

    private async UniTaskVoid PlayDroneDeliveryAsync(Transform crate, float landSeconds)
    {
        if (crate == null)
            return;

        Vector3 landedPosition = ResolveCratePosition();
        Vector3 startPosition = landedPosition + Vector3.up * m_descentHeight;
        var cancellation = crate.gameObject.GetCancellationTokenOnDestroy();
        Transform drone = m_droneModel != null ? m_droneModel.transform : null;

        if (drone != null)
            m_droneModel.SetActive(true);

        App.Sound?.PlaySfxAt(EAudioClip.DroneApproach, startPosition);

        float elapsed = 0f;
        while (elapsed < landSeconds)
        {
            Vector3 position = Vector3.Lerp(
                startPosition,
                landedPosition,
                m_ease.Evaluate(elapsed / landSeconds)
            );
            crate.position = position;
            if (drone != null)
                drone.position = position + Vector3.up * m_droneHoverOffset;

            await UniTask.Yield(PlayerLoopTiming.Update, cancellation);
            elapsed += Time.deltaTime;
        }

        crate.position = landedPosition;
        App.Sound?.PlaySfxAt(EAudioClip.CrateLand, landedPosition);

        if (drone == null)
            return;

        try
        {
            Vector3 hoverPosition = landedPosition + Vector3.up * m_droneHoverOffset;
            Vector3 exitPosition = startPosition + Vector3.up * m_droneHoverOffset;
            float ascendElapsed = 0f;
            while (ascendElapsed < m_ascendSeconds)
            {
                drone.position = Vector3.Lerp(
                    hoverPosition,
                    exitPosition,
                    m_ease.Evaluate(ascendElapsed / m_ascendSeconds)
                );
                await UniTask.Yield(PlayerLoopTiming.Update, cancellation);
                ascendElapsed += Time.deltaTime;
            }
        }
        finally
        {
            m_droneModel.SetActive(false);
        }
    }

    private void OnEnable()
    {
        All.Add(this);
    }

    private void OnDisable()
    {
        All.Remove(this);
    }
}

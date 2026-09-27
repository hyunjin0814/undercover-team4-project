using UnityEngine;

/// <summary>
/// 플레이어 시점 튜닝 수치를 모은 SO.
/// </summary>
[CreateAssetMenu(fileName = "PlayerLookConfig", menuName = "Scriptable Objects/PlayerLookConfig")]
public class PlayerLookConfig : ScriptableObject
{
    [Header("1인칭 시점")]
    [Tooltip("기준 감도(도/픽셀) — 여기에 설정 창의 배율을 곱한다. 실제 속도를 정하는 것은 이 값이고, "
        + "배율은 '기본보다 몇 배'만 고른다 (#225/#665)")]
    [SerializeField] private float m_mouseSensitivity = 0.08f;

    [SerializeField] private float m_minPitch = -80f;
    [SerializeField] private float m_maxPitch = 80f;

    [Header("다운(무력화) 시점")]
    [Tooltip("다운 중 카메라를 낮출 바닥 근처 높이(m)")]
    [SerializeField] private float m_downCamHeight = 0.35f;

    [Tooltip("다운 중 카메라 피치(양수=아래, 음수=위). 바닥에서 살짝 위를 보게 함")]
    [SerializeField] private float m_downCamPitch = -20f;

    [Tooltip("서기↔다운 시점 전환 보간 속도. 클수록 빨리 붙는다 — 낮으면 화면이 길게 미끄러져 멀미가 난다 (#665)")]
    [SerializeField] private float m_camPoseLerpSpeed = 14f;

    [Tooltip("쓰러진 동안(다운·기절) 시야를 좌우로 돌릴 수 있는 범위(±도)")]
    [SerializeField] private float m_downYawRange = 100f;

    [Tooltip("쓰러진 동안 시야 피치 하한(음수=위)")]
    [SerializeField] private float m_downMinPitch = -80f;

    [Tooltip("쓰러진 동안 시야 피치 상한(양수=아래)")]
    [SerializeField] private float m_downMaxPitch = 20f;

    [Header("감정표현 시점 (#219)")]
    [Tooltip("감정표현 재생 중 카메라를 뒤로 뺄 거리(m)")]
    [SerializeField] private float m_emoteCamDistance = 2.5f;

    [Tooltip("감정표현 재생 중 카메라를 위로 올릴 높이(m)")]
    [SerializeField] private float m_emoteCamHeight = 0.4f;

    [Tooltip("1인칭↔감정표현 시점 전환 보간 속도. 클수록 빨리 붙는다 (#665)")]
    [SerializeField] private float m_emoteCamLerpSpeed = 10f;

    [Tooltip("3인칭 카메라가 벽을 파고들지 않게 띄울 반경(m)")]
    [SerializeField] private float m_emoteCamProbeRadius = 0.25f;

    public float MouseSensitivity => m_mouseSensitivity;
    public float MinPitch => m_minPitch;
    public float MaxPitch => m_maxPitch;
    public float DownCamHeight => m_downCamHeight;
    public float DownCamPitch => m_downCamPitch;
    public float CamPoseLerpSpeed => m_camPoseLerpSpeed;
    public float DownYawRange => m_downYawRange;
    public float DownMinPitch => m_downMinPitch;
    public float DownMaxPitch => m_downMaxPitch;
    public float EmoteCamDistance => m_emoteCamDistance;
    public float EmoteCamHeight => m_emoteCamHeight;
    public float EmoteCamLerpSpeed => m_emoteCamLerpSpeed;
    public float EmoteCamProbeRadius => m_emoteCamProbeRadius;
}

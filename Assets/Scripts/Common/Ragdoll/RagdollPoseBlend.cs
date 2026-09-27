using UnityEngine;

/// <summary>
/// 저장한 포즈에서 현재 애니메이터 포즈로 부드럽게 보간하는 부활 블렌드.
/// LateUpdate에서 매 프레임 목표 포즈를 다시 읽어 재생 중인 기상 클립에 수렴한다.
/// </summary>
public class RagdollPoseBlend
{
    private readonly Transform[] m_bones;
    private readonly Vector3[] m_fromPositions;
    private readonly Quaternion[] m_fromRotations;

    private float m_timer;

    public bool IsValid => m_bones != null && m_bones.Length > 0;

    public RagdollPoseBlend(Transform boneRoot)
    {
        m_bones =
            boneRoot != null ? boneRoot.GetComponentsInChildren<Transform>(true) : new Transform[0];

        m_fromPositions = new Vector3[m_bones.Length];
        m_fromRotations = new Quaternion[m_bones.Length];
    }

    /// <summary>지금 포즈를 출발점으로 잡는다 — 애니메이터가 덮어쓰기 전에 부른다.</summary>
    public void Begin()
    {
        for (int i = 0; i < m_bones.Length; i++)
        {
            m_fromPositions[i] = m_bones[i].localPosition;
            m_fromRotations[i] = m_bones[i].localRotation;
        }

        m_timer = 0f;
    }

    /// <summary>블렌드 한 프레임 — LateUpdate에서 부른다 (클래스 주석의 이유).</summary>
    public bool Tick(float blendSeconds)
    {
        m_timer += Time.deltaTime;
        float t = blendSeconds <= 0f ? 1f : Mathf.Clamp01(m_timer / blendSeconds);

        for (int i = 0; i < m_bones.Length; i++)
        {
            m_bones[i].localRotation = Quaternion.Slerp(
                m_fromRotations[i],
                m_bones[i].localRotation,
                t
            );
            m_bones[i].localPosition = Vector3.Lerp(
                m_fromPositions[i],
                m_bones[i].localPosition,
                t
            );
        }

        return t >= 1f;
    }
}

using System;
using UnityEngine;

/// <summary>
/// 몽타주 화질 한 단계 — 표시 픽셀 크기 + 대비 저하.
/// </summary>
[Serializable]
public struct MontageClarityStep
{
    [Tooltip("뭉갤 목표 픽셀 크기 — 정사각 블록 평균. 베이스 해상도보다 크면 무시된다")]
    [Min(1)]
    public int PixelSize;

    [Tooltip("RGB를 중앙 회색으로 당기는 정도. 알파는 건드리지 않는다 — 건드리면 미공개 표시(m_unknownTint)를 사칭한다")]
    [Range(0f, 1f)]
    public float Fade;
}

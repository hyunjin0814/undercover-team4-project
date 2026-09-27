using UnityEngine;

/// <summary>
/// 자기 사거리로 조준하는 무기 표식 — NPC 윤곽선 대신 크로스헤어 색으로 명중 가능 여부를 알린다.
/// </summary>
public interface IAimedWeapon
{
    /// <summary>지금 이 조준선으로 유효한 대상을 겨누고 있는가 — 오너 크로스헤어 색 예측용.</summary>
    bool HasValidAimTarget(Vector3 origin, Vector3 direction);
}

using UnityEngine;

/// <summary>
/// NPC 외형 조합(AppearanceProfile) 공급자 인터페이스 — 생산 방식과 무관하게 소비 측이 쓴다.
/// </summary>
public interface IAppearanceProfileSource
{
    AppearanceProfile Profile { get; }
}

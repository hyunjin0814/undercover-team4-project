using System;
using UnityEngine;

/// <summary>
/// 부착 지점의 자식이 바뀌는 순간을 전 피어 로컬 이벤트로 알린다. PlayerLoadout이 런타임에 붙인다.
/// </summary>
public class HeldItemsWatcher : MonoBehaviour
{
    public event Action OnChanged;

    private void OnTransformChildrenChanged() => OnChanged?.Invoke();
}

using UnityEngine;

/// <summary>
/// 상점에서 산 물건임을 나타내는 표식. 잃어버렸을 때 팀 구매 목록에서 뺄 항목을 가른다.
/// </summary>
public class ShopDeliveredItem : MonoBehaviour
{
    public ItemBase SourcePrefab { get; set; }
}

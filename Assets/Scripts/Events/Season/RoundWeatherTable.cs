using UnityEngine;

/// <summary>
/// 라운드별 날씨 확률 표 SO — 맑음 확률과 날씨별 가중치를 담는다.
/// 표 끝을 넘으면 마지막 행을 유지하며, 맵마다 다른 표를 꽂을 수 있다.
/// </summary>
[CreateAssetMenu(
    fileName = "RoundWeatherTable",
    menuName = "Undercover/Events/Round Weather Table"
)]
public class RoundWeatherTable : ScriptableObject
{
    [Tooltip(
        "라운드 순서대로 나열한 맑음 확률(%). 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드.\n\n"
            + "나머지 확률을 아래 가중치대로 날씨 3종이 나눠 갖는다"
    )]
    [Range(0, 100)]
    [SerializeField]
    private int[] m_clearPercents = { 70, 50, 30, 20 };

    [System.Serializable]
    private class WeatherWeight
    {
        public WeatherKind kind;

        [Tooltip("맑음이 아닐 때 이 날씨가 뽑힐 상대 가중치 — 0이면 이 맵에서는 나오지 않는다")]
        [Min(0f)]
        public float weight = 1f;
    }

    [Tooltip(
        "날씨별 상대 가중치. 목록에 없는 날씨는 1(균등)로 친다 — 차등을 줄 것만 적으면 된다.\n\n"
            + "예: 실내 위주 맵에서 눈을 0으로 두면 그 맵에는 눈이 오지 않는다"
    )]
    [SerializeField]
    private WeatherWeight[] m_weights = new WeatherWeight[0];

    public bool HasRows => m_clearPercents != null && m_clearPercents.Length > 0;

    /// <summary>N라운드의 맑음 확률(%)을 돌려준다. 표가 비었으면 fallback.</summary>
    public int GetClearPercent(int round, int fallback)
    {
        if (!HasRows)
            return fallback;

        int index = Mathf.Clamp(
            round - RoundProgress.k_firstRound,
            0,
            m_clearPercents.Length - 1
        );
        return m_clearPercents[index];
    }

    /// <summary>날씨의 상대 가중치를 돌려준다. 목록에 없으면 1.</summary>
    public float WeightOf(WeatherKind kind)
    {
        if (m_weights == null)
            return 1f;

        for (int i = 0; i < m_weights.Length; i++)
            if (m_weights[i] != null && m_weights[i].kind == kind)
                return Mathf.Max(0f, m_weights[i].weight);

        return 1f;
    }
}

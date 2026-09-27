using System;
using UnityEngine;

/// <summary>
/// 맵 격자 생성기가 읽는 팔레트 SO — 레이아웃 글자마다 깔 프리팹을 담는다.
/// Create ▸ Map ▸ Map Palette로 만든다. 에디터 전용 데이터다.
/// </summary>
[CreateAssetMenu(fileName = "MapPalette", menuName = "Map/Map Palette")]
public class MapPalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("레이아웃 텍스트에서 이 규칙을 가리키는 글자 한 자")]
        [SerializeField]
        private string m_symbol = ".";

        [Tooltip("생성물을 담을 자식 오브젝트 이름 — 같은 이름을 쓴 규칙끼리 한 곳에 모인다")]
        [SerializeField]
        private string m_group = "Tiles";

        [Tooltip("이 칸에 놓을 프리팹 후보. 둘 이상이면 무작위로 하나 고른다")]
        [SerializeField]
        private GameObject[] m_prefabs;

        [Tooltip(
            "먼저 밑에 깔 프리팹 후보 — 비우면 깔지 않는다.\n"
                + "흙처럼 가장자리가 둥글어 타일끼리 안 물리는 것 밑에 포장을 받쳐 구멍이 안 뚫리게 하는 용도."
        )]
        [SerializeField]
        private GameObject[] m_underlayPrefabs;

        [Tooltip(
            "아래 '연석이 향하는 기호'에 한 면 접할 때 쓸 프리팹 — 비우면 방향 맞추기를 하지 않는다.\n"
                + "연석이 로컬 +Z를 향한 프리팹을 넣을 것. 생성기가 접한 방향으로 돌려준다."
        )]
        [SerializeField]
        private GameObject[] m_edgePrefabs;

        [Tooltip("두 면 접할 때(바깥 모서리) 쓸 프리팹 — 로컬 +Z·+X 두 면이 도로 쪽인 프리팹을 넣을 것")]
        [SerializeField]
        private GameObject[] m_cornerPrefabs;

        [Tooltip("높이 보정(m) — 두께가 있는 타일이 도로면과 이가 안 맞을 때 조정한다")]
        [SerializeField]
        private float m_yOffset;

        [Tooltip("배치 배율 — 가장자리가 둥근 타일을 조금 키워 서로 물리게 할 때 쓴다")]
        [SerializeField]
        private float m_scale = 1f;

        [Tooltip("켜면 90도 단위로 무작위 회전한다. 방향 맞추기가 걸린 칸에는 적용되지 않는다")]
        [SerializeField]
        private bool m_randomYaw;

        public char Symbol => string.IsNullOrEmpty(m_symbol) ? '\0' : m_symbol[0];

        public string Group => string.IsNullOrEmpty(m_group) ? "Tiles" : m_group;
        public GameObject[] Prefabs => m_prefabs;
        public GameObject[] UnderlayPrefabs => m_underlayPrefabs;
        public GameObject[] EdgePrefabs => m_edgePrefabs;
        public GameObject[] CornerPrefabs => m_cornerPrefabs;
        public float YOffset => m_yOffset;
        public float Scale => m_scale;
        public bool RandomYaw => m_randomYaw;

        public bool UsesEdgeFacing => m_edgePrefabs != null && m_edgePrefabs.Length > 0;
    }

    [Serializable]
    public class WallSettings
    {
        [Tooltip("경계로 쓸 레이아웃 글자들. 이 글자 칸과 맞닿은 '안쪽 칸' 사이 모서리에 벽이 선다")]
        [SerializeField]
        private string m_symbols = "#";

        [Tooltip("아래 조각 — 로컬 +X가 두께, 로컬 -Z가 길이(한 칸)인 프리팹")]
        [SerializeField]
        private GameObject m_bottom;

        [Tooltip("도로가 벽에 닿는 자리에 세울 게이트 — 비우면 벽이 이어진다. 벽 조각과 같은 축 규약을 따를 것")]
        [SerializeField]
        private GameObject m_gate;

        [Tooltip(
            "게이트 배치 배율. Z가 길이(칸 방향)라 개구부 폭에 맞춰 늘린다.\n"
                + "격리벽 게이트 기준 (1, 1.55, 1.8) — 문짝이 원본 그대로면 벽에 비해 낮고 좁아 보인다."
        )]
        [SerializeField]
        private Vector3 m_gateScale = new Vector3(1f, 1.55f, 1.8f);

        [Tooltip("게이트를 벽면보다 더 안쪽으로 당기는 거리(m) — 벽에 파묻히지 않게 앞으로 내놓는다")]
        [SerializeField]
        private float m_gateInset = 0.5f;

        [Tooltip("위 조각 — 비워도 된다")]
        [SerializeField]
        private GameObject m_top;

        [Tooltip("위 조각을 올릴 높이(m)")]
        [SerializeField]
        private float m_topY = 5f;

        [Tooltip("맨 위 장식(철조망 등) — 비워도 된다")]
        [SerializeField]
        private GameObject m_cap;

        [Tooltip("맨 위 장식을 올릴 높이(m)")]
        [SerializeField]
        private float m_capY = 7.92f;

        [Tooltip("한 짝 양 끝에 세울 기둥 — 비워도 된다. 같은 자리에 두 번 서지 않게 생성기가 걸러준다")]
        [SerializeField]
        private GameObject m_column;

        [Tooltip("벽면을 칸 경계보다 안쪽으로 당기는 거리(m) — 바닥 타일 옆면이 비치는 것을 가린다. 기둥엔 미적용")]
        [SerializeField]
        private float m_inset = 0.3f;

        [Header("물리 껍질")]
        [Tooltip("켜면 조각 콜라이더를 끄고 아래 치수의 BoxCollider로 막는다. 끄면 프리팹 콜라이더를 그대로 쓴다")]
        [SerializeField]
        private bool m_useBoxCollider = true;

        [Tooltip("벽 두께(m) — 아래 조각의 로컬 +X 방향 두께와 맞출 것")]
        [SerializeField]
        private float m_thickness = 1.1f;

        [Tooltip("벽 전체 높이(m) — 조각을 다 쌓은 높이")]
        [SerializeField]
        private float m_height = 7.95f;

        [Tooltip("기둥 한 변(m)")]
        [SerializeField]
        private float m_columnWidth = 0.9f;

        public string Symbols => string.IsNullOrEmpty(m_symbols) ? "#" : m_symbols;
        public GameObject Bottom => m_bottom;
        public GameObject Gate => m_gate;
        public Vector3 GateScale => m_gateScale;
        public float GateInset => m_gateInset;
        public GameObject Top => m_top;
        public float TopY => m_topY;
        public GameObject Cap => m_cap;
        public float CapY => m_capY;
        public GameObject Column => m_column;
        public float Inset => m_inset;
        public bool UseBoxCollider => m_useBoxCollider;
        public float Thickness => m_thickness;
        public float Height => m_height;
        public float ColumnWidth => m_columnWidth;

        public bool HasAnyPiece => m_bottom != null || m_top != null || m_cap != null || m_column != null;

        public bool IsWallSymbol(char symbol) => Symbols.IndexOf(symbol) >= 0;
    }

    [Header("레이아웃")]
    [Tooltip("배치도 텍스트. 첫 줄이 북쪽(z 최대)이고, '//'로 시작하는 줄과 빈 줄은 주석이다")]
    [SerializeField]
    private TextAsset m_layout;

    [Tooltip("한 칸의 한 변(m). 에셋 팩의 도로·보도 모듈 크기에 맞춘다 — Synty Polygon 계열은 보통 5m")]
    [SerializeField]
    private float m_cellSize = 5f;

    [Tooltip("무작위 선택·회전에 쓰는 시드. 같은 팔레트와 레이아웃이면 몇 번을 돌려도 같은 결과가 나온다")]
    [SerializeField]
    private int m_seed = 215;

    [Header("배치 규칙")]
    [Tooltip("연석(가장자리 프리팹)이 바라볼 글자들 — 보통 도로·횡단보도")]
    [SerializeField]
    private string m_edgeFacingSymbols = "RC";

    [Tooltip("글자별 배치 규칙. 여기 없는 글자는 그냥 넘어간다(공백으로 비워두는 것과 같다)")]
    [SerializeField]
    private Entry[] m_entries;

    [Header("경계벽")]
    [SerializeField]
    private WallSettings m_wall = new WallSettings();

    public TextAsset Layout => m_layout;
    public float CellSize => m_cellSize;
    public int Seed => m_seed;
    public Entry[] Entries => m_entries;
    public WallSettings Wall => m_wall;

    /// <summary>연석이 바라볼 대상인가 — 도로 쪽 글자.</summary>
    public bool IsEdgeFacing(char symbol) =>
        !string.IsNullOrEmpty(m_edgeFacingSymbols) && m_edgeFacingSymbols.IndexOf(symbol) >= 0;

    /// <summary>글자에 대응하는 규칙 — 없으면 null.</summary>
    public Entry Find(char symbol)
    {
        if (m_entries == null)
        {
            return null;
        }

        foreach (Entry entry in m_entries)
        {
            if (entry != null && entry.Symbol == symbol)
            {
                return entry;
            }
        }

        return null;
    }
}

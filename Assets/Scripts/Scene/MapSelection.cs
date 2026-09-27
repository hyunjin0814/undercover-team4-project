using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 다음 라운드 게임 맵 선택 상태를 들고 있는 세션 상주 홀더.
/// 맵 목록은 인스펙터 배열이 정본이며 선택 인덱스만 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class MapSelection : NetworkedManagerBase
{
    [Serializable]
    public class Entry
    {
        [Tooltip(
            "로드할 씬 이름. EditorBuildSettings에 등록돼 있어야 한다 — NGO 씬 동기화가 이름으로 로드한다"
        )]
        public string SceneName;

        [Tooltip("콘솔에 표시할 이름. 비우면 씬 이름을 그대로 쓴다")]
        public LocalizedString DisplayName;

        [Tooltip(
            "콘솔에 띄울 항공뷰 이미지. 에디터에서 맵 위에 직교 카메라를 놓고 한 번 렌더해 둔 스프라이트다"
                + " — 런타임 생성이 아니다. 비우면 이미지 없이 이름만 뜬다"
        )]
        public Sprite Preview;

        [Tooltip("이 맵에 스폰될 NPC 수(표시용 수동 값). 맵 씬 NpcSpawner의 스폰 수와 맞출 것")]
        [Min(0)]
        public int NpcCount;

        [Tooltip(
            "미리보기를 90도 눕혀서 보여준다 — 세로로 긴 맵을 가로 프레임에 크게 담을 때 켠다."
                + " 본부 미니맵도 같은 맵을 눕혀 띄우고 있어야 두 화면의 방향이 어긋나지 않는다"
        )]
        public bool PreviewRotated;
    }

    [Tooltip("고를 수 있는 맵 목록 — 순서가 곧 콘솔의 이전/다음 순서다. 첫 칸이 기본 선택")]
    [SerializeField]
    private Entry[] m_maps;

    private readonly NetworkVariable<int> m_selectedIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public int MapCount => m_maps != null ? m_maps.Length : 0;
    public int SelectedIndex => m_selectedIndex.Value;

    public event Action OnSelectionChanged;

    public string SelectedSceneName => NullIfBlank(Selected?.SceneName);

    public string SelectedDisplayName
    {
        get
        {
            LocalizedString name = Selected?.DisplayName;
            return name != null && !name.IsEmpty
                ? NullIfBlank(name.GetLocalizedString()) ?? SelectedSceneName
                : SelectedSceneName;
        }
    }

    public Sprite SelectedPreview => Selected?.Preview;

    public int SelectedNpcCount => Selected?.NpcCount ?? 0;

    public bool SelectedPreviewRotated => Selected?.PreviewRotated ?? false;

    private Entry Selected =>
        m_maps != null && SelectedIndex >= 0 && SelectedIndex < m_maps.Length
            ? m_maps[SelectedIndex]
            : null;

    private static string NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public override void OnNetworkSpawn()
    {
        if (IsServer && SaveService.Pending != null)
        {
            int saved = SaveService.Pending.MapIndex;
            m_selectedIndex.Value = saved >= 0 && saved < MapCount ? saved : 0;
        }

        m_selectedIndex.OnValueChanged += HandleSelectedIndexChanged;
        OnSelectionChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_selectedIndex.OnValueChanged -= HandleSelectedIndexChanged;
    }

    private void HandleSelectedIndexChanged(int previous, int current) =>
        OnSelectionChanged?.Invoke();

    /// <summary>목록 이동 요청 — 이전/다음 버튼이 부른다. delta는 -1 또는 +1.</summary>
    [Rpc(SendTo.Server)]
    public void RequestSelectRpc(int delta)
    {
        int count = MapCount;
        if (count == 0 || delta == 0)
            return;

        if (!IsSelectable)
            return;

        m_selectedIndex.Value = ((m_selectedIndex.Value + delta) % count + count) % count;
    }

    public static bool IsSelectable =>
        App.SceneFlow.Shop != null && !App.SceneFlow.Shop.IsDispatched;
}

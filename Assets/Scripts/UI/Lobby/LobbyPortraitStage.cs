using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 로비·상점 명단 카드용 얼굴을 무대 캐릭터로 렌더해 RenderTexture로 굽는다(색·치장 조합 단위).
/// 첫 프레임이 끝난 뒤 굽는다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIContent)]
public class LobbyPortraitStage : MonoBehaviour
{
    [Header("무대")]
    [Tooltip("얼굴을 딸 캐릭터 모델 — 플레이어와 같은 것을 쓴다 (SM_Gen_Chr_Robot_01)")]
    [SerializeField] private GameObject m_characterPrefab;

    [Tooltip("색 팔레트 — Player 프리팹의 PlayerCosmetics와 같은 에셋을 물릴 것 (#432)")]
    [SerializeField] private PlayerColorPalette m_palette;

    [Tooltip("치장 카탈로그 — Player 프리팹의 PlayerAccessories와 같은 에셋을 물릴 것 (#818)")]
    [SerializeField] private AccessoryCatalog m_accessoryCatalog;

    [Tooltip("무대를 세울 위치 — 씬의 다른 것과 겹치지 않게 멀리 둔다")]
    [SerializeField] private Vector3 m_stageOrigin = new Vector3(0f, -500f, 0f);

    [Tooltip("머리 본 이름 — 이 본을 정면에서 잡는다")]
    [SerializeField] private string m_headBoneName = "Head";

    [Header("카메라")]
    [Tooltip("머리에서 이만큼(m) 떨어져서 잡는다 — 작을수록 얼굴이 꽉 찬다")]
    [SerializeField] private float m_distance = 0.55f;

    [Tooltip("머리 본 기준 위아래 보정(m) — 본이 목에 있으면 살짝 올려야 얼굴이 가운데 온다")]
    [SerializeField] private float m_heightOffset = 0.06f;

    [Tooltip("시야각 — 작을수록 왜곡이 적다")]
    [Range(10f, 60f)]
    [SerializeField] private float m_fieldOfView = 28f;

    [Tooltip("초상 텍스처 크기(px) — 카드 사진 창과 같은 세로 비율로 굽는다. 정사각으로 구워 창에 늘리면 얼굴이 눌린다")]
    [SerializeField] private Vector2Int m_textureSize = new Vector2Int(256, 348);

    [Header("전신 미리보기 (#432)")]
    [Tooltip("발끝에서 이만큼(m) 뒤로 물러나 전신을 잡는다")]
    [SerializeField] private float m_bodyDistance = 3.4f;

    [Tooltip("카메라 높이(m) — 몸 가운데쯤")]
    [SerializeField] private float m_bodyHeight = 0.95f;

    [Range(10f, 60f)]
    [SerializeField] private float m_bodyFieldOfView = 32f;

    [SerializeField] private Vector2Int m_bodyTextureSize = new Vector2Int(384, 640);

    public readonly struct PortraitKey : System.IEquatable<PortraitKey>
    {
        public readonly PlayerColorSet Colors;
        public readonly AccessorySet Accessories;

        public PortraitKey(PlayerColorSet colors, AccessorySet accessories)
        {
            Colors = colors;
            Accessories = accessories;
        }

        public static PortraitKey Mine =>
            new PortraitKey(PlayerColorSet.FromSettings(), AccessorySet.FromSettings());

        public bool Equals(PortraitKey other) =>
            Colors.Equals(other.Colors) && Accessories.Equals(other.Accessories);

        public override bool Equals(object obj) => obj is PortraitKey other && Equals(other);

        public override int GetHashCode() => (Colors.Key * 397) ^ Accessories.GetHashCode();
    }

    private readonly Dictionary<PortraitKey, RenderTexture> m_portraits =
        new Dictionary<PortraitKey, RenderTexture>();
    private readonly HashSet<PortraitKey> m_pending = new HashSet<PortraitKey>();
    private readonly HashSet<PortraitKey> m_live = new HashSet<PortraitKey>();
    private readonly List<PortraitKey> m_stale = new List<PortraitKey>();

    private Camera m_camera;
    private Camera m_bodyCamera;
    private RenderTexture m_bodyTexture;
    private bool m_bodyDirty = true;
    private BodyTint m_tint;
    private Transform m_stageHead;
    private readonly GameObject[] m_accessories = new GameObject[System.Enum.GetValues(
        typeof(EAccessorySlot)
    ).Length];
    private readonly GameObject[] m_worn = new GameObject[System.Enum.GetValues(
        typeof(EAccessorySlot)
    ).Length];
    private readonly Color[] m_tintBuffer = new Color[3];
    private bool m_baking;
    private bool m_lit;
    private SessionRoster m_roster;

    private static readonly Dictionary<PortraitKey, Texture2D> s_sessionPortraits =
        new Dictionary<PortraitKey, Texture2D>();

    public Texture Portrait => GetPortrait(PortraitKey.Mine);

    public Texture BodyPreview
    {
        get
        {
            if (m_bodyCamera == null)
                return null;

            m_bodyDirty = true;
            BakeAsync().Forget();
            return m_bodyTexture;
        }
    }

    public static Texture SessionPortrait => GetSessionPortrait(PortraitKey.Mine);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetStatics() => s_sessionPortraits.Clear();

    /// <summary>그 조합으로 로비에서 구워 둔 얼굴 — 게임 씬 UI가 읽는다. 없으면 null.</summary>
    public static Texture GetSessionPortrait(PlayerColorSet colors, AccessorySet accessories) =>
        GetSessionPortrait(new PortraitKey(colors, accessories));

    public static Texture GetSessionPortrait(PortraitKey key) =>
        s_sessionPortraits.TryGetValue(key, out Texture2D portrait) ? portrait : null;

    /// <summary>색·치장 조합의 얼굴 텍스처를 돌려준다. 처음 요청이면 빈 텍스처를 먼저 주고 곧 채운다. 무대가 없으면 null.</summary>
    public Texture GetPortrait(PlayerColorSet colors, AccessorySet accessories) =>
        GetPortrait(new PortraitKey(colors, accessories));

    public Texture GetPortrait(PortraitKey key)
    {
        if (m_camera == null)
            return null;

        if (m_portraits.TryGetValue(key, out RenderTexture cached))
            return cached;

        RenderTexture texture = CreateTexture(key);
        m_portraits[key] = texture;
        m_pending.Add(key);
        BakeAsync().Forget();

        return texture;
    }

    private void Awake()
    {
        if (m_characterPrefab == null)
        {
            Debug.LogWarning($"[{nameof(LobbyPortraitStage)}] 캐릭터 모델이 연결되지 않았습니다 — 얼굴 없이 진행합니다.", this);
            return;
        }

        BuildStage();
    }

    private void OnEnable()
    {
        CosmeticLoadout.OnPlayerColorChanged += HandleOwnColorChanged;
        CosmeticLoadout.OnAccessoryChanged += HandleOwnAccessoryChanged;
        TryBindRoster();
    }

    private void OnDisable()
    {
        CosmeticLoadout.OnPlayerColorChanged -= HandleOwnColorChanged;
        CosmeticLoadout.OnAccessoryChanged -= HandleOwnAccessoryChanged;
        UnbindRoster();
    }

    private void Update()
    {
        if (m_roster == null)
            TryBindRoster();
    }

    private void TryBindRoster()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster == null)
            return;

        m_roster = roster;
        m_roster.Players.OnListChanged += HandleRosterChanged;
        m_roster.OnListReady += BakeRoster;
        BakeRoster();
    }

    private void UnbindRoster()
    {
        if (m_roster == null)
            return;

        m_roster.Players.OnListChanged -= HandleRosterChanged;
        m_roster.OnListReady -= BakeRoster;
        m_roster = null;
    }

    private void HandleRosterChanged(NetworkListEvent<LobbyPlayerEntry> _)
    {
        BakeRoster();
        BakeNow();
    }

    /// <summary>프레임을 기다리지 않고 지금 굽는다 — 출동 직전 외형 변경이 씬 전환에 지지 않게.</summary>
    private void BakeNow()
    {
        if (!m_lit || m_camera == null || m_pending.Count == 0)
            return;

        BakePending();
    }

    /// <summary>명부에 있는 사람 전부의 얼굴을 확보한다.</summary>
    private void BakeRoster()
    {
        if (m_roster == null || !m_roster.IsSpawned)
            return;

        for (int i = 0; i < m_roster.Players.Count; i++)
            GetPortrait(new PortraitKey(m_roster.Players[i].Colors, m_roster.Players[i].Accessories));
    }

    private void OnDestroy()
    {
        foreach (RenderTexture texture in m_portraits.Values)
        {
            if (texture == null)
                continue;

            texture.Release();
            Destroy(texture);
        }

        m_portraits.Clear();

        if (m_bodyTexture == null)
            return;

        m_bodyTexture.Release();
        Destroy(m_bodyTexture);
        m_bodyTexture = null;
    }

    private void HandleOwnColorChanged(EBodyPart _)
    {
        m_bodyDirty = true;
        GetPortrait(PortraitKey.Mine);
    }

    private void HandleOwnAccessoryChanged(EAccessorySlot _)
    {
        m_bodyDirty = true;
        GetPortrait(PortraitKey.Mine);
        BakeAsync().Forget();
    }

    /// <summary>무대 모델의 치장을 즉시 갈아 끼운다.</summary>
    private void Wear(AccessorySet accessories)
    {
        if (m_stageHead == null || m_accessoryCatalog == null)
            return;

        EAccessorySlotMask hidden = m_accessoryCatalog.HiddenSlots(accessories);

        foreach (EAccessorySlot slot in System.Enum.GetValues(typeof(EAccessorySlot)))
        {
            int i = (int)slot;
            GameObject prefab = AccessoryCatalog.IsHidden(hidden, slot)
                ? null
                : m_accessoryCatalog.Get(slot, accessories[slot]);
            if (m_worn[i] == prefab && (prefab == null) == (m_accessories[i] == null))
                continue;

            if (m_accessories[i] != null)
            {
                DestroyImmediate(m_accessories[i]);
                m_accessories[i] = null;
            }

            m_worn[i] = prefab;
            if (prefab == null)
                continue;

            m_accessories[i] = Instantiate(prefab, m_stageHead, false);
            m_accessories[i].name = prefab.name;
        }
    }

    private void BuildStage()
    {
        var stage = new GameObject("PortraitStage");
        stage.transform.SetParent(transform, false);
        stage.transform.position = m_stageOrigin;

        GameObject model = Instantiate(m_characterPrefab, m_stageOrigin, Quaternion.identity, stage.transform);
        model.name = "PortraitCharacter";

        m_tint = model.AddComponent<BodyTint>();

        Transform head = FindDeep(model.transform, m_headBoneName);
        if (head == null)
        {
            Debug.LogWarning($"[{nameof(LobbyPortraitStage)}] '{m_headBoneName}' 본을 찾지 못했습니다 — 모델 원점을 대신 잡습니다.", this);
            head = model.transform;
        }

        m_stageHead = head;
        Wear(AccessorySet.FromSettings());

        var camGo = new GameObject("PortraitCamera");
        camGo.transform.SetParent(stage.transform, false);
        m_camera = camGo.AddComponent<Camera>();
        m_camera.clearFlags = CameraClearFlags.SolidColor;
        m_camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        m_camera.fieldOfView = m_fieldOfView;
        m_camera.nearClipPlane = 0.05f;
        m_camera.farClipPlane = 5f;

        Vector3 focus = head.position + Vector3.up * m_heightOffset;
        camGo.transform.position = focus + model.transform.forward * m_distance;
        camGo.transform.LookAt(focus);

        m_camera.enabled = false;

        m_bodyTexture = CreateTexture(m_bodyTextureSize, "LobbyBodyPreview");

        var bodyGo = new GameObject("BodyCamera");
        bodyGo.transform.SetParent(stage.transform, false);
        m_bodyCamera = bodyGo.AddComponent<Camera>();
        m_bodyCamera.clearFlags = CameraClearFlags.SolidColor;
        m_bodyCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        m_bodyCamera.fieldOfView = m_bodyFieldOfView;
        m_bodyCamera.nearClipPlane = 0.05f;
        m_bodyCamera.farClipPlane = 20f;
        m_bodyCamera.enabled = false;

        Vector3 bodyFocus = model.transform.position + Vector3.up * m_bodyHeight;
        bodyGo.transform.position = bodyFocus + model.transform.forward * m_bodyDistance;
        bodyGo.transform.LookAt(bodyFocus);

        GetPortrait(PortraitKey.Mine);
    }

    private RenderTexture CreateTexture(PortraitKey key) =>
        CreateTexture(m_textureSize, $"LobbyPortrait {key.GetHashCode()}");

    private RenderTexture CreateTexture(Vector2Int size, string name)
    {
        var texture = new RenderTexture(size.x, size.y, 16, RenderTextureFormat.ARGB32)
        {
            name = name,
            antiAliasing = 2,
        };

        texture.Create();
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = prev;

        return texture;
    }

    private async UniTaskVoid BakeAsync()
    {
        if (m_baking)
            return;

        m_baking = true;
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        try
        {
            await UniTask.WaitForEndOfFrame(token);

            if (!m_lit)
            {
                DynamicGI.UpdateEnvironment();
                await UniTask.WaitForEndOfFrame(token);
                m_lit = true;
            }

            BakePending();
        }
        finally
        {
            m_baking = false;
        }
    }

    private void BakePending()
    {
        if (m_pending.Count == 0)
        {
            BakeBody();
            EvictUnused();
            return;
        }

        foreach (PortraitKey key in m_pending)
        {
            if (!m_portraits.TryGetValue(key, out RenderTexture texture) || texture == null)
                continue;

            Tint(key.Colors);
            Wear(key.Accessories);
            m_camera.targetTexture = texture;
            RenderPortrait(texture);
            CaptureSession(key, texture);
        }

        m_pending.Clear();
        m_camera.targetTexture = null;

        BakeBody();
        EvictUnused();
    }

    private void EvictUnused()
    {
        m_live.Clear();
        m_live.Add(PortraitKey.Mine);

        SessionRoster roster = m_roster != null ? m_roster : App.Game.Roster;
        bool rosterReady = roster != null && roster.IsSpawned;

        if (rosterReady)
        {
            for (int i = 0; i < roster.Players.Count; i++)
                m_live.Add(new PortraitKey(roster.Players[i].Colors, roster.Players[i].Accessories));
        }

        Sweep(m_portraits);

        if (rosterReady)
            Sweep(s_sessionPortraits);
    }

    private void Sweep<TTexture>(Dictionary<PortraitKey, TTexture> cache) where TTexture : Texture
    {
        m_stale.Clear();
        foreach (PortraitKey key in cache.Keys)
        {
            if (!m_live.Contains(key))
                m_stale.Add(key);
        }

        for (int i = 0; i < m_stale.Count; i++)
        {
            if (cache.TryGetValue(m_stale[i], out TTexture texture) && texture != null)
            {
                if (texture is RenderTexture render)
                    render.Release();

                Destroy(texture);
            }

            cache.Remove(m_stale[i]);
        }
    }

    private void BakeBody()
    {
        if (!m_bodyDirty || m_bodyCamera == null || m_bodyTexture == null)
            return;

        Tint(PlayerColorSet.FromSettings());
        Wear(AccessorySet.FromSettings());
        m_bodyCamera.targetTexture = m_bodyTexture;
        Render(m_bodyCamera, m_bodyTexture);
        m_bodyCamera.targetTexture = null;
        m_bodyDirty = false;
    }

    private void Tint(PlayerColorSet colors)
    {
        if (m_tint == null || m_palette == null)
            return;

        for (int i = 0; i < m_tintBuffer.Length; i++)
            m_tintBuffer[i] = m_palette.Get(colors[(EBodyPart)i]);

        m_tint.SetBase(m_tintBuffer, m_tintBuffer[(int)EBodyPart.Torso]);
    }

    private void CaptureSession(PortraitKey key, RenderTexture source)
    {
        RenderTexture resolved = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, resolved);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = resolved;

        var captured = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false, false)
        {
            name = $"SessionPortrait {key.GetHashCode()}",
        };
        captured.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
        captured.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(resolved);

        if (s_sessionPortraits.TryGetValue(key, out Texture2D stale) && stale != null)
            Destroy(stale);

        s_sessionPortraits[key] = captured;
    }

    private void RenderPortrait(RenderTexture destination) => Render(m_camera, destination);

    private static void Render(Camera camera, RenderTexture destination)
    {
        if (camera == null || destination == null)
            return;

        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = destination };
        if (RenderPipeline.SupportsRenderRequest(camera, request))
            RenderPipeline.SubmitRenderRequest(camera, request);
        else
            camera.Render();
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null)
                return hit;
        }

        return null;
    }
}

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 플레이하면 바로 조작 가능한 최소 테스트 베이스 씬(NetworkManager + PlayerSpawnManager)을 만든다.
/// 메뉴: Tools/Test Scene/Create Base Test Scene.
/// </summary>
public static class TestBaseSceneBuilder
{
    private const string k_scenePath = "Assets/Scenes/TestBase.unity";
    private const string k_networkManagerPath = "Assets/Prefabs/NetworkManager.prefab";

    [MenuItem("Tools/Test Scene/Create Base Test Scene")]
    public static void Create()
    {
        GameObject networkManagerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            k_networkManagerPath
        );
        if (networkManagerPrefab == null)
        {
            Debug.LogError($"[TestBaseSceneBuilder] {k_networkManagerPath} 를 찾지 못했다.");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        Transform environment = new GameObject("=== ENVIRONMENT ===").transform;
        Transform systems = new GameObject("=== SYSTEMS ===").transform;
        new GameObject("=== WORLD ===");

        CreateLight(environment);
        CreateFallbackCamera(environment);
        CreateFloor(environment);

        Transform spawnPoint = CreateSpawnPoint(systems);
        InstantiateNetworkManager(networkManagerPrefab, systems);
        CreateSpawnManager(systems, spawnPoint);
        CreateDevAutoHost(systems);

        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), k_scenePath);
        Debug.Log(
            $"[TestBaseSceneBuilder] {k_scenePath} 생성 완료 — 복제(Ctrl+D)해서 테스트별로 쓸 것. "
                + "테스트 대상은 === WORLD === 아래에 배치한다."
        );
    }

    private static void CreateLight(Transform parent)
    {
        var go = new GameObject("Directional light");
        go.transform.SetParent(parent);
        go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.shadows = LightShadows.Soft;
    }

    private static void CreateFallbackCamera(Transform parent)
    {
        var go = new GameObject("Main Camera") { tag = "MainCamera" };
        go.transform.SetParent(parent);
        go.transform.SetPositionAndRotation(
            new Vector3(0f, 3f, -8f),
            Quaternion.Euler(10f, 0f, 0f)
        );

        go.AddComponent<Camera>().depth = -1f;
        go.AddComponent<AudioListener>();
    }

    private static void CreateFloor(Transform parent)
    {
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.SetParent(parent);
        floor.transform.localScale = new Vector3(4f, 1f, 4f);
    }

    private static Transform CreateSpawnPoint(Transform parent)
    {
        var go = new GameObject("SpawnPoint");
        go.transform.SetParent(parent);
        go.transform.position = new Vector3(0f, 1f, 0f);
        return go.transform;
    }

    private static void InstantiateNetworkManager(GameObject prefab, Transform parent)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        instance.transform.SetParent(parent);
    }

    private static void CreateSpawnManager(Transform parent, Transform spawnPoint)
    {
        var go = new GameObject("PlayerSpawnManager");
        go.transform.SetParent(parent);

        var spawnManager = go.AddComponent<PlayerSpawnManager>();
        var serialized = new SerializedObject(spawnManager);
        serialized.FindProperty("m_spawnPlayers").boolValue = true;
        serialized.FindProperty("m_spawnPoint").objectReferenceValue = spawnPoint;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void CreateDevAutoHost(Transform parent)
    {
        var go = new GameObject("DevAutoHost");
        go.transform.SetParent(parent);
        go.AddComponent<DevAutoHost>();
    }
}

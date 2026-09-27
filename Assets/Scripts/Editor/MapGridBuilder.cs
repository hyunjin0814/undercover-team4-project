using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// MapPalette와 ASCII 레이아웃을 읽어 맵의 바닥과 경계만 격자로 까는 에디터 도구.
/// 메뉴: Tools/맵 격자 생성. 무엇을 깔지는 팔레트가 정한다.
/// </summary>
public static class MapGridBuilder
{
    private static readonly int[] s_dirCol = { 0, 1, 0, -1 };
    private static readonly int[] s_dirRow = { 1, 0, -1, 0 };

    private static readonly float[] s_wallYaw = { 90f, 180f, 270f, 0f };
    private static readonly int[] s_wallPivotCol = { 1, 1, 0, 0 };
    private static readonly int[] s_wallPivotRow = { 1, 0, 0, 1 };

    [MenuItem("Tools/맵 격자 생성")]
    private static void Build()
    {
        var palette = Selection.activeObject as MapPalette;
        if (palette == null)
        {
            Debug.LogError("MapGridBuilder: Project 창에서 MapPalette 에셋을 고른 뒤 실행할 것 (Create ▸ Map ▸ Map Palette)");
            return;
        }

        if (palette.Layout == null)
        {
            Debug.LogError($"MapGridBuilder: 팔레트 '{palette.name}'에 레이아웃 텍스트가 물려 있지 않다", palette);
            return;
        }

        if (palette.CellSize <= 0f)
        {
            Debug.LogError($"MapGridBuilder: 팔레트 '{palette.name}'의 칸 크기가 0 이하다", palette);
            return;
        }

        if (!TryParse(palette.Layout, out char[][] grid))
        {
            return;
        }

        string rootName = palette.Layout.name.Replace("_Layout", string.Empty);
        GameObject existing = GameObject.Find(rootName);
        if (existing != null)
        {
            bool replace = EditorUtility.DisplayDialog(
                "맵 격자 생성",
                $"씬에 이미 '{rootName}'이 있다. 지우고 다시 만들까?\n(직접 배치한 건물·프롭·스폰 포인트가 그 아래 있다면 함께 사라진다)",
                "지우고 생성",
                "취소"
            );

            if (!replace)
            {
                return;
            }
        }

        Random.InitState(palette.Seed);

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("맵 격자 생성");
        int undoGroup = Undo.GetCurrentGroup();

        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
        }

        var root = new GameObject(rootName);
        Undo.RegisterCreatedObjectUndo(root, "맵 격자 생성");

        var groups = new Dictionary<string, Transform>();
        int tiles = 0;
        var unknownSymbols = new HashSet<char>();

        for (int row = 0; row < grid.Length; row++)
        {
            for (int col = 0; col < grid[row].Length; col++)
            {
                char symbol = grid[row][col];
                if (symbol == ' ')
                {
                    continue;
                }

                if (palette.Wall.IsWallSymbol(symbol))
                {
                    continue;
                }

                MapPalette.Entry entry = palette.Find(symbol);
                if (entry == null)
                {
                    unknownSymbols.Add(symbol);
                    continue;
                }

                if (PlaceCell(palette, entry, grid, Group(groups, root.transform, entry.Group), col, row))
                {
                    tiles++;
                }
            }
        }

        int walls = BuildBoundary(palette, grid, Group(groups, root.transform, "Boundary"), out int columns);

        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root;

        float width2 = grid[0].Length * palette.CellSize;
        float depth = grid.Length * palette.CellSize;
        Debug.Log(
            $"MapGridBuilder: '{rootName}' 생성 완료 — {grid[0].Length}x{grid.Length}칸 ({width2}x{depth}m), "
                + $"바닥 {tiles}칸 · 벽 {walls}짝 · 기둥 {columns}개",
            root
        );

        if (unknownSymbols.Count > 0)
        {
            Debug.LogWarning(
                $"MapGridBuilder: 팔레트 '{palette.name}'에 규칙이 없는 글자를 건너뛰었다 — "
                    + $"'{string.Join("', '", unknownSymbols)}'",
                palette
            );
        }
    }

    /// <summary>바닥·건물·프롭은 두고 경계벽만 다시 세운다.</summary>
    [MenuItem("Tools/맵 경계만 다시 생성")]
    private static void RebuildBoundaryOnly()
    {
        var palette = Selection.activeObject as MapPalette;
        if (palette == null || palette.Layout == null)
        {
            Debug.LogError("MapGridBuilder: Project 창에서 레이아웃이 물린 MapPalette 에셋을 고른 뒤 실행할 것");
            return;
        }

        if (!TryParse(palette.Layout, out char[][] grid))
        {
            return;
        }

        string rootName = palette.Layout.name.Replace("_Layout", string.Empty);
        GameObject root = GameObject.Find(rootName);
        if (root == null)
        {
            Debug.LogError($"MapGridBuilder: 씬에 '{rootName}'이 없다 — 먼저 Tools/맵 격자 생성으로 만들 것", palette);
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("맵 경계 재생성");
        int undoGroup = Undo.GetCurrentGroup();

        Transform existing = root.transform.Find("Boundary");
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing.gameObject);
        }

        var boundaryGo = new GameObject("Boundary");
        Undo.RegisterCreatedObjectUndo(boundaryGo, "맵 경계 재생성");
        boundaryGo.transform.SetParent(root.transform, false);

        Random.InitState(palette.Seed);
        int walls = BuildBoundary(palette, grid, boundaryGo.transform, out int columns);

        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = boundaryGo;
        Debug.Log($"MapGridBuilder: '{rootName}' 경계 재생성 — 벽 {walls}짝 · 기둥 {columns}개", boundaryGo);
    }

    /// <summary>경계 글자 칸을 훑어 벽 한 짝씩 세우고, 마지막에 기둥을 한 번만 세운다. 세운 짝 수를 돌려준다.</summary>
    private static int BuildBoundary(MapPalette palette, char[][] grid, Transform boundary, out int columns)
    {
        var columnSpots = new HashSet<Vector2Int>();
        var suppressedColumns = new HashSet<Vector2Int>();
        int walls = 0;

        if (palette.Wall.HasAnyPiece)
        {
            for (int row = 0; row < grid.Length; row++)
            {
                for (int col = 0; col < grid[row].Length; col++)
                {
                    if (palette.Wall.IsWallSymbol(grid[row][col]))
                    {
                        walls += PlaceWall(palette, grid, boundary, col, row, columnSpots, suppressedColumns);
                    }
                }
            }
        }

        columnSpots.ExceptWith(suppressedColumns);

        if (palette.Wall.Column != null)
        {
            foreach (Vector2Int spot in columnSpots)
            {
                var at = new Vector3(spot.x, 0f, spot.y);
                GameObject column = PlaceRaw(palette.Wall.Column, boundary, at, 0f);

                if (!palette.Wall.UseBoxCollider)
                {
                    continue;
                }

                DisableColliders(column);
                float width = palette.Wall.ColumnWidth;
                PlaceBlocker(
                    boundary,
                    at,
                    0f,
                    new Vector3(0f, palette.Wall.Height * 0.5f, 0f),
                    new Vector3(width, palette.Wall.Height, width)
                );
            }
        }

        columns = columnSpots.Count;
        return walls;
    }

    /// <summary>레이아웃 텍스트를 격자로 파싱한다. 줄 길이가 어긋나면 false.</summary>
    private static bool TryParse(TextAsset asset, out char[][] grid)
    {
        grid = null;

        var lines = new List<string>();
        foreach (string raw in asset.text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            Debug.LogError($"MapGridBuilder: '{asset.name}'에 배치 줄이 하나도 없다", asset);
            return false;
        }

        int width = lines[0].Length;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length != width)
            {
                Debug.LogError(
                    $"MapGridBuilder: '{asset.name}' {i + 1}번째 배치 줄 길이가 {lines[i].Length} — 첫 줄({width})과 달라 중단한다",
                    asset
                );
                return false;
            }
        }

        grid = new char[lines.Count][];
        for (int i = 0; i < lines.Count; i++)
        {
            grid[lines.Count - 1 - i] = lines[i].ToCharArray();
        }

        return true;
    }

    /// <summary>한 칸에 밑깔개·타일·연석을 배치한다. 놓을 프리팹이 없으면 false.</summary>
    private static bool PlaceCell(
        MapPalette palette,
        MapPalette.Entry entry,
        char[][] grid,
        Transform parent,
        int col,
        int row
    )
    {
        if (!HasAny(entry.Prefabs))
        {
            return false;
        }

        if (HasAny(entry.UnderlayPrefabs))
        {
            PlaceTile(palette, Pick(entry.UnderlayPrefabs), parent, col, row, 0f, 0f, 1f);
        }

        if (entry.UsesEdgeFacing && TryPlaceEdgeFacing(palette, entry, grid, parent, col, row))
        {
            return true;
        }

        float yaw = entry.RandomYaw ? Random.Range(0, 4) * 90f : 0f;
        PlaceTile(palette, Pick(entry.Prefabs), parent, col, row, yaw, entry.YOffset, entry.Scale);
        return true;
    }

    /// <summary>접한 도로 방향을 보고 연석이 그쪽을 보도록 프리팹과 회전을 고른다 — 맞는 경우가 없으면 false.</summary>
    private static bool TryPlaceEdgeFacing(
        MapPalette palette,
        MapPalette.Entry entry,
        char[][] grid,
        Transform parent,
        int col,
        int row
    )
    {
        var facing = new bool[4];
        int count = 0;

        for (int d = 0; d < 4; d++)
        {
            if (palette.IsEdgeFacing(At(grid, col + s_dirCol[d], row + s_dirRow[d])))
            {
                facing[d] = true;
                count++;
            }
        }

        if (count == 1)
        {
            for (int d = 0; d < 4; d++)
            {
                if (facing[d])
                {
                    PlaceTile(palette, Pick(entry.EdgePrefabs), parent, col, row, d * 90f, entry.YOffset, entry.Scale);
                    return true;
                }
            }
        }
        else if (count == 2 && HasAny(entry.CornerPrefabs))
        {
            for (int d = 0; d < 4; d++)
            {
                if (facing[d] && facing[(d + 1) % 4])
                {
                    PlaceTile(palette, Pick(entry.CornerPrefabs), parent, col, row, d * 90f, entry.YOffset, entry.Scale);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>경계 칸과 맞닿은 모서리마다 경계벽을 세우고 세운 개수를 돌려준다.</summary>
    private static int PlaceWall(
        MapPalette palette,
        char[][] grid,
        Transform parent,
        int col,
        int row,
        HashSet<Vector2Int> columnSpots,
        HashSet<Vector2Int> suppressedColumns
    )
    {
        MapPalette.WallSettings wall = palette.Wall;
        float cell = palette.CellSize;
        int placed = 0;

        for (int d = 0; d < 4; d++)
        {
            char neighbour = At(grid, col + s_dirCol[d], row + s_dirRow[d]);
            if (neighbour == '\0' || neighbour == ' ' || wall.IsWallSymbol(neighbour))
            {
                continue;
            }

            float yaw = s_wallYaw[d];
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            var edge = new Vector3((col + s_wallPivotCol[d]) * cell, 0f, (row + s_wallPivotRow[d]) * cell);

            Vector3 pivot = edge + rotation * new Vector3(-wall.Inset, 0f, 0f);

            GameObject bottom = PlaceRaw(wall.Bottom, parent, pivot, yaw);
            GameObject top = PlaceRaw(wall.Top, parent, pivot + Vector3.up * wall.TopY, yaw);
            GameObject cap = PlaceRaw(wall.Cap, parent, pivot + Vector3.up * wall.CapY, yaw);

            Vector3 extend = rotation * Vector3.back;
            if (wall.Gate != null
                && palette.IsEdgeFacing(neighbour)
                && IsGateRunStart(palette, grid, col, row, d, cell, extend, out int runCells))
            {
                float runLength = runCells * cell;
                float gateLength = cell * wall.GateScale.z;
                Vector3 gateAt = pivot
                    + extend * ((runLength - gateLength) * 0.5f)
                    + rotation * new Vector3(-wall.GateInset, 0f, 0f);

                GameObject gate = PlaceRaw(wall.Gate, parent, gateAt, yaw);
                if (gate != null)
                {
                    gate.transform.localScale = wall.GateScale;
                }

                for (int i = 1; i < runCells; i++)
                {
                    Vector3 inner = edge + extend * (cell * i);
                    suppressedColumns.Add(new Vector2Int(Mathf.RoundToInt(inner.x), Mathf.RoundToInt(inner.z)));
                }
            }

            if (wall.UseBoxCollider)
            {
                DisableColliders(bottom);
                DisableColliders(top);
                DisableColliders(cap);

                PlaceBlocker(
                    parent,
                    pivot,
                    yaw,
                    new Vector3(wall.Thickness * 0.5f, wall.Height * 0.5f, -cell * 0.5f),
                    new Vector3(wall.Thickness, wall.Height, cell)
                );
            }

            if (wall.Column != null)
            {
                Vector3 far = edge + rotation * new Vector3(0f, 0f, -cell);
                columnSpots.Add(new Vector2Int(Mathf.RoundToInt(edge.x), Mathf.RoundToInt(edge.z)));
                columnSpots.Add(new Vector2Int(Mathf.RoundToInt(far.x), Mathf.RoundToInt(far.z)));
            }

            placed++;
        }

        return placed;
    }

    /// <summary>이 칸이 도로 개구부의 시작 칸인지 판정하고, 개구부 칸 수를 runCells로 돌려준다.</summary>
    private static bool IsGateRunStart(
        MapPalette palette,
        char[][] grid,
        int col,
        int row,
        int d,
        float cell,
        Vector3 extend,
        out int runCells
    )
    {
        int stepCol = (d == 0 || d == 2) ? 1 : 0;
        int stepRow = (d == 0 || d == 2) ? 0 : 1;

        int back = 0;
        while (FrontsRoad(palette, grid, col - stepCol * (back + 1), row - stepRow * (back + 1), d))
        {
            back++;
        }

        int forward = 0;
        while (FrontsRoad(palette, grid, col + stepCol * (forward + 1), row + stepRow * (forward + 1), d))
        {
            forward++;
        }

        runCells = back + forward + 1;

        var stepWorld = new Vector3(stepCol * cell, 0f, stepRow * cell);
        int behindCol = Vector3.Dot(stepWorld, extend) > 0f ? col - stepCol : col + stepCol;
        int behindRow = Vector3.Dot(stepWorld, extend) > 0f ? row - stepRow : row + stepRow;
        return !FrontsRoad(palette, grid, behindCol, behindRow, d);
    }

    /// <summary>그 칸이 경계 글자이면서 <paramref name="d"/> 쪽 이웃이 도로인가.</summary>
    private static bool FrontsRoad(MapPalette palette, char[][] grid, int col, int row, int d)
    {
        if (!palette.Wall.IsWallSymbol(At(grid, col, row)))
        {
            return false;
        }

        return palette.IsEdgeFacing(At(grid, col + s_dirCol[d], row + s_dirRow[d]));
    }

    /// <summary>벽 조각의 콜라이더를 끈다 — 충돌은 PlaceBlocker가 세운 상자가 대신한다.</summary>
    private static void DisableColliders(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }
    }

    /// <summary>콜라이더만 있는 상자를 세운다 — center·size는 로컬 기준.</summary>
    private static void PlaceBlocker(Transform parent, Vector3 position, float yaw, Vector3 center, Vector3 size)
    {
        var blocker = new GameObject("WallBlocker");
        blocker.transform.SetParent(parent, false);
        blocker.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        blocker.isStatic = true;

        BoxCollider box = blocker.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
    }

    /// <summary>렌더러 경계 중심을 칸 중심에 맞춰 타일을 배치한다.</summary>
    private static void PlaceTile(
        MapPalette palette,
        GameObject prefab,
        Transform parent,
        int col,
        int row,
        float yaw,
        float y,
        float scale
    )
    {
        GameObject instance = PlaceRaw(prefab, parent, Vector3.zero, yaw);
        if (instance == null)
        {
            return;
        }

        if (!Mathf.Approximately(scale, 1f))
        {
            instance.transform.localScale = Vector3.one * scale;
        }

        float cell = palette.CellSize;
        Bounds bounds = RendererBounds(instance);
        Vector3 position = instance.transform.position;
        position.x += (col + 0.5f) * cell - bounds.center.x;
        position.z += (row + 0.5f) * cell - bounds.center.z;
        position.y = y;
        instance.transform.position = position;
    }

    private static GameObject PlaceRaw(GameObject prefab, Transform parent, Vector3 position, float yaw)
    {
        if (prefab == null)
        {
            return null;
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        instance.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

        GameObjectUtility.SetStaticEditorFlags(
            instance,
            StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
        );

        return instance;
    }

    /// <summary>이름별 자식 묶음 — 없으면 만든다.</summary>
    private static Transform Group(Dictionary<string, Transform> groups, Transform root, string name)
    {
        if (groups.TryGetValue(name, out Transform existing))
        {
            return existing;
        }

        var child = new GameObject(name);
        child.transform.SetParent(root, false);
        groups[name] = child.transform;
        return child.transform;
    }

    private static Bounds RendererBounds(GameObject instance)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return new Bounds(instance.transform.position, Vector3.zero);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private static char At(char[][] grid, int col, int row)
    {
        if (row < 0 || row >= grid.Length || col < 0 || col >= grid[row].Length)
        {
            return '\0';
        }

        return grid[row][col];
    }

    private static bool HasAny(GameObject[] pool)
    {
        if (pool == null)
        {
            return false;
        }

        foreach (GameObject prefab in pool)
        {
            if (prefab != null)
            {
                return true;
            }
        }

        return false;
    }

    private static GameObject Pick(GameObject[] pool)
    {
        int candidates = 0;
        foreach (GameObject prefab in pool)
        {
            if (prefab != null)
            {
                candidates++;
            }
        }

        if (candidates == 0)
        {
            return null;
        }

        int index = Random.Range(0, candidates);
        foreach (GameObject prefab in pool)
        {
            if (prefab == null)
            {
                continue;
            }

            if (index-- == 0)
            {
                return prefab;
            }
        }

        return null;
    }
}

// Place this file in: Assets/Editor/EngineeringFloor1Generator.cs
// Then open Unity and choose: Tools > Engineering Building > Building Generator

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public sealed class EngineeringFloor1Generator : EditorWindow
{
    private const string RootName = "Engineering Building";
    private const string LegacyRootName = "Engineering Building - Floor 1";
    private const string MaterialFolder = "Assets/EngineeringBuildingGenerated/Materials";

    [SerializeField] private float uniformScale = 1f;
    [SerializeField] private float wallHeight = 3.2f;
    [SerializeField] private float floor2Elevation = 3.35f;
    [SerializeField] private float wallThickness = 0.18f;
    [SerializeField] private float floorThickness = 0.12f;
    [SerializeField] private float structuralSlabThickness = 0.22f;
    [SerializeField] private float doorWidth = 0.95f;
    [SerializeField] private float floor2ScaleX = 0.988f;
    [SerializeField] private float floor2ScaleZ = 0.987f;
    [SerializeField] private float floor2OffsetX = 0f;
    [SerializeField] private float floor2OffsetZ = 0f;
    [SerializeField] private bool createRoomLabels = true;
    [SerializeField] private bool createOverviewCamera = true;
    [SerializeField] private bool createRaisedNorthTerrain = true;

    private readonly Dictionary<SpaceType, Material> materials = new Dictionary<SpaceType, Material>();
    private Transform root;
    private Transform currentLevel;
    private Transform floors;
    private Transform walls;
    private Transform details;
    private Transform labels;

    private enum SpaceType
    {
        Lecture,
        Imagination,
        Realization,
        Department,
        Office,
        Public,
        Operations,
        Corridor,
        Wall,
        Column,
        Breezeway,
        Slab,
        StairYellow,
        StairBlack,
        RailingRed,
        Terrain,
        Pathway
    }

    private enum DoorSide { None, North, South, East, West }

    private readonly struct RoomSpec
    {
        public readonly string Name;
        public readonly float X;
        public readonly float Z;
        public readonly float Width;
        public readonly float Depth;
        public readonly SpaceType Type;
        public readonly DoorSide Door;
        public readonly float DoorOffset;

        public RoomSpec(string name, float x, float z, float width, float depth,
            SpaceType type, DoorSide door, float doorOffset = 0f)
        {
            Name = name;
            X = x;
            Z = z;
            Width = width;
            Depth = depth;
            Type = type;
            Door = door;
            DoorOffset = Mathf.Clamp(doorOffset, -0.48f, 0.48f);
        }
    }

    [MenuItem("Tools/Engineering Building/Building Generator")]
    private static void OpenWindow()
    {
        GetWindow<EngineeringFloor1Generator>("Building Generator");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Engineering Building - Floors 1 and 2", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "This creates both floors in one shared coordinate system. Floor 2 is placed " +
            "automatically above Floor 1, so no manual alignment is required. The plans have " +
            "no dimensions, so Scale remains adjustable.",
            MessageType.Info);

        uniformScale = EditorGUILayout.FloatField("Uniform Scale", uniformScale);
        wallHeight = EditorGUILayout.FloatField("Wall Height (m)", wallHeight);
        floor2Elevation = EditorGUILayout.FloatField("Floor 2 Elevation (m)", floor2Elevation);
        wallThickness = EditorGUILayout.FloatField("Wall Thickness (m)", wallThickness);
        floorThickness = EditorGUILayout.FloatField("Floor Thickness (m)", floorThickness);
        structuralSlabThickness = EditorGUILayout.FloatField("Between-floor Slab (m)", structuralSlabThickness);
        doorWidth = EditorGUILayout.FloatField("Door Opening (m)", doorWidth);
        createRoomLabels = EditorGUILayout.Toggle("Create Room Labels", createRoomLabels);
        createOverviewCamera = EditorGUILayout.Toggle("Create Overview Camera", createOverviewCamera);
        createRaisedNorthTerrain = EditorGUILayout.Toggle("Raised North Terrain", createRaisedNorthTerrain);

        EditorGUILayout.LabelField("Floor 2 Plan Alignment", EditorStyles.boldLabel);
        floor2ScaleX = EditorGUILayout.FloatField("Floor 2 X Scale", floor2ScaleX);
        floor2ScaleZ = EditorGUILayout.FloatField("Floor 2 Z Scale", floor2ScaleZ);
        floor2OffsetX = EditorGUILayout.FloatField("Floor 2 X Offset", floor2OffsetX);
        floor2OffsetZ = EditorGUILayout.FloatField("Floor 2 Z Offset", floor2OffsetZ);

        GUILayout.Space(8f);

        if (GUILayout.Button("Generate / Replace Complete Building", GUILayout.Height(34f)))
        {
            uniformScale = Mathf.Max(0.01f, uniformScale);
            wallHeight = Mathf.Max(1f, wallHeight);
            floor2Elevation = Mathf.Max(wallHeight + structuralSlabThickness, floor2Elevation);
            wallThickness = Mathf.Clamp(wallThickness, 0.05f, 1f);
            floorThickness = Mathf.Clamp(floorThickness, 0.02f, 1f);
            structuralSlabThickness = Mathf.Clamp(structuralSlabThickness, 0.08f, 0.6f);
            doorWidth = Mathf.Clamp(doorWidth, 0.5f, 3f);
            floor2ScaleX = Mathf.Clamp(floor2ScaleX, 0.8f, 1.2f);
            floor2ScaleZ = Mathf.Clamp(floor2ScaleZ, 0.8f, 1.2f);
            GenerateBuilding();
        }

        if (GUILayout.Button("Apply Floor 2 Alignment Only"))
            ApplyFloor2Alignment();

        GUILayout.Space(12f);
        EditorGUILayout.LabelField("Floor Visibility", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Both")) SetFloorVisibility(true, true);
        if (GUILayout.Button("Floor 1")) SetFloorVisibility(true, false);
        if (GUILayout.Button("Floor 2")) SetFloorVisibility(false, true);
        if (GUILayout.Button("Hide All")) SetFloorVisibility(false, false);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Despawn Floor 1")) DespawnLevel("Floor 1");
        if (GUILayout.Button("Despawn Floor 2")) DespawnLevel("Floor 2");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "Generating again replaces the previously generated building root (including the " +
            "older Floor 1-only root). Duplicate or rename it first if you want to preserve manual edits.",
            MessageType.Warning);
    }

    private void GenerateBuilding()
    {
        GameObject oldRoot = GameObject.Find(RootName);
        if (oldRoot != null)
            Undo.DestroyObjectImmediate(oldRoot);

        GameObject legacyRoot = GameObject.Find(LegacyRootName);
        if (legacyRoot != null)
            Undo.DestroyObjectImmediate(legacyRoot);

        EnsureMaterialAssets();

        GameObject rootObject = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(rootObject, "Generate engineering building");
        root = rootObject.transform;
        root.localScale = Vector3.one * uniformScale;

        BeginLevel("Floor 1", 0f);
        BuildCorridorsAndBreezeway();
        BuildRooms();
        BuildStairs();
        BuildColumns();
        BuildEntranceMarkers();

        BeginLevel("Floor 2", floor2Elevation);
        BuildSecondFloorStructuralDeck();
        BuildSecondFloorCorridors();
        BuildSecondFloorRooms();
        BuildSecondFloorDetails();
        if (createRaisedNorthTerrain)
            BuildRaisedNorthTerrain();

        if (createOverviewCamera)
            BuildSceneHelpers();

        Selection.activeGameObject = rootObject;
        SceneView.lastActiveSceneView?.FrameSelected();
        EditorUtility.SetDirty(rootObject);
    }

    private void BeginLevel(string levelName, float elevation)
    {
        currentLevel = NewGroup(levelName, root);
        bool isFloor2 = levelName == "Floor 2";
        currentLevel.localPosition = isFloor2
            ? new Vector3(floor2OffsetX, elevation, floor2OffsetZ)
            : new Vector3(0f, elevation, 0f);
        currentLevel.localScale = isFloor2
            ? new Vector3(floor2ScaleX, 1f, floor2ScaleZ)
            : Vector3.one;
        floors = NewGroup("Floors", currentLevel);
        walls = NewGroup("Walls", currentLevel);
        details = NewGroup("Details", currentLevel);
        labels = NewGroup("Room Labels", currentLevel);
    }

    private void ApplyFloor2Alignment()
    {
        GameObject building = GameObject.Find(RootName);
        Transform floor2 = building != null ? building.transform.Find("Floor 2") : null;
        if (floor2 == null)
        {
            ShowNotification(new GUIContent("Generate Floor 2 first."));
            return;
        }

        floor2ScaleX = Mathf.Clamp(floor2ScaleX, 0.8f, 1.2f);
        floor2ScaleZ = Mathf.Clamp(floor2ScaleZ, 0.8f, 1.2f);
        Undo.RecordObject(floor2, "Align Floor 2");
        floor2.localPosition = new Vector3(floor2OffsetX, floor2Elevation, floor2OffsetZ);
        floor2.localScale = new Vector3(floor2ScaleX, 1f, floor2ScaleZ);
        EditorUtility.SetDirty(floor2);
        SceneView.RepaintAll();
    }

    private void SetFloorVisibility(bool showFloor1, bool showFloor2)
    {
        GameObject building = GameObject.Find(RootName);
        if (building == null)
        {
            ShowNotification(new GUIContent("Generate the building first."));
            return;
        }

        SetLevelActive(building.transform.Find("Floor 1"), showFloor1);
        SetLevelActive(building.transform.Find("Floor 2"), showFloor2);
        SceneView.RepaintAll();
    }

    private static void SetLevelActive(Transform level, bool active)
    {
        if (level == null || level.gameObject.activeSelf == active)
            return;

        Undo.RecordObject(level.gameObject, active ? "Show floor" : "Hide floor");
        level.gameObject.SetActive(active);
        EditorUtility.SetDirty(level.gameObject);
    }

    private void DespawnLevel(string levelName)
    {
        GameObject building = GameObject.Find(RootName);
        Transform level = building != null ? building.transform.Find(levelName) : null;
        if (level == null)
        {
            ShowNotification(new GUIContent(levelName + " was not found."));
            return;
        }

        bool confirmed = EditorUtility.DisplayDialog(
            "Despawn " + levelName + "?",
            "This removes the generated " + levelName + " object. You can restore it by generating the complete building again.",
            "Despawn",
            "Cancel");

        if (confirmed)
            Undo.DestroyObjectImmediate(level.gameObject);
    }

    private void BuildRooms()
    {
        // Approximate traced rectangles. Plan-right is Unity +X; plan-up is Unity +Z.
        RoomSpec[] rooms =
        {
            // West edge and lobby
            R("141", 2.9f, 28.2f, 5.8f, 3.6f, SpaceType.Imagination, DoorSide.East),
            R("139", 2.9f, 23.2f, 5.8f, 6.4f, SpaceType.Realization, DoorSide.East),
            R("137", 2.9f, 18.3f, 5.8f, 3.4f, SpaceType.Office, DoorSide.East),
            R("133", 2.9f, 13.2f, 5.8f, 6.8f, SpaceType.Lecture, DoorSide.East),
            R("131", 2.9f, 8.5f, 5.8f, 2.6f, SpaceType.Office, DoorSide.East),
            R("129", 2.9f, 4.7f, 5.8f, 4.3f, SpaceType.Imagination, DoorSide.East),
            R("127A", 1.65f, 1.65f, 3.3f, 3.3f, SpaceType.Office, DoorSide.North),
            R("127", 4.55f, 1.65f, 2.5f, 3.3f, SpaceType.Office, DoorSide.North),
            R("Lower Lobby 142", 8.6f, 24.9f, 5.4f, 8.6f, SpaceType.Public, DoorSide.West),

            // Left wing, north row
            R("143", 12.95f, 27.8f, 3.3f, 4.2f, SpaceType.Office, DoorSide.South),
            R("145", 12.95f, 23.25f, 3.3f, 4.9f, SpaceType.Office, DoorSide.South),
            R("147", 21.2f, 25.3f, 13.2f, 9.1f, SpaceType.Realization, DoorSide.South),
            R("151", 32.55f, 25.3f, 9.5f, 9.1f, SpaceType.Lecture, DoorSide.South),
            R("153A", 39.35f, 27.8f, 4.1f, 4.2f, SpaceType.Department, DoorSide.South),
            R("153", 39.35f, 23.25f, 4.1f, 4.9f, SpaceType.Department, DoorSide.South),
            R("155", 46.1f, 25.3f, 9.4f, 9.1f, SpaceType.Department, DoorSide.South),
            R("167", 53.1f, 18.8f, 4.6f, 12.8f, SpaceType.Department, DoorSide.West),

            // Left wing, center rooms
            R("126", 10.9f, 11.7f, 3.8f, 2.5f, SpaceType.Operations, DoorSide.South),
            R("124A", 13.15f, 13.3f, 1.3f, 5.7f, SpaceType.Operations, DoorSide.West),
            R("124 Men", 15.15f, 13.3f, 2.7f, 5.7f, SpaceType.Public, DoorSide.South),
            R("130 Women", 15.15f, 17.0f, 4.1f, 3.5f, SpaceType.Public, DoorSide.North),
            R("122", 20.4f, 14.05f, 7.0f, 8.1f, SpaceType.Operations, DoorSide.South),
            R("120", 24.85f, 14.05f, 1.9f, 8.1f, SpaceType.Operations, DoorSide.South),
            R("118", 29.35f, 14.05f, 7.1f, 8.1f, SpaceType.Operations, DoorSide.South),
            R("132", 36.5f, 15.0f, 7.2f, 6.2f, SpaceType.Operations, DoorSide.South),
            R("116", 35.2f, 11.0f, 6.0f, 1.8f, SpaceType.Operations, DoorSide.South),
            R("134", 41.0f, 16.9f, 2.0f, 3.0f, SpaceType.Operations, DoorSide.East),
            R("114", 40.0f, 12.3f, 2.2f, 3.0f, SpaceType.Operations, DoorSide.East),
            R("159", 47.9f, 19.2f, 5.8f, 2.5f, SpaceType.Office, DoorSide.West),
            R("161", 47.9f, 16.6f, 5.8f, 2.5f, SpaceType.Office, DoorSide.West),
            R("163", 47.9f, 14.0f, 5.8f, 2.5f, SpaceType.Office, DoorSide.West),
            R("165", 47.9f, 11.35f, 5.8f, 2.6f, SpaceType.Office, DoorSide.West),

            // Left wing, south row
            R("123", 10.4f, 6.65f, 2.8f, 2.1f, SpaceType.Operations, DoorSide.North),
            R("125", 10.4f, 2.8f, 2.8f, 5.6f, SpaceType.Office, DoorSide.North),
            R("119", 17.6f, 3.85f, 11.6f, 7.7f, SpaceType.Realization, DoorSide.North),
            R("117", 27.8f, 3.85f, 8.8f, 7.7f, SpaceType.Realization, DoorSide.North),
            R("115", 35.0f, 6.35f, 5.6f, 2.7f, SpaceType.Office, DoorSide.North),
            R("117A", 35.0f, 2.5f, 5.6f, 5.0f, SpaceType.Realization, DoorSide.North),
            R("111", 44.3f, 3.85f, 13.0f, 7.7f, SpaceType.Realization, DoorSide.North),
            R("169A", 52.1f, 1.6f, 2.6f, 2.4f, SpaceType.Department, DoorSide.West),
            R("169B", 52.1f, 4.25f, 2.6f, 2.4f, SpaceType.Department, DoorSide.West),

            // Right wing, north and core
            R("101A", 61.4f, 28.2f, 2.6f, 4.2f, SpaceType.Operations, DoorSide.East),
            R("101 Lighting Lab", 69.0f, 25.4f, 12.0f, 10.8f, SpaceType.Realization, DoorSide.South),
            R("101B", 69.5f, 19.5f, 4.0f, 2.2f, SpaceType.Operations, DoorSide.South),
            R("101C", 73.2f, 18.9f, 3.0f, 3.4f, SpaceType.Operations, DoorSide.South),
            R("101D", 69.8f, 16.5f, 4.6f, 3.0f, SpaceType.Operations, DoorSide.West),
            R("105", 82.7f, 22.1f, 15.4f, 18.2f, SpaceType.Operations, DoorSide.South),
            R("105A", 94.5f, 27.2f, 9.0f, 8.0f, SpaceType.Operations, DoorSide.West),
            R("105B", 94.5f, 18.7f, 9.0f, 8.8f, SpaceType.Operations, DoorSide.West),
            R("105C", 88.8f, 15.0f, 2.0f, 5.0f, SpaceType.Operations, DoorSide.West),
            R("107A", 86.5f, 16.0f, 2.0f, 5.2f, SpaceType.Operations, DoorSide.West),

            // Right wing elevators and stairs
            R("Elevator 1", 65.2f, 17.0f, 2.5f, 2.4f, SpaceType.Public, DoorSide.East),
            R("Elevator 2", 65.2f, 14.4f, 2.5f, 2.4f, SpaceType.Public, DoorSide.East),
            R("Elevator 3", 65.2f, 11.8f, 2.5f, 2.4f, SpaceType.Public, DoorSide.East),

            // Right wing, south-west
            R("102A", 65.0f, 4.3f, 4.0f, 8.6f, SpaceType.Imagination, DoorSide.East),
            R("102 Photonics", 70.8f, 4.3f, 7.6f, 8.6f, SpaceType.Realization, DoorSide.North),

            // Right wing, south labs/offices
            R("104", 77.0f, 14.4f, 4.0f, 3.0f, SpaceType.Department, DoorSide.South),
            R("104H", 81.0f, 14.4f, 3.6f, 3.0f, SpaceType.Department, DoorSide.South),
            R("103H", 84.8f, 14.4f, 3.6f, 3.0f, SpaceType.Department, DoorSide.South),
            R("103A", 86.4f, 11.0f, 4.2f, 3.2f, SpaceType.Department, DoorSide.West),
            R("103", 77.6f, 10.4f, 5.2f, 4.6f, SpaceType.Department, DoorSide.North),
            R("103V", 80.4f, 7.7f, 3.6f, 2.4f, SpaceType.Department, DoorSide.North),
            R("103T", 85.0f, 7.7f, 4.4f, 2.4f, SpaceType.Department, DoorSide.North),
            R("103U", 89.3f, 7.7f, 4.0f, 2.4f, SpaceType.Department, DoorSide.North),
            R("103L", 95.2f, 10.3f, 7.8f, 4.8f, SpaceType.Department, DoorSide.West),
            R("103R", 77.1f, 2.9f, 4.2f, 5.8f, SpaceType.Department, DoorSide.North),
            R("103Q", 81.4f, 2.9f, 4.0f, 5.8f, SpaceType.Department, DoorSide.North),
            R("103P", 85.5f, 2.9f, 4.0f, 5.8f, SpaceType.Department, DoorSide.North),
            R("103S", 89.7f, 2.9f, 4.2f, 5.8f, SpaceType.Department, DoorSide.North),
            R("103N", 93.1f, 2.9f, 2.6f, 5.8f, SpaceType.Department, DoorSide.North),
            R("103M", 97.2f, 4.1f, 5.6f, 8.2f, SpaceType.Department, DoorSide.West)
        };

        foreach (RoomSpec room in rooms)
            CreateRoom(room);
    }

    private static RoomSpec R(string name, float x, float z, float width, float depth,
        SpaceType type, DoorSide door, float offset = 0f)
    {
        return new RoomSpec(name, x, z, width, depth, type, door, offset);
    }

    private void BuildSecondFloorRooms()
    {
        // Floor 2 is traced into the same X/Z coordinate system as Floor 1.
        // The +X side of this model is north, matching the arrows on both plans.
        RoomSpec[] rooms =
        {
            // West wing, upper row
            R("269", 4.6f, 26.0f, 9.2f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("271", 12.25f, 26.0f, 6.1f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("273", 18.3f, 26.0f, 6.0f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("275", 24.35f, 26.0f, 6.1f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("277", 30.4f, 26.0f, 6.0f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("279", 36.45f, 26.0f, 6.1f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("281", 42.55f, 26.0f, 6.1f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("283", 50.35f, 26.0f, 9.5f, 10.2f, SpaceType.Lecture, DoorSide.South),
            R("267", 3.1f, 19.15f, 6.2f, 3.5f, SpaceType.Office, DoorSide.East),
            R("263", 3.1f, 14.1f, 6.2f, 6.6f, SpaceType.Lecture, DoorSide.East),
            R("261", 3.1f, 9.55f, 6.2f, 2.5f, SpaceType.Office, DoorSide.East),
            R("285", 52.0f, 15.4f, 6.4f, 10.8f, SpaceType.Lecture, DoorSide.West),

            // West wing, interior core
            R("256 Women", 15.8f, 15.4f, 4.2f, 6.0f, SpaceType.Public, DoorSide.South),
            R("250 Men", 15.8f, 11.8f, 4.2f, 3.0f, SpaceType.Public, DoorSide.South),
            R("258", 21.1f, 15.3f, 6.2f, 6.2f, SpaceType.Operations, DoorSide.South),
            R("248", 21.1f, 11.8f, 6.2f, 2.8f, SpaceType.Office, DoorSide.South),
            R("260B", 27.5f, 15.9f, 3.0f, 4.8f, SpaceType.Office, DoorSide.South),
            R("260", 30.0f, 14.6f, 2.0f, 7.4f, SpaceType.Public, DoorSide.South),
            R("260A", 32.3f, 15.9f, 2.6f, 4.8f, SpaceType.Office, DoorSide.South),
            R("246B", 27.5f, 11.9f, 3.0f, 2.7f, SpaceType.Office, DoorSide.South),
            R("246", 30.0f, 11.9f, 2.0f, 2.7f, SpaceType.Public, DoorSide.South),
            R("246A", 32.3f, 11.9f, 2.6f, 2.7f, SpaceType.Office, DoorSide.South),
            R("244D", 35.5f, 16.0f, 3.6f, 4.6f, SpaceType.Department, DoorSide.South),
            R("262", 38.3f, 16.0f, 2.0f, 4.6f, SpaceType.Department, DoorSide.South),
            R("262B", 41.0f, 16.0f, 3.2f, 4.6f, SpaceType.Department, DoorSide.South),
            R("264", 44.0f, 16.0f, 2.0f, 4.6f, SpaceType.Department, DoorSide.South),
            R("244", 37.0f, 11.8f, 6.6f, 3.0f, SpaceType.Department, DoorSide.South),
            R("244B", 41.1f, 12.2f, 3.0f, 2.4f, SpaceType.Department, DoorSide.South),
            R("244A", 44.0f, 12.2f, 2.0f, 2.4f, SpaceType.Department, DoorSide.South),

            // West wing and bridge, lower row
            R("259", 3.5f, 3.85f, 7.0f, 7.7f, SpaceType.Realization, DoorSide.North),
            R("257", 9.75f, 3.85f, 5.5f, 7.7f, SpaceType.Department, DoorSide.North),
            R("255", 15.25f, 3.85f, 5.5f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("253 ET Computer Lab", 21.0f, 3.85f, 6.0f, 7.7f, SpaceType.Imagination, DoorSide.North),
            R("251", 27.1f, 3.85f, 6.2f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("249", 35.0f, 3.85f, 9.6f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("247", 42.2f, 3.85f, 4.8f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("245", 47.3f, 3.85f, 5.4f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("243", 52.7f, 3.85f, 5.4f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("241", 58.1f, 3.85f, 5.4f, 7.7f, SpaceType.Lecture, DoorSide.North),
            R("239", 63.2f, 3.85f, 4.8f, 7.7f, SpaceType.Lecture, DoorSide.North),

            // Floor 2 bridge between the two primary wings
            R("205 ECE Computer Lab", 59.2f, 23.5f, 8.0f, 7.2f, SpaceType.Imagination, DoorSide.East),
            R("201", 57.3f, 15.0f, 3.8f, 8.0f, SpaceType.Department, DoorSide.South),
            R("203", 61.1f, 15.0f, 3.8f, 8.0f, SpaceType.Department, DoorSide.South),

            // East wing, upper row
            R("207", 67.3f, 26.2f, 5.8f, 10.6f, SpaceType.Lecture, DoorSide.South),
            R("209", 72.8f, 26.2f, 5.2f, 10.6f, SpaceType.Lecture, DoorSide.South),
            R("211", 79.0f, 26.2f, 7.2f, 10.6f, SpaceType.Lecture, DoorSide.South),
            R("213", 85.8f, 26.2f, 6.4f, 10.6f, SpaceType.Lecture, DoorSide.South),
            R("217", 92.0f, 26.2f, 6.0f, 10.6f, SpaceType.Lecture, DoorSide.South),
            R("219", 98.0f, 26.2f, 6.0f, 10.6f, SpaceType.Lecture, DoorSide.South),
            R("221", 98.5f, 18.9f, 5.0f, 3.6f, SpaceType.Operations, DoorSide.South),

            // East wing, central core
            R("202", 68.0f, 17.0f, 3.2f, 2.4f, SpaceType.Operations, DoorSide.South),
            R("204 Women", 74.0f, 15.8f, 3.6f, 4.8f, SpaceType.Public, DoorSide.South),
            R("206", 77.4f, 15.8f, 3.0f, 4.8f, SpaceType.Operations, DoorSide.South),
            R("208", 80.5f, 15.8f, 3.2f, 4.8f, SpaceType.Office, DoorSide.South),
            R("210 HVAC", 85.5f, 16.0f, 6.8f, 4.8f, SpaceType.Operations, DoorSide.South),
            R("214", 91.4f, 14.5f, 2.4f, 3.8f, SpaceType.Operations, DoorSide.West),
            R("226 Men", 76.2f, 11.8f, 7.5f, 3.4f, SpaceType.Public, DoorSide.South),
            R("222", 85.0f, 11.8f, 5.2f, 3.4f, SpaceType.Office, DoorSide.South),
            R("220", 88.8f, 11.8f, 2.2f, 3.4f, SpaceType.Department, DoorSide.South),
            R("218", 91.5f, 10.9f, 3.0f, 2.0f, SpaceType.Operations, DoorSide.South),

            // East wing, lower offices and department rooms
            R("233I", 66.8f, 6.0f, 3.0f, 2.4f, SpaceType.Office, DoorSide.North),
            R("233G", 70.0f, 6.0f, 3.0f, 2.4f, SpaceType.Office, DoorSide.North),
            R("233E", 73.2f, 6.0f, 3.0f, 2.4f, SpaceType.Office, DoorSide.North),
            R("233", 76.0f, 6.0f, 2.4f, 2.4f, SpaceType.Public, DoorSide.North),
            R("233A", 79.2f, 6.0f, 3.8f, 2.4f, SpaceType.Department, DoorSide.North),
            R("231", 84.0f, 6.0f, 5.6f, 2.4f, SpaceType.Department, DoorSide.North),
            R("227", 88.8f, 6.0f, 4.0f, 2.4f, SpaceType.Department, DoorSide.North),
            R("227A", 92.4f, 6.0f, 3.2f, 2.4f, SpaceType.Department, DoorSide.North),
            R("225", 95.5f, 6.0f, 3.0f, 2.4f, SpaceType.Department, DoorSide.North),
            R("225A", 98.8f, 6.0f, 3.4f, 2.4f, SpaceType.Department, DoorSide.North),
            R("233J", 66.8f, 1.9f, 3.0f, 3.8f, SpaceType.Office, DoorSide.North),
            R("233H", 70.0f, 1.9f, 3.0f, 3.8f, SpaceType.Office, DoorSide.North),
            R("233F", 73.2f, 1.9f, 3.0f, 3.8f, SpaceType.Office, DoorSide.North),
            R("233D", 76.4f, 1.9f, 3.0f, 3.8f, SpaceType.Office, DoorSide.North),
            R("233C", 79.6f, 1.9f, 3.0f, 3.8f, SpaceType.Office, DoorSide.North),
            R("233B", 82.8f, 1.9f, 3.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("231B", 85.8f, 1.9f, 3.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("231A", 88.8f, 1.9f, 3.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("227C", 91.8f, 1.9f, 3.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("227B", 94.8f, 1.9f, 3.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("225C", 97.8f, 1.9f, 3.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("225B", 100.3f, 1.9f, 2.0f, 3.8f, SpaceType.Department, DoorSide.North),
            R("223", 99.0f, 8.7f, 4.0f, 2.8f, SpaceType.Department, DoorSide.West)
        };

        foreach (RoomSpec room in rooms)
            CreateRoom(room);

        CreateSecondFloorMainLobby();
    }

    private void CreateSecondFloorMainLobby()
    {
        const string lobbyName = "Main Lobby 215";
        const float x = 96.7f;
        const float z = 13.6f;
        const float width = 8.6f;
        const float depth = 7.8f;

        Transform lobby = NewGroup("Room " + lobbyName, currentLevel);
        CreateBox(lobby, "Floor", new Vector3(x, floorThickness * 0.5f, z),
            new Vector3(width, floorThickness, depth), materials[SpaceType.Public]);

        // Four openings: exterior grade entrance, central core, upper hall (219), and lower hall (225).
        AddHorizontalWall(lobby, lobbyName + " Upper Hall Wall", x, z + depth * 0.5f,
            width, true, -0.15f);
        AddHorizontalWall(lobby, lobbyName + " Lower Hall Wall", x, z - depth * 0.5f,
            width, true, -0.15f);
        AddVerticalWall(lobby, lobbyName + " North Entrance Wall", x + width * 0.5f, z,
            depth, true, 0f);
        AddVerticalWall(lobby, lobbyName + " Core Hall Wall", x - width * 0.5f, z,
            depth, true, 0f);

        if (createRoomLabels)
            CreateLabel(lobbyName, new Vector3(x, floorThickness + 0.025f, z));
    }

    private void BuildSecondFloorCorridors()
    {
        CreateFloor("Floor 2 west upper corridor", 29.0f, 19.8f, 46.0f, 2.2f, SpaceType.Corridor);
        CreateFloor("Floor 2 west lower corridor", 29.5f, 9.1f, 47.0f, 2.8f, SpaceType.Corridor);
        CreateFloor("Floor 2 west vertical corridor", 7.8f, 14.0f, 3.2f, 11.8f, SpaceType.Corridor);
        CreateFloor("Floor 2 bridge corridor", 59.2f, 9.1f, 9.0f, 2.8f, SpaceType.Corridor);
        CreateFloor("Floor 2 east upper corridor", 82.5f, 20.0f, 37.0f, 2.4f, SpaceType.Corridor);
        CreateFloor("Floor 2 east lower corridor", 84.0f, 8.9f, 37.0f, 2.8f, SpaceType.Corridor);
        CreateFloor("Floor 2 east core corridor", 69.0f, 14.0f, 3.0f, 9.8f, SpaceType.Corridor);
        CreateFloor("Lobby to room 219 hallway", 95.5f, 19.35f, 11.0f, 3.7f, SpaceType.Corridor);
        CreateFloor("Lobby to rooms 225 corridor", 95.5f, 8.8f, 11.0f, 3.6f, SpaceType.Corridor);
    }

    private void BuildSecondFloorStructuralDeck()
    {
        // The deck closes the visual gap between floors. Rectangular cells are omitted at
        // each stairwell, producing true openings without requiring ProBuilder or boolean meshes.
        Rect[] stairOpenings =
        {
            Rect.MinMaxRect(9.35f, 14.35f, 11.25f, 19.45f),
            Rect.MinMaxRect(38.55f, 13.35f, 40.45f, 18.45f),
            Rect.MinMaxRect(61.65f, 21.0f, 64.35f, 27.0f),
            Rect.MinMaxRect(68.65f, 13.15f, 73.75f, 15.05f),
            Rect.MinMaxRect(88.2f, 10.0f, 94.0f, 16.8f)
        };

        CreateDeckRegion("West Wing Deck", Rect.MinMaxRect(0f, 0f, 55.5f, 31.2f), stairOpenings);
        CreateDeckRegion("Bridge Deck", Rect.MinMaxRect(55.5f, 0f, 64.0f, 27.1f), stairOpenings);
        CreateDeckRegion("East Wing Deck", Rect.MinMaxRect(64.0f, 0f, 101.2f, 31.6f), stairOpenings);
    }

    private void CreateDeckRegion(string regionName, Rect region, Rect[] openings)
    {
        List<float> xCuts = new List<float> { region.xMin, region.xMax };
        List<float> zCuts = new List<float> { region.yMin, region.yMax };

        foreach (Rect opening in openings)
        {
            bool overlaps = opening.xMax > region.xMin && opening.xMin < region.xMax &&
                            opening.yMax > region.yMin && opening.yMin < region.yMax;
            if (!overlaps)
                continue;

            xCuts.Add(Mathf.Clamp(opening.xMin, region.xMin, region.xMax));
            xCuts.Add(Mathf.Clamp(opening.xMax, region.xMin, region.xMax));
            zCuts.Add(Mathf.Clamp(opening.yMin, region.yMin, region.yMax));
            zCuts.Add(Mathf.Clamp(opening.yMax, region.yMin, region.yMax));
        }

        xCuts.Sort();
        zCuts.Sort();
        Transform deckGroup = NewGroup(regionName, floors);

        int piece = 1;
        for (int xIndex = 0; xIndex < xCuts.Count - 1; xIndex++)
        {
            for (int zIndex = 0; zIndex < zCuts.Count - 1; zIndex++)
            {
                float xMin = xCuts[xIndex];
                float xMax = xCuts[xIndex + 1];
                float zMin = zCuts[zIndex];
                float zMax = zCuts[zIndex + 1];
                if (xMax - xMin < 0.01f || zMax - zMin < 0.01f)
                    continue;

                Vector2 center = new Vector2((xMin + xMax) * 0.5f, (zMin + zMax) * 0.5f);
                bool insideOpening = false;
                foreach (Rect opening in openings)
                {
                    if (center.x > opening.xMin && center.x < opening.xMax &&
                        center.y > opening.yMin && center.y < opening.yMax)
                    {
                        insideOpening = true;
                        break;
                    }
                }

                if (insideOpening)
                    continue;

                CreateBox(deckGroup, "Deck Piece " + piece++,
                    new Vector3(center.x, -structuralSlabThickness * 0.5f, center.y),
                    new Vector3(xMax - xMin, structuralSlabThickness, zMax - zMin),
                    materials[SpaceType.Slab]);
            }
        }
    }

    private void BuildSecondFloorDetails()
    {
        CreateColumn("Floor 2 west column", 7.0f, 20.0f);
        CreateColumn("Floor 2 bridge west column", 55.3f, 19.8f);
        CreateColumn("Floor 2 bridge east column", 63.3f, 19.8f);
        CreateColumn("Floor 2 lobby column", 95.5f, 14.0f);
        CreateMarker("Floor 2 North Ground Entrance", new Vector3(101.2f, 0.05f, 14.0f));
        CreateMarker("Floor 2 Engineering Patio Entrance", new Vector3(101.2f, 0.05f, 4.0f));
    }

    private void BuildRaisedNorthTerrain()
    {
        // North is +X in these plans. This volume rises from Floor 1 grade to Floor 2 grade.
        float terrainHeight = floor2Elevation;
        CreateBox(details, "Raised North Terrain",
            new Vector3(108.0f, -terrainHeight * 0.5f, 15.0f),
            new Vector3(14.0f, terrainHeight, 40.0f), materials[SpaceType.Terrain]);

        CreateBox(details, "North Entrance Path",
            new Vector3(104.8f, 0.08f, 14.0f),
            new Vector3(8.0f, 0.16f, 3.4f), materials[SpaceType.Pathway]);
        CreateBox(details, "North Curved Path Approximation",
            new Vector3(108.0f, 0.08f, 24.0f),
            new Vector3(4.2f, 0.16f, 18.0f), materials[SpaceType.Pathway]);
        CreateBox(details, "Engineering Patio",
            new Vector3(104.5f, 0.08f, 4.0f),
            new Vector3(6.5f, 0.16f, 8.0f), materials[SpaceType.Pathway]);
    }

    private void BuildCorridorsAndBreezeway()
    {
        CreateFloor("West vertical corridor", 7.5f, 14.25f, 3.2f, 14.3f, SpaceType.Corridor);
        CreateFloor("Left north corridor", 29.0f, 19.7f, 44.0f, 2.2f, SpaceType.Corridor);
        CreateFloor("Left south corridor", 30.0f, 8.6f, 42.0f, 1.8f, SpaceType.Corridor);
        CreateFloor("Left center spine", 43.4f, 14.2f, 3.0f, 12.8f, SpaceType.Corridor);
        CreateFloor("Breezeway", 58.8f, 8.6f, 8.6f, 3.2f, SpaceType.Breezeway);

        CreateFloor("Right west corridor", 70.0f, 10.2f, 14.0f, 3.2f, SpaceType.Corridor);
        CreateFloor("Right vertical corridor", 74.3f, 14.4f, 2.2f, 11.6f, SpaceType.Corridor);
        CreateFloor("Right middle corridor", 87.2f, 12.6f, 24.0f, 2.0f, SpaceType.Corridor);
        CreateFloor("Right lower corridor", 87.5f, 6.35f, 24.8f, 1.3f, SpaceType.Corridor);
        CreateFloor("Right cross corridor", 87.2f, 8.8f, 1.7f, 8.9f, SpaceType.Corridor);
    }

    private void CreateRoom(RoomSpec room)
    {
        Transform roomGroup = NewGroup("Room " + room.Name, currentLevel);
        CreateBox(roomGroup, "Floor", new Vector3(room.X, floorThickness * 0.5f, room.Z),
            new Vector3(room.Width, floorThickness, room.Depth), materials[room.Type]);

        AddHorizontalWall(roomGroup, room.Name + " North Wall", room.X,
            room.Z + room.Depth * 0.5f, room.Width, room.Door == DoorSide.North, room.DoorOffset);
        AddHorizontalWall(roomGroup, room.Name + " South Wall", room.X,
            room.Z - room.Depth * 0.5f, room.Width, room.Door == DoorSide.South, room.DoorOffset);
        AddVerticalWall(roomGroup, room.Name + " East Wall", room.X + room.Width * 0.5f,
            room.Z, room.Depth, room.Door == DoorSide.East, room.DoorOffset);
        AddVerticalWall(roomGroup, room.Name + " West Wall", room.X - room.Width * 0.5f,
            room.Z, room.Depth, room.Door == DoorSide.West, room.DoorOffset);

        if (createRoomLabels)
            CreateLabel(room.Name, new Vector3(room.X, floorThickness + 0.025f, room.Z));
    }

    private void AddHorizontalWall(Transform parent, string name, float centerX, float z,
        float length, bool hasDoor, float normalizedDoorOffset)
    {
        if (!hasDoor || length <= doorWidth + wallThickness * 2f)
        {
            CreateWallBox(parent, name, new Vector3(centerX, wallHeight * 0.5f, z),
                new Vector3(length, wallHeight, wallThickness));
            return;
        }

        float usableTravel = Mathf.Max(0f, length - doorWidth - wallThickness * 2f);
        float doorCenter = centerX + normalizedDoorOffset * usableTravel;
        float wallStart = centerX - length * 0.5f;
        float wallEnd = centerX + length * 0.5f;
        float leftLength = doorCenter - doorWidth * 0.5f - wallStart;
        float rightLength = wallEnd - (doorCenter + doorWidth * 0.5f);

        if (leftLength > 0.02f)
            CreateWallBox(parent, name + " L", new Vector3(wallStart + leftLength * 0.5f, wallHeight * 0.5f, z),
                new Vector3(leftLength, wallHeight, wallThickness));
        if (rightLength > 0.02f)
            CreateWallBox(parent, name + " R", new Vector3(wallEnd - rightLength * 0.5f, wallHeight * 0.5f, z),
                new Vector3(rightLength, wallHeight, wallThickness));
    }

    private void AddVerticalWall(Transform parent, string name, float x, float centerZ,
        float length, bool hasDoor, float normalizedDoorOffset)
    {
        if (!hasDoor || length <= doorWidth + wallThickness * 2f)
        {
            CreateWallBox(parent, name, new Vector3(x, wallHeight * 0.5f, centerZ),
                new Vector3(wallThickness, wallHeight, length));
            return;
        }

        float usableTravel = Mathf.Max(0f, length - doorWidth - wallThickness * 2f);
        float doorCenter = centerZ + normalizedDoorOffset * usableTravel;
        float wallStart = centerZ - length * 0.5f;
        float wallEnd = centerZ + length * 0.5f;
        float bottomLength = doorCenter - doorWidth * 0.5f - wallStart;
        float topLength = wallEnd - (doorCenter + doorWidth * 0.5f);

        if (bottomLength > 0.02f)
            CreateWallBox(parent, name + " A", new Vector3(x, wallHeight * 0.5f, wallStart + bottomLength * 0.5f),
                new Vector3(wallThickness, wallHeight, bottomLength));
        if (topLength > 0.02f)
            CreateWallBox(parent, name + " B", new Vector3(x, wallHeight * 0.5f, wallEnd - topLength * 0.5f),
                new Vector3(wallThickness, wallHeight, topLength));
    }

    private void CreateWallBox(Transform parent, string name, Vector3 position, Vector3 size)
    {
        GameObject wall = CreateBox(parent, name, position, size, materials[SpaceType.Wall]);
        wall.transform.SetParent(walls, true);
    }

    private void CreateFloor(string name, float x, float z, float width, float depth, SpaceType type)
    {
        CreateBox(floors, name, new Vector3(x, floorThickness * 0.5f, z),
            new Vector3(width, floorThickness, depth), materials[type]);
    }

    private void BuildStairs()
    {
        CreateStairFlight("Left Stair", new Vector3(10.3f, floorThickness, 14.7f), true);
        CreateStairFlight("Left Core Stair", new Vector3(39.5f, floorThickness, 13.7f), true);
        CreateStairFlight("Bridge Stair", new Vector3(62.8f, floorThickness, 21.2f), true);
        CreateStairFlight("Right Core Stair", new Vector3(69.0f, floorThickness, 14.1f), false);
        CreateLShapedLobbyStair("Main Lobby L Stair", new Vector3(89.0f, floorThickness, 10.25f));
    }

    private void CreateStairFlight(string name, Vector3 start, bool alongZ)
    {
        Transform group = NewGroup(name, details);
        const int stepCount = 18;
        float rise = floor2Elevation / stepCount;
        const float run = 0.25f;
        const float width = 1.25f;

        for (int i = 0; i < stepCount; i++)
        {
            float height = rise * (i + 1);
            Vector3 position = start + (alongZ
                ? new Vector3(0f, height * 0.5f, i * run)
                : new Vector3(i * run, height * 0.5f, 0f));
            Vector3 size = alongZ
                ? new Vector3(width, height, run)
                : new Vector3(run, height, width);
            Material tread = i % 2 == 0 ? materials[SpaceType.StairYellow] : materials[SpaceType.StairBlack];
            CreateBox(group, "Step " + (i + 1), position, size, tread);

            if (i % 4 == 0 || i == stepCount - 1)
            {
                float railY = start.y + height + 0.48f;
                if (alongZ)
                {
                    CreateRailPost(group, new Vector3(position.x - width * 0.5f, railY, position.z));
                    CreateRailPost(group, new Vector3(position.x + width * 0.5f, railY, position.z));
                }
                else
                {
                    CreateRailPost(group, new Vector3(position.x, railY, position.z - width * 0.5f));
                    CreateRailPost(group, new Vector3(position.x, railY, position.z + width * 0.5f));
                }
            }
        }
    }

    private void CreateLShapedLobbyStair(string name, Vector3 start)
    {
        Transform group = NewGroup(name, details);
        const int stepsPerFlight = 9;
        const float run = 0.27f;
        const float width = 1.5f;
        const float landingSize = 1.6f;
        float rise = floor2Elevation / (stepsPerFlight * 2f);

        // First flight travels toward +Z.
        for (int i = 0; i < stepsPerFlight; i++)
        {
            float height = rise * (i + 1);
            Vector3 position = new Vector3(start.x, start.y + height * 0.5f, start.z + i * run);
            Material tread = i % 2 == 0 ? materials[SpaceType.StairYellow] : materials[SpaceType.StairBlack];
            CreateBox(group, "Lower Flight Step " + (i + 1), position,
                new Vector3(width, height, run), tread);

            if (i % 3 == 0 || i == stepsPerFlight - 1)
            {
                CreateRailPost(group, new Vector3(position.x - width * 0.5f, start.y + height + 0.48f, position.z));
                CreateRailPost(group, new Vector3(position.x + width * 0.5f, start.y + height + 0.48f, position.z));
            }
        }

        float halfHeight = rise * stepsPerFlight;
        float landingZ = start.z + stepsPerFlight * run + landingSize * 0.5f;
        CreateBox(group, "L Stair Landing",
            new Vector3(start.x, start.y + halfHeight - 0.09f, landingZ),
            new Vector3(landingSize, 0.18f, landingSize), materials[SpaceType.StairYellow]);

        // Second flight turns ninety degrees and travels toward +X.
        float secondStartX = start.x + landingSize * 0.5f;
        for (int i = 0; i < stepsPerFlight; i++)
        {
            float height = rise * (i + 1);
            Vector3 position = new Vector3(secondStartX + i * run + run * 0.5f,
                start.y + halfHeight + height * 0.5f, landingZ);
            Material tread = i % 2 == 0 ? materials[SpaceType.StairYellow] : materials[SpaceType.StairBlack];
            CreateBox(group, "Upper Flight Step " + (i + 1), position,
                new Vector3(run, height, width), tread);

            if (i % 3 == 0 || i == stepsPerFlight - 1)
            {
                CreateRailPost(group, new Vector3(position.x, start.y + halfHeight + height + 0.48f,
                    position.z - width * 0.5f));
                CreateRailPost(group, new Vector3(position.x, start.y + halfHeight + height + 0.48f,
                    position.z + width * 0.5f));
            }
        }

        // Red guards around the inside and outside corners make the L shape clear from above.
        CreateBox(group, "Lower Flight Red Side",
            new Vector3(start.x - width * 0.5f, start.y + halfHeight * 0.5f + 0.45f,
                start.z + stepsPerFlight * run * 0.5f),
            new Vector3(0.10f, halfHeight + 0.9f, stepsPerFlight * run), materials[SpaceType.RailingRed]);
        CreateBox(group, "Upper Flight Red Side",
            new Vector3(secondStartX + stepsPerFlight * run * 0.5f,
                start.y + halfHeight + halfHeight * 0.5f + 0.45f, landingZ + width * 0.5f),
            new Vector3(stepsPerFlight * run, halfHeight + 0.9f, 0.10f), materials[SpaceType.RailingRed]);
    }

    private void CreateRailPost(Transform parent, Vector3 position)
    {
        CreateBox(parent, "Red Rail Post", position, new Vector3(0.09f, 0.96f, 0.09f),
            materials[SpaceType.RailingRed]);
    }

    private void BuildColumns()
    {
        float[] labXs = { 77.8f, 82.0f, 86.2f };
        float[] labZs = { 18.0f, 23.0f };
        foreach (float x in labXs)
        foreach (float z in labZs)
            CreateColumn("105 Column", x, z);

        float[] southXs = { 80.0f, 85.0f, 90.0f, 95.0f };
        foreach (float x in southXs)
            CreateColumn("South Wing Column", x, 9.5f);

        CreateColumn("Breezeway West Column", 55.4f, 8.6f);
        CreateColumn("Breezeway East Column", 62.6f, 8.6f);
    }

    private void CreateColumn(string name, float x, float z)
    {
        CreateBox(details, name, new Vector3(x, wallHeight * 0.5f, z),
            new Vector3(0.42f, wallHeight, 0.42f), materials[SpaceType.Column]);
    }

    private void BuildEntranceMarkers()
    {
        CreateMarker("West Entrance", new Vector3(0f, 0.05f, 18.3f));
        CreateMarker("Breezeway Entrance", new Vector3(59f, 0.05f, 8.6f));
        CreateMarker("South Entrance", new Vector3(87f, 0.05f, 0f));
    }

    private void CreateMarker(string name, Vector3 position)
    {
        GameObject marker = new GameObject(name);
        marker.transform.SetParent(details, false);
        marker.transform.localPosition = position;
        marker.transform.localRotation = Quaternion.identity;
    }

    private void BuildSceneHelpers()
    {
        GameObject cameraObject = new GameObject("Building Overview Camera");
        cameraObject.transform.SetParent(root, false);
        cameraObject.transform.localPosition = new Vector3(50f, 75f, 16f);
        cameraObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = 53f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 160f;

        if (Object.FindFirstObjectByType<Light>() == null)
        {
            GameObject lightObject = new GameObject("Engineering Building Sun");
            lightObject.transform.SetParent(root, false);
            lightObject.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
        }
    }

    private void CreateLabel(string textValue, Vector3 position)
    {
        GameObject labelObject = new GameObject(textValue + " Label");
        labelObject.transform.SetParent(labels, false);
        labelObject.transform.localPosition = position;
        labelObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        TextMesh textMesh = labelObject.AddComponent<TextMesh>();
        textMesh.text = textValue;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.fontSize = 48;
        textMesh.characterSize = 0.035f;
        textMesh.color = new Color(0.06f, 0.06f, 0.06f, 1f);

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null)
        {
            textMesh.font = font;
            MeshRenderer renderer = labelObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
        }
    }

    private GameObject CreateBox(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.localPosition = position;
        box.transform.localRotation = Quaternion.identity;
        box.transform.localScale = size;
        box.GetComponent<MeshRenderer>().sharedMaterial = material;
        return box;
    }

    private static Transform NewGroup(string name, Transform parent)
    {
        GameObject group = new GameObject(name);
        group.transform.SetParent(parent, false);
        return group.transform;
    }

    private void EnsureMaterialAssets()
    {
        EnsureFolder("Assets", "EngineeringBuildingGenerated");
        EnsureFolder("Assets/EngineeringBuildingGenerated", "Materials");

        materials.Clear();
        materials[SpaceType.Lecture] = GetOrCreateMaterial("Lecture", new Color(0.61f, 0.34f, 0.66f));
        materials[SpaceType.Imagination] = GetOrCreateMaterial("Imagination", new Color(1.00f, 0.49f, 0.02f));
        materials[SpaceType.Realization] = GetOrCreateMaterial("Realization", new Color(0.94f, 0.91f, 0.08f));
        materials[SpaceType.Department] = GetOrCreateMaterial("Department", new Color(0.31f, 0.83f, 0.20f));
        materials[SpaceType.Office] = GetOrCreateMaterial("Office", new Color(0.28f, 0.82f, 0.82f));
        materials[SpaceType.Public] = GetOrCreateMaterial("Public", new Color(0.95f, 0.54f, 0.56f));
        materials[SpaceType.Operations] = GetOrCreateMaterial("Operations", new Color(0.72f, 0.67f, 0.66f));
        materials[SpaceType.Corridor] = GetOrCreateMaterial("Corridor", new Color(0.83f, 0.83f, 0.80f));
        materials[SpaceType.Wall] = GetOrCreateMaterial("Wall", new Color(0.88f, 0.89f, 0.87f));
        materials[SpaceType.Column] = GetOrCreateMaterial("Column", new Color(0.12f, 0.13f, 0.14f));
        materials[SpaceType.Breezeway] = GetOrCreateMaterial("Breezeway", new Color(0.64f, 0.66f, 0.68f));
        materials[SpaceType.Slab] = GetOrCreateMaterial("StructuralSlab", new Color(0.58f, 0.59f, 0.60f));
        materials[SpaceType.StairYellow] = GetOrCreateMaterial("StairYellow", new Color(0.96f, 0.78f, 0.04f));
        materials[SpaceType.StairBlack] = GetOrCreateMaterial("StairBlack", new Color(0.04f, 0.04f, 0.04f));
        materials[SpaceType.RailingRed] = GetOrCreateMaterial("RailingRed", new Color(0.72f, 0.04f, 0.035f));
        materials[SpaceType.Terrain] = GetOrCreateMaterial("RaisedTerrain", new Color(0.26f, 0.42f, 0.20f));
        materials[SpaceType.Pathway] = GetOrCreateMaterial("Pathway", new Color(0.45f, 0.58f, 0.60f));
        AssetDatabase.SaveAssets();
    }

    private static void EnsureFolder(string parent, string child)
    {
        string path = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(path))
            AssetDatabase.CreateFolder(parent, child);
    }

    private static Material GetOrCreateMaterial(string name, Color color)
    {
        string path = MaterialFolder + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }

        material.color = color;
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(material);
        return material;
    }
}

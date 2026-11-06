using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class SimulationManager : MonoBehaviour
{
    private enum SimulationPhase
    {
        SuspectMoving,
        OfficerResponding,
        Complete
    }

    // =========================================================
    // BUILDING 9 v5.4 SETTINGS
    // =========================================================

    private const int TotalFloors = 5;
    private const float FloorHeight = 4.2f;
    private const float FloorMarkerOffset = 0.65f;

    private const float MarkerSize = 1.25f;

    [System.NonSerialized] private float moveDuration = 0.65f;
    [System.NonSerialized] private float pauseBetweenMoves = 0.10f;
    [System.NonSerialized] private float detectionDistance = 1.8f;

    // =========================================================
    // BUILDING REFERENCES
    // =========================================================

    [System.NonSerialized] private Transform buildingRoot;
    [System.NonSerialized] private Transform stairConnections;

    // Index 1..5. Index 0 is unused.
    [System.NonSerialized] private readonly Transform[] floors =
        new Transform[TotalFloors + 1];

    // =========================================================
    // TRACKING
    // =========================================================

    [System.NonSerialized] private SimulationPhase phase =
        SimulationPhase.SuspectMoving;

    [System.NonSerialized] private GameObject suspect;
    [System.NonSerialized] private GameObject officer;

    [System.NonSerialized] private Vector3 suspectCurrentPosition;
    [System.NonSerialized] private Vector3 suspectPreviousPosition;

    [System.NonSerialized] private Vector3 officerCurrentPosition;
    [System.NonSerialized] private Vector3 officerPreviousPosition;

    [System.NonSerialized] private readonly List<Vector3> trackingPath =
        new List<Vector3>();

    private const int RandomRoomTargetCount = TotalFloors;

    private struct RoomDestination
    {
        public string roomNumber;
        public int floorNumber;

        // Original room-label position from Nakul's model.
        public Vector3 position;

        // Nearest legal hallway point outside the room.
        // The marker stops here and the UI reports that the room was reached.
        public Vector3 hallwayPosition;
    }

    [System.NonSerialized] private readonly List<RoomDestination> roomTargets =
        new List<RoomDestination>();

    [System.NonSerialized] private string roomRouteText = "";
    [System.NonSerialized] private string suspectRoomStatus = "";
    [System.NonSerialized] private string officerRoomStatus = "";
    [System.NonSerialized] private string suspectDisplayLabel = "SUSPECT";
    [System.NonSerialized] private string officerDisplayLabel = "LAW ENFORCEMENT";

    private const float RoomArrivalPause = 1.80f;

    // Hallway / wall-aware floor pathfinding.
    // Uses Nakul's generated colliders to avoid walls.
    private const float NavigationGridSize = 0.85f;
    private const float NavigationRadius = 0.28f;
    private const float NavigationHeight = 1.35f;
    private const int NavigationMaxExpandedNodes = 18000;

    [System.NonSerialized] private readonly List<MeshFilter>[] roomFinishMeshes =
        new List<MeshFilter>[TotalFloors + 1];

    private struct GridPathNode
    {
        public Vector2Int cell;
        public float fScore;

        public GridPathNode(
            Vector2Int cellValue,
            float scoreValue
        )
        {
            cell = cellValue;
            fScore = scoreValue;
        }
    }

    [System.NonSerialized] private readonly List<Vector3> suspectHistory =
        new List<Vector3>();

    [System.NonSerialized] private readonly List<Vector3> officerHistory =
        new List<Vector3>();

    [System.NonSerialized] private bool suspectDetectedByOfficer = false;

    [System.NonSerialized] private LineRenderer suspectTrail;
    [System.NonSerialized] private LineRenderer officerTrail;

    // Visible locator poles + labels
    [System.NonSerialized] private LineRenderer suspectLocator;
    [System.NonSerialized] private LineRenderer officerLocator;

    // =========================================================
    // MANUAL 3D VIEW
    // 0 = All Floors, 1..5 = selected floor
    // =========================================================

    [System.NonSerialized] private Camera trackingCamera;
    [System.NonSerialized] private int selectedView = 0;

    // Manual Game-view camera orbit/zoom state
    [System.NonSerialized] private Vector3 cameraTarget;
    [System.NonSerialized] private float cameraDistance = 55f;
    [System.NonSerialized] private float cameraYaw = 225f;
    [System.NonSerialized] private float cameraPitch = 24f;

    [Header("Free-flight camera - hold right mouse")]
    [SerializeField, Min(0.1f)] private float flySpeed = 8f;
    [SerializeField, Min(1f)] private float flyFastMultiplier = 3f;
    [SerializeField, Range(0.01f, 1f)] private float flyMouseSensitivity = 0.15f;

    [System.NonSerialized] private bool freeFlyActive;
    [System.NonSerialized] private float flyYaw;
    [System.NonSerialized] private float flyPitch;
    [System.NonSerialized] private CursorLockMode cursorBeforeFlight;
    [System.NonSerialized] private bool cursorVisibleBeforeFlight;

    private struct FlightInput
    {
        public bool held, pressed, escape, fast, insideView;
        public Vector2 look;
        public Vector3 move;
    }

    // =========================================================
    // START
    // =========================================================

    // A new epoch is issued on every Play entry, even with domain reload off.
    private static int playSessionSerial;
    private static bool sessionStopping;
    [System.NonSerialized] private int initializedSession = -1;
    [System.NonSerialized] private bool restartRequested = true;
    [System.NonSerialized] private bool sessionReady;
    [System.NonSerialized] private string startupError = "";
    [System.NonSerialized] private GameObject runtimeRoot;
    private sealed class CameraState { public Camera camera; public bool enabled; }
    private sealed class ActiveState { public GameObject target; public bool active; }
    [System.NonSerialized] private readonly List<CameraState> originalCameras = new List<CameraState>();
    [System.NonSerialized] private readonly List<ActiveState> originalVisibility = new List<ActiveState>();
    [System.NonSerialized] private readonly List<GameObject> ownedObjects = new List<GameObject>();
    [System.NonSerialized] private readonly List<Material> ownedMaterials = new List<Material>();

    // Scene-only discovery works across Unity 6 releases without the
    // deprecated sort-mode overloads. Called at startup/stop, never per frame.
    static T[] FindLoadedSceneComponents<T>(bool includeInactive) where T : Component
    {
        List<T> result = new List<T>();
        foreach (T component in Resources.FindObjectsOfTypeAll<T>())
        {
            if (component == null) continue;
            GameObject obj = component.gameObject;
            // Exclude prefab assets, unloaded scenes and internal editor objects.
            if (!obj.scene.IsValid() || !obj.scene.isLoaded) continue;
            if (!includeInactive && !obj.activeInHierarchy) continue;
            if ((obj.hideFlags & HideFlags.DontSave) != 0 ||
                (component.hideFlags & HideFlags.DontSave) != 0) continue;
            result.Add(component);
        }
        return result.ToArray();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void BeginPlaySession()
    {
#if UNITY_EDITOR
        RegisterEditorLifecycle();
#else
        sessionStopping = false;
        unchecked { playSessionSerial++; }
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void RequestLoadedSceneStart()
    {
        foreach (SimulationManager manager in FindLoadedSceneComponents<SimulationManager>(true))
            if (manager.isActiveAndEnabled) manager.restartRequested = true;
    }

#if UNITY_EDITOR
    private static bool editorSessionActive;
    private static readonly List<SimulationManager> editorManagers = new List<SimulationManager>();

    [UnityEditor.InitializeOnLoadMethod]
    static void RegisterEditorLifecycle()
    {
        UnityEditor.EditorApplication.playModeStateChanged -= EditorPlayModeChanged;
        UnityEditor.EditorApplication.playModeStateChanged += EditorPlayModeChanged;
        UnityEditor.EditorApplication.update -= EditorStartupTick;
        UnityEditor.EditorApplication.update += EditorStartupTick;
        // Recover after a script refresh during Play, when no new Play event
        // will arrive. The delayed callback runs after the scene is available.
        UnityEditor.EditorApplication.delayCall -= RecoverPlayingEditorSession;
        UnityEditor.EditorApplication.delayCall += RecoverPlayingEditorSession;
    }

    [UnityEditor.InitializeOnEnterPlayMode]
    static void PrepareEditorPlayEntry(UnityEditor.EnterPlayModeOptions options)
    {
        sessionStopping = false;
        editorSessionActive = false;
        editorManagers.Clear();
        RegisterEditorLifecycle();
    }

    static void RegisterEditorManager(SimulationManager manager)
    {
        if (manager == null || !manager.gameObject.scene.IsValid() || !manager.gameObject.scene.isLoaded) return;
        if (!editorManagers.Contains(manager)) editorManagers.Add(manager);
    }

    static void StartEditorPlaySession()
    {
        if (!UnityEditor.EditorApplication.isPlaying) return;
        // This is the authoritative reset for the editor Play button. Never
        // depend on a runtime-load callback to undo the last Stop operation.
        sessionStopping = false;
        if (editorSessionActive) { EditorStartupTick();return; }
        editorSessionActive = true;
        unchecked { playSessionSerial++; }
        editorManagers.Clear();
        int activeManagers = 0;
        foreach (SimulationManager manager in FindLoadedSceneComponents<SimulationManager>(true))
        {
            RegisterEditorManager(manager);
            if (manager.isActiveAndEnabled) { manager.restartRequested = true;activeManagers++; }
        }
        Debug.Log("SHIELD v1.3.1 - Play entered; " + activeManagers + " active simulator(s).");
        if (activeManagers == 0)
            Debug.LogWarning("SHIELD: enable the SimulationManager component on an active Simulation GameObject in the current scene.");
        EditorStartupTick();
    }

    static void RecoverPlayingEditorSession()
    {
        if (UnityEditor.EditorApplication.isPlaying && !sessionStopping && !editorSessionActive)
            StartEditorPlaySession();
    }

    static void EditorStartupTick()
    {
        if (!UnityEditor.EditorApplication.isPlaying || !editorSessionActive) return;
        // Editor mode is authoritative. A stale quit callback must not latch
        // the runtime shutdown flag while this editor Play session is active.
        sessionStopping = false;
        // Cached scene managers avoid searching the entire building per frame.
        // The fallback also works if Unity skips this component's Start/Update.
        for (int i = 0; i < editorManagers.Count; i++)
        {
            SimulationManager manager = editorManagers[i];
            if (manager != null && manager.isActiveAndEnabled) manager.EnsureSimulationStarted();
        }
    }

    static void EditorPlayModeChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state == UnityEditor.PlayModeStateChange.EnteredPlayMode)
        {
            StartEditorPlaySession();
        }
        else if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
        {
            sessionStopping = true;editorSessionActive = false;
            UnityEditor.EditorApplication.delayCall -= RecoverPlayingEditorSession;
            foreach (SimulationManager manager in FindLoadedSceneComponents<SimulationManager>(true))
                manager.StopSimulationRuntime();
            editorManagers.Clear();
        }
        else if (state == UnityEditor.PlayModeStateChange.EnteredEditMode)
        {
            sessionStopping = false;editorSessionActive = false;editorManagers.Clear();
        }
        else if (state == UnityEditor.PlayModeStateChange.ExitingEditMode)
        {
            sessionStopping = false;editorSessionActive = false;
        }
    }
#endif


    void OnEnable()
    {
        if (Application.isPlaying) restartRequested = true;
#if UNITY_EDITOR
        RegisterEditorManager(this);
#endif
    }

    void Start() { EnsureSimulationStarted(); }

    void EnsureSimulationStarted()
    {
        if (!Application.isPlaying || !isActiveAndEnabled || sessionStopping) return;
#if UNITY_EDITOR
        if (!editorSessionActive) return;
#endif
        if (sessionReady && (runtimeRoot == null || trackingCamera == null || suspect == null || officer == null))
            restartRequested = true;
        if (restartRequested || initializedSession != playSessionSerial) RestartSimulation();
    }

    public void RestartSimulation()
    {
        if (!Application.isPlaying || !isActiveAndEnabled || sessionStopping) return;
#if UNITY_EDITOR
        if (!editorSessionActive) return;
#endif
        StopSimulationRuntime();
        initializedSession = playSessionSerial;
        restartRequested = false;
        ResetSessionState();
        try
        {
            GameObject building = GameObject.Find("Engineering Building");
            if (building == null) throw new System.InvalidOperationException("Engineering Building not found. Open the generated building scene first.");
            buildingRoot = building.transform;
            stairConnections = buildingRoot.Find("Stair connections");
            for (int floorNumber = 1; floorNumber <= TotalFloors; floorNumber++)
            {
                floors[floorNumber] = buildingRoot.Find("Floor " + floorNumber);
                if (floors[floorNumber] == null)
                    throw new System.InvalidOperationException("Floor " + floorNumber + " was not found under Engineering Building.");
            }
            CaptureVisibility();
            ActivateAllNavigationGeometry();
            Physics.SyncTransforms();

            BuildRoomFinishCache();
            BuildFiveFloorPath();
            if (trackingPath.Count < 2) throw new System.InvalidOperationException("Could not create the five-floor tracking path.");

            runtimeRoot = new GameObject("SHIELD Simulation Runtime");
            SetupManual3DCamera();
            CreateSuspect();CreateOfficer();CreateTrails();CreateLocators();
            ShowAllFloors();
            sessionReady = true;
            Debug.Log("SHIELD room-based simulation started - " + roomRouteText);
            StartCoroutine(RunSimulation());
        }
        catch (System.Exception error)
        {
            StopSimulationRuntime();
            initializedSession = playSessionSerial;restartRequested = false;
            startupError = error.Message;
            Debug.LogError("SHIELD simulation could not start: " + error);
        }
    }

    void ResetSessionState()
    {
        phase = SimulationPhase.SuspectMoving;
        suspectDetectedByOfficer = false;
        trackingPath.Clear();suspectHistory.Clear();officerHistory.Clear();roomTargets.Clear();roomRouteText = "";suspectRoomStatus = "";officerRoomStatus = "";suspectDisplayLabel = "SUSPECT";officerDisplayLabel = "LAW ENFORCEMENT";
        suspectCurrentPosition = Vector3.zero;suspectPreviousPosition = Vector3.zero;
        officerCurrentPosition = Vector3.zero;officerPreviousPosition = Vector3.zero;
        selectedView = 0;cameraTarget = Vector3.zero;cameraDistance = 55f;cameraYaw = 225f;cameraPitch = 24f;
        flyYaw = 0f;flyPitch = 0f;freeFlyActive = false;
        buildingRoot = null;stairConnections = null;
        System.Array.Clear(floors, 0, floors.Length);

        for (int floorNumber = 1; floorNumber <= TotalFloors; floorNumber++)
            roomFinishMeshes[floorNumber] = null;

        startupError = "";
    }

    void CaptureVisibility()
    {
        for (int n = 1; n <= TotalFloors; n++) RememberActive(floors[n].gameObject);
        if (stairConnections == null) return;
        RememberActive(stairConnections.gameObject);
        for (int n = 1; n <= TotalFloors; n++)
        {
            Transform group = stairConnections.Find("From Floor " + n);
            if (group != null) RememberActive(group.gameObject);
        }
    }
    void RememberActive(GameObject target)
    { originalVisibility.Add(new ActiveState { target = target, active = target.activeSelf }); }

    void ActivateAllNavigationGeometry()
    {
        for (
            int floorNumber = 1;
            floorNumber <= TotalFloors;
            floorNumber++
        )
        {
            if (floors[floorNumber] != null)
            {
                floors[floorNumber].gameObject.SetActive(
                    true
                );
            }
        }

        if (stairConnections != null)
        {
            stairConnections.gameObject.SetActive(
                true
            );

            for (
                int floorNumber = 1;
                floorNumber <= TotalFloors;
                floorNumber++
            )
            {
                Transform group =
                    stairConnections.Find(
                        "From Floor " +
                        floorNumber
                    );

                if (group != null)
                {
                    group.gameObject.SetActive(
                        true
                    );
                }
            }
        }
    }

    void BuildRoomFinishCache()
    {
        for (
            int floorNumber = 1;
            floorNumber <= TotalFloors;
            floorNumber++
        )
        {
            roomFinishMeshes[floorNumber] =
                new List<MeshFilter>();

            Transform surfaces =
                floors[floorNumber].Find(
                    "Floors"
                );

            if (surfaces == null)
                continue;

            MeshFilter[] meshes =
                surfaces.GetComponentsInChildren
                    <MeshFilter>(true);

            foreach (
                MeshFilter meshFilter
                in meshes
            )
            {
                if (
                    meshFilter == null ||
                    meshFilter.sharedMesh == null
                )
                {
                    continue;
                }

                string lowerName =
                    meshFilter.gameObject
                        .name
                        .ToLowerInvariant();

                // Nakul's category overlays identify rooms / non-corridor
                // floor areas. Corridor is the uncovered continuous slab.
                if (
                    lowerName.Contains(
                        "source finishes"
                    )
                )
                {
                    roomFinishMeshes[floorNumber]
                        .Add(
                            meshFilter
                        );
                }
            }

            Debug.Log(
                "Floor " +
                floorNumber +
                ": cached " +
                roomFinishMeshes[floorNumber].Count +
                " room-finish meshes for hallway-only routing."
            );
        }
    }

    GameObject TrackRuntimeObject(GameObject obj)
    {
        ownedObjects.Add(obj);
        if (runtimeRoot != null) obj.transform.SetParent(runtimeRoot.transform, true);
        return obj;
    }
    GameObject CreateRuntimeObject(string objectName)
    { return TrackRuntimeObject(new GameObject(objectName)); }
    GameObject CreateRuntimePrimitive(PrimitiveType type)
    { return TrackRuntimeObject(GameObject.CreatePrimitive(type)); }
    Material CreateRuntimeMaterial(Shader shader, Color color)
    {
        Material material = new Material(shader);material.color = color;
        ownedMaterials.Add(material);return material;
    }

    static void DestroyOwned(UnityEngine.Object target)
    {
        if (target == null) return;
        if (Application.isPlaying) Destroy(target);else DestroyImmediate(target);
    }
    void StopSimulationRuntime()
    {
        StopAllCoroutines();EndFreeFly();sessionReady = false;
        // Markers change parent as they move between floors. Track ownership
        // explicitly, so cleanup never depends on their current parent/name.
        foreach (GameObject obj in ownedObjects)
        {
            if (obj == null) continue;
            obj.SetActive(false);
            if (runtimeRoot == null || !obj.transform.IsChildOf(runtimeRoot.transform)) DestroyOwned(obj);
        }
        if (runtimeRoot != null) { runtimeRoot.SetActive(false);DestroyOwned(runtimeRoot); }
        ownedObjects.Clear();runtimeRoot = null;
        foreach (Material material in ownedMaterials) DestroyOwned(material);
        ownedMaterials.Clear();
        foreach (CameraState state in originalCameras)
            if (state.camera != null) state.camera.enabled = state.enabled;
        originalCameras.Clear();
        foreach (ActiveState state in originalVisibility)
            if (state.target != null) state.target.SetActive(state.active);
        originalVisibility.Clear();
        suspect = null;officer = null;trackingCamera = null;
        suspectTrail = null;officerTrail = null;suspectLocator = null;officerLocator = null;
        initializedSession = -1;
    }

    void OnDisable() { StopSimulationRuntime(); }
    void OnDestroy() { StopSimulationRuntime(); }
    void OnApplicationQuit() { sessionStopping = true;StopSimulationRuntime(); }


    // =========================================================
    // PATH
    // =========================================================

    void BuildFiveFloorPath()
    {
        trackingPath.Clear();
        roomTargets.Clear();

        List<RoomDestination> allRooms =
            GetAllRoomDestinations();

        if (allRooms.Count < TotalFloors)
        {
            throw new System.InvalidOperationException(
                "Not enough numbered rooms with hallway access were found for Floors 1-5."
            );
        }

        ChooseRandomRoomTargets(
            allRooms,
            RandomRoomTargetCount
        );

        Vector3 currentPosition =
            GetStartingPoint();

        int currentFloor =
            1;

        // Snap the starting location to a legal hallway cell.
        Vector3 legalStart =
            FindNearestHallwayPoint(
                currentFloor,
                currentPosition
            );

        if (legalStart.x > -9000f)
            currentPosition = legalStart;

        AddPathPoint(
            currentPosition
        );

        for (
            int targetIndex = 0;
            targetIndex < roomTargets.Count;
            targetIndex++
        )
        {
            RoomDestination target =
                roomTargets[targetIndex];

            // Because this demo intentionally picks one room per floor
            // in ascending order, this usually runs only upward.
            while (
                currentFloor !=
                target.floorNumber
            )
            {
                if (
                    currentFloor <
                    target.floorNumber
                )
                {
                    List<Vector3> stairRoute =
                        GetGeneratedStairTransition(
                            currentFloor,
                            currentPosition
                        );

                    if (
                        stairRoute.Count == 0
                    )
                    {
                        stairRoute =
                            BuildFallbackVerticalTransition(
                                currentFloor,
                                currentPosition,
                                true
                            );
                    }
                    else
                    {
                        // FIRST: walk through the hallway to the stair entrance.
                        Vector3 stairHallwayEntry =
                            FindNearestHallwayPoint(
                                currentFloor,
                                stairRoute[0]
                            );

                        if (
                            stairHallwayEntry.x >
                            -9000f
                        )
                        {
                            AddHallwayRoute(
                                currentFloor,
                                currentPosition,
                                stairHallwayEntry
                            );

                            currentPosition =
                                stairHallwayEntry;
                        }
                    }

                    // SECOND: climb the generated stair geometry.
                    foreach (
                        Vector3 stairPoint
                        in stairRoute
                    )
                    {
                        AddPathPoint(
                            stairPoint
                        );
                    }

                    currentFloor++;

                    Vector3 rawStairExit =
                        trackingPath[
                            trackingPath.Count - 1
                        ];

                    rawStairExit.y =
                        GetFloorMarkerY(
                            currentFloor
                        );

                    // THIRD: snap the stair exit back onto the hallway.
                    Vector3 stairHallwayExit =
                        FindNearestHallwayPoint(
                            currentFloor,
                            rawStairExit
                        );

                    if (
                        stairHallwayExit.x >
                        -9000f
                    )
                    {
                        AddPathPoint(
                            stairHallwayExit
                        );

                        currentPosition =
                            stairHallwayExit;
                    }
                    else
                    {
                        AddPathPoint(
                            rawStairExit
                        );

                        currentPosition =
                            rawStairExit;
                    }
                }
                else
                {
                    int lowerFloor =
                        currentFloor - 1;

                    List<Vector3> stairRoute =
                        GetGeneratedStairTransition(
                            lowerFloor,
                            currentPosition
                        );

                    if (
                        stairRoute.Count > 0
                    )
                    {
                        stairRoute.Reverse();

                        Vector3 stairHallwayEntry =
                            FindNearestHallwayPoint(
                                currentFloor,
                                stairRoute[0]
                            );

                        if (
                            stairHallwayEntry.x >
                            -9000f
                        )
                        {
                            AddHallwayRoute(
                                currentFloor,
                                currentPosition,
                                stairHallwayEntry
                            );

                            currentPosition =
                                stairHallwayEntry;
                        }
                    }
                    else
                    {
                        stairRoute =
                            BuildFallbackVerticalTransition(
                                currentFloor,
                                currentPosition,
                                false
                            );
                    }

                    foreach (
                        Vector3 stairPoint
                        in stairRoute
                    )
                    {
                        AddPathPoint(
                            stairPoint
                        );
                    }

                    currentFloor--;

                    Vector3 rawStairExit =
                        trackingPath[
                            trackingPath.Count - 1
                        ];

                    rawStairExit.y =
                        GetFloorMarkerY(
                            currentFloor
                        );

                    Vector3 stairHallwayExit =
                        FindNearestHallwayPoint(
                            currentFloor,
                            rawStairExit
                        );

                    if (
                        stairHallwayExit.x >
                        -9000f
                    )
                    {
                        AddPathPoint(
                            stairHallwayExit
                        );

                        currentPosition =
                            stairHallwayExit;
                    }
                    else
                    {
                        AddPathPoint(
                            rawStairExit
                        );

                        currentPosition =
                            rawStairExit;
                    }
                }
            }

            // Finally walk ONLY through hallway cells to the room's
            // nearest hallway access point. Do not enter the room interior.
            AddHallwayRoute(
                currentFloor,
                currentPosition,
                target.hallwayPosition
            );

            AddPathPoint(
                target.hallwayPosition
            );

            currentPosition =
                target.hallwayPosition;

            Debug.Log(
                "ROOM TARGET " +
                (targetIndex + 1) +
                ": " +
                target.roomNumber +
                " (Floor " +
                target.floorNumber +
                ") hallway access = " +
                target.hallwayPosition
            );
        }

        roomRouteText =
            "Room Route: " +
            string.Join(
                " -> ",
                roomTargets.Select(
                    room =>
                        "F" +
                        room.floorNumber +
                        ":" +
                        room.roomNumber
                )
            );

        Debug.Log(
            roomRouteText
        );
    }

    void AddHallwayRoute(
        int floorNumber,
        Vector3 start,
        Vector3 destination
    )
    {
        List<Vector3> hallwayPath =
            FindWallAwareFloorPath(
                floorNumber,
                start,
                destination
            );

        if (
            hallwayPath.Count == 0
        )
        {
            Debug.LogWarning(
                "No hallway-only route found on Floor " +
                floorNumber +
                " from " +
                start +
                " to " +
                destination +
                ". Holding at the current hallway point instead of cutting through a wall."
            );

            return;
        }

        foreach (
            Vector3 point
            in hallwayPath
        )
        {
            AddPathPoint(
                point
            );
        }
    }

    List<RoomDestination> GetAllRoomDestinations()
    {
        List<RoomDestination> rooms =
            new List<RoomDestination>();

        for (
            int floorNumber = 1;
            floorNumber <= TotalFloors;
            floorNumber++
        )
        {
            Transform labels =
                floors[floorNumber].Find(
                    "Room labels"
                );

            if (labels == null)
                continue;

            foreach (
                Transform child
                in labels
            )
            {
                string roomName =
                    child.name.Trim();

                if (
                    !LooksLikeRoomNumber(
                        roomName,
                        floorNumber
                    )
                )
                {
                    continue;
                }

                Vector3 roomPosition =
                    child.position;

                roomPosition.y =
                    GetFloorMarkerY(
                        floorNumber
                    );

                Vector3 hallwayPosition =
                    FindNearestHallwayPoint(
                        floorNumber,
                        roomPosition
                    );

                if (
                    hallwayPosition.x <
                    -9000f
                )
                {
                    Debug.LogWarning(
                        "Skipping room " +
                        roomName +
                        " because no hallway access point was found."
                    );

                    continue;
                }

                RoomDestination room =
                    new RoomDestination
                    {
                        roomNumber = roomName,
                        floorNumber = floorNumber,
                        position = roomPosition,
                        hallwayPosition = hallwayPosition
                    };

                rooms.Add(
                    room
                );
            }
        }

        Debug.Log(
            "Found " +
            rooms.Count +
            " numbered room destinations."
        );

        return rooms;
    }

    bool LooksLikeRoomNumber(
        string roomName,
        int floorNumber
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                roomName
            )
        )
        {
            return false;
        }

        int digitCount = 0;

        foreach (
            char character
            in roomName
        )
        {
            if (
                char.IsDigit(
                    character
                )
            )
            {
                digitCount++;
            }
        }

        if (digitCount < 3)
            return false;

        // Building 9 room labels normally begin with the floor number,
        // e.g. 215, 307, 504, 512.
        return roomName[0] ==
            floorNumber
                .ToString()[0];
    }

    void ChooseRandomRoomTargets(
        List<RoomDestination> allRooms,
        int requestedCount
    )
    {
        roomTargets.Clear();

        // Professor-requested behavior:
        // exactly ONE random room from each floor,
        // always in Floor 1 -> 2 -> 3 -> 4 -> 5 order.
        for (
            int floorNumber = 1;
            floorNumber <= TotalFloors;
            floorNumber++
        )
        {
            List<RoomDestination> floorRooms =
                allRooms
                    .Where(
                        room =>
                            room.floorNumber ==
                            floorNumber
                    )
                    .ToList();

            if (floorRooms.Count == 0)
            {
                throw new System.InvalidOperationException(
                    "No numbered room labels were found on Floor " +
                    floorNumber +
                    "."
                );
            }

            int randomIndex =
                Random.Range(
                    0,
                    floorRooms.Count
                );

            RoomDestination selected =
                floorRooms[
                    randomIndex
                ];

            roomTargets.Add(
                selected
            );
        }
    }

    int FindRoomTargetIndexAtPosition(
        Vector3 position
    )
    {
        for (
            int i = 0;
            i < roomTargets.Count;
            i++
        )
        {
            if (
                Vector3.Distance(
                    position,
                    roomTargets[i].hallwayPosition
                ) <
                0.12f
            )
            {
                return i;
            }
        }

        return -1;
    }

    void SetSuspectHeadingToRoom(
        int roomIndex
    )
    {
        if (
            roomIndex < 0 ||
            roomIndex >= roomTargets.Count
        )
        {
            return;
        }

        RoomDestination room =
            roomTargets[
                roomIndex
            ];

        suspectRoomStatus =
            "Heading to Room " +
            room.roomNumber +
            " on Floor " +
            room.floorNumber;

        suspectDisplayLabel =
            "SUSPECT | TO ROOM " +
            room.roomNumber;
    }

    void SetOfficerHeadingToRoom(
        int roomIndex
    )
    {
        if (
            roomIndex < 0 ||
            roomIndex >= roomTargets.Count
        )
        {
            return;
        }

        RoomDestination room =
            roomTargets[
                roomIndex
            ];

        officerRoomStatus =
            "Heading to Room " +
            room.roomNumber +
            " on Floor " +
            room.floorNumber;

        officerDisplayLabel =
            "LAW ENFORCEMENT | TO ROOM " +
            room.roomNumber;
    }

    List<Vector3> BuildFallbackVerticalTransition(
        int currentFloor,
        Vector3 currentPosition,
        bool goingUp
    )
    {
        List<Vector3> points =
            new List<Vector3>();

        int destinationFloor =
            goingUp
                ? currentFloor + 1
                : currentFloor - 1;

        float destinationY =
            GetFloorMarkerY(
                destinationFloor
            );

        for (
            int step = 1;
            step <= 16;
            step++
        )
        {
            float t =
                step / 16f;

            points.Add(
                new Vector3(
                    currentPosition.x,
                    Mathf.Lerp(
                        currentPosition.y,
                        destinationY,
                        t
                    ),
                    currentPosition.z
                )
            );
        }

        return points;
    }

    void AddRoomApproach(
        int floorNumber,
        Vector3 start,
        Vector3 roomPosition
    )
    {
        Vector3 target =
            roomPosition;

        start.y =
            GetFloorMarkerY(
                floorNumber
            );

        target.y =
            GetFloorMarkerY(
                floorNumber
            );

        List<Vector3> hallwayPath =
            FindWallAwareFloorPath(
                floorNumber,
                start,
                target
            );

        if (hallwayPath.Count > 0)
        {
            foreach (
                Vector3 point
                in hallwayPath
            )
            {
                AddPathPoint(
                    point
                );
            }

            return;
        }

        // Fallback only if a grid route cannot be found.
        // This keeps the simulation usable but logs the problem
        // so we know which room/floor needs a better waypoint.
        Debug.LogWarning(
            "No wall-aware route found on Floor " +
            floorNumber +
            " from " +
            start +
            " to " +
            roomPosition +
            ". Using a simple approach as fallback."
        );

        Vector3 delta =
            target -
            start;

        delta.y = 0f;

        float distance =
            delta.magnitude;

        if (distance < 0.5f)
            return;

        int segments =
            Mathf.Clamp(
                Mathf.CeilToInt(
                    distance / 2.0f
                ),
                2,
                10
            );

        for (
            int i = 1;
            i < segments;
            i++
        )
        {
            Vector3 point =
                Vector3.Lerp(
                    start,
                    target,
                    i /
                    (float)segments
                );

            point.y =
                GetFloorMarkerY(
                    floorNumber
                );

            AddPathPoint(
                point
            );
        }
    }

    Vector3 FindNearestHallwayPoint(
        int floorNumber,
        Vector3 desiredPoint
    )
    {
        Bounds bounds =
            GetFloorBounds(
                floors[
                    floorNumber
                ]
            );

        const float margin =
            0.55f;

        float minX =
            bounds.min.x +
            margin;

        float maxX =
            bounds.max.x -
            margin;

        float minZ =
            bounds.min.z +
            margin;

        float maxZ =
            bounds.max.z -
            margin;

        int width =
            Mathf.Max(
                1,
                Mathf.CeilToInt(
                    (maxX - minX) /
                    NavigationGridSize
                )
            );

        int depth =
            Mathf.Max(
                1,
                Mathf.CeilToInt(
                    (maxZ - minZ) /
                    NavigationGridSize
                )
            );

        Vector2Int origin =
            WorldToGridCell(
                desiredPoint,
                minX,
                minZ,
                width,
                depth
            );

        Vector2Int hallwayCell =
            FindNearestWalkableCell(
                floorNumber,
                origin,
                minX,
                minZ,
                width,
                depth
            );

        if (
            hallwayCell.x < 0
        )
        {
            return new Vector3(
                -9999f,
                -9999f,
                -9999f
            );
        }

        return GridCellToWorld(
            hallwayCell,
            minX,
            minZ,
            floorNumber
        );
    }

    bool IsInsideRoomFinish(
        Vector3 worldPoint,
        int floorNumber
    )
    {
        List<MeshFilter> meshes =
            roomFinishMeshes[
                floorNumber
            ];

        if (
            meshes == null ||
            meshes.Count == 0
        )
        {
            return false;
        }

        foreach (
            MeshFilter meshFilter
            in meshes
        )
        {
            if (
                meshFilter == null ||
                meshFilter.sharedMesh == null
            )
            {
                continue;
            }

            Renderer renderer =
                meshFilter.GetComponent
                    <Renderer>();

            if (
                renderer != null &&
                !renderer.bounds.Contains(
                    new Vector3(
                        worldPoint.x,
                        renderer.bounds.center.y,
                        worldPoint.z
                    )
                )
            )
            {
                continue;
            }

            Vector3 localPoint =
                meshFilter.transform
                    .InverseTransformPoint(
                        worldPoint
                    );

            Mesh mesh =
                meshFilter.sharedMesh;

            Vector3[] vertices =
                mesh.vertices;

            int[] triangles =
                mesh.triangles;

            Vector2 p =
                new Vector2(
                    localPoint.x,
                    localPoint.z
                );

            for (
                int triangle = 0;
                triangle + 2 <
                    triangles.Length;
                triangle += 3
            )
            {
                Vector3 va =
                    vertices[
                        triangles[
                            triangle
                        ]
                    ];

                Vector3 vb =
                    vertices[
                        triangles[
                            triangle + 1
                        ]
                    ];

                Vector3 vc =
                    vertices[
                        triangles[
                            triangle + 2
                        ]
                    ];

                if (
                    PointInTriangleXZ(
                        p,
                        new Vector2(
                            va.x,
                            va.z
                        ),
                        new Vector2(
                            vb.x,
                            vb.z
                        ),
                        new Vector2(
                            vc.x,
                            vc.z
                        )
                    )
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    bool PointInTriangleXZ(
        Vector2 p,
        Vector2 a,
        Vector2 b,
        Vector2 c
    )
    {
        float denominator =
            (
                (b.y - c.y) *
                (a.x - c.x)
            ) +
            (
                (c.x - b.x) *
                (a.y - c.y)
            );

        if (
            Mathf.Abs(
                denominator
            ) <
            0.000001f
        )
        {
            return false;
        }

        float alpha =
            (
                (b.y - c.y) *
                (p.x - c.x) +
                (c.x - b.x) *
                (p.y - c.y)
            ) /
            denominator;

        float beta =
            (
                (c.y - a.y) *
                (p.x - c.x) +
                (a.x - c.x) *
                (p.y - c.y)
            ) /
            denominator;

        float gamma =
            1f -
            alpha -
            beta;

        const float epsilon =
            -0.001f;

        return
            alpha >= epsilon &&
            beta >= epsilon &&
            gamma >= epsilon;
    }

    List<Vector3> FindWallAwareFloorPath(
        int floorNumber,
        Vector3 start,
        Vector3 target
    )
    {
        List<Vector3> result =
            new List<Vector3>();

        Bounds bounds =
            GetFloorBounds(
                floors[
                    floorNumber
                ]
            );

        // Add a small margin so cells stay inside the floor footprint.
        const float margin = 0.55f;

        float minX =
            bounds.min.x +
            margin;

        float maxX =
            bounds.max.x -
            margin;

        float minZ =
            bounds.min.z +
            margin;

        float maxZ =
            bounds.max.z -
            margin;

        if (
            maxX <= minX ||
            maxZ <= minZ
        )
        {
            return result;
        }

        int width =
            Mathf.Max(
                1,
                Mathf.CeilToInt(
                    (maxX - minX) /
                    NavigationGridSize
                )
            );

        int depth =
            Mathf.Max(
                1,
                Mathf.CeilToInt(
                    (maxZ - minZ) /
                    NavigationGridSize
                )
            );

        Vector2Int startCell =
            WorldToGridCell(
                start,
                minX,
                minZ,
                width,
                depth
            );

        Vector2Int targetCell =
            WorldToGridCell(
                target,
                minX,
                minZ,
                width,
                depth
            );

        startCell =
            FindNearestWalkableCell(
                floorNumber,
                startCell,
                minX,
                minZ,
                width,
                depth
            );

        targetCell =
            FindNearestWalkableCell(
                floorNumber,
                targetCell,
                minX,
                minZ,
                width,
                depth
            );

        if (
            startCell.x < 0 ||
            targetCell.x < 0
        )
        {
            return result;
        }

        Dictionary<Vector2Int, Vector2Int> cameFrom =
            new Dictionary<Vector2Int, Vector2Int>();

        Dictionary<Vector2Int, float> gScore =
            new Dictionary<Vector2Int, float>();

        List<GridPathNode> open =
            new List<GridPathNode>();

        HashSet<Vector2Int> closed =
            new HashSet<Vector2Int>();

        gScore[startCell] =
            0f;

        open.Add(
            new GridPathNode(
                startCell,
                GridHeuristic(
                    startCell,
                    targetCell
                )
            )
        );

        Vector2Int[] neighbors =
        {
            new Vector2Int( 1,  0),
            new Vector2Int(-1,  0),
            new Vector2Int( 0,  1),
            new Vector2Int( 0, -1),
            new Vector2Int( 1,  1),
            new Vector2Int( 1, -1),
            new Vector2Int(-1,  1),
            new Vector2Int(-1, -1)
        };

        int expanded =
            0;

        bool found =
            false;

        while (
            open.Count > 0 &&
            expanded <
                NavigationMaxExpandedNodes
        )
        {
            int bestIndex =
                0;

            for (
                int i = 1;
                i < open.Count;
                i++
            )
            {
                if (
                    open[i].fScore <
                    open[bestIndex].fScore
                )
                {
                    bestIndex =
                        i;
                }
            }

            Vector2Int current =
                open[
                    bestIndex
                ].cell;

            open.RemoveAt(
                bestIndex
            );

            if (
                closed.Contains(
                    current
                )
            )
            {
                continue;
            }

            if (
                current ==
                targetCell
            )
            {
                found =
                    true;

                break;
            }

            closed.Add(
                current
            );

            expanded++;

            foreach (
                Vector2Int offset
                in neighbors
            )
            {
                Vector2Int next =
                    current +
                    offset;

                if (
                    next.x < 0 ||
                    next.y < 0 ||
                    next.x >= width ||
                    next.y >= depth ||
                    closed.Contains(next)
                )
                {
                    continue;
                }

                Vector3 nextWorld =
                    GridCellToWorld(
                        next,
                        minX,
                        minZ,
                        floorNumber
                    );

                if (
                    !IsWalkableFloorPoint(
                        nextWorld,
                        floorNumber
                    )
                )
                {
                    continue;
                }

                // Prevent diagonal corner-cutting through walls.
                if (
                    offset.x != 0 &&
                    offset.y != 0
                )
                {
                    Vector2Int sideA =
                        current +
                        new Vector2Int(
                            offset.x,
                            0
                        );

                    Vector2Int sideB =
                        current +
                        new Vector2Int(
                            0,
                            offset.y
                        );

                    Vector3 sideAWorld =
                        GridCellToWorld(
                            sideA,
                            minX,
                            minZ,
                            floorNumber
                        );

                    Vector3 sideBWorld =
                        GridCellToWorld(
                            sideB,
                            minX,
                            minZ,
                            floorNumber
                        );

                    if (
                        !IsWalkableFloorPoint(
                            sideAWorld,
                            floorNumber
                        ) ||
                        !IsWalkableFloorPoint(
                            sideBWorld,
                            floorNumber
                        )
                    )
                    {
                        continue;
                    }
                }

                float moveCost =
                    (
                        offset.x != 0 &&
                        offset.y != 0
                    )
                        ? 1.4142f
                        : 1f;

                float tentativeG =
                    gScore[
                        current
                    ] +
                    moveCost;

                float existingG;

                if (
                    !gScore.TryGetValue(
                        next,
                        out existingG
                    ) ||
                    tentativeG <
                    existingG
                )
                {
                    cameFrom[next] =
                        current;

                    gScore[next] =
                        tentativeG;

                    float f =
                        tentativeG +
                        GridHeuristic(
                            next,
                            targetCell
                        );

                    open.Add(
                        new GridPathNode(
                            next,
                            f
                        )
                    );
                }
            }
        }

        if (!found)
        {
            return result;
        }

        List<Vector2Int> cells =
            new List<Vector2Int>();

        Vector2Int trace =
            targetCell;

        cells.Add(
            trace
        );

        int traceSafety =
            0;

        while (
            trace != startCell &&
            traceSafety < 20000
        )
        {
            traceSafety++;

            Vector2Int previous;

            if (
                !cameFrom.TryGetValue(
                    trace,
                    out previous
                )
            )
            {
                result.Clear();
                return result;
            }

            trace =
                previous;

            cells.Add(
                trace
            );
        }

        cells.Reverse();

        // Convert the dense grid path into fewer, clean walking points.
        List<Vector3> denseWorldPath =
            new List<Vector3>();

        foreach (
            Vector2Int cell
            in cells
        )
        {
            denseWorldPath.Add(
                GridCellToWorld(
                    cell,
                    minX,
                    minZ,
                    floorNumber
                )
            );
        }

        if (
            denseWorldPath.Count > 0
        )
        {
            // Start exactly where the marker already is if that segment is clear.
            // Keep the FINAL point snapped to a legal hallway grid cell so the
            // path never forces a last step through a room or wall.
            if (
                HasClearWalkingSegment(
                    start,
                    denseWorldPath[0],
                    floorNumber
                )
            )
            {
                denseWorldPath[0] =
                    start;
            }
        }

        result =
            SimplifyWalkPath(
                denseWorldPath,
                floorNumber
            );

        Debug.Log(
            "Floor " +
            floorNumber +
            " wall-aware path: " +
            cells.Count +
            " grid cells -> " +
            result.Count +
            " movement points."
        );

        return result;
    }

    Vector2Int WorldToGridCell(
        Vector3 point,
        float minX,
        float minZ,
        int width,
        int depth
    )
    {
        int x =
            Mathf.Clamp(
                Mathf.RoundToInt(
                    (point.x - minX) /
                    NavigationGridSize
                ),
                0,
                width - 1
            );

        int z =
            Mathf.Clamp(
                Mathf.RoundToInt(
                    (point.z - minZ) /
                    NavigationGridSize
                ),
                0,
                depth - 1
            );

        return new Vector2Int(
            x,
            z
        );
    }

    Vector3 GridCellToWorld(
        Vector2Int cell,
        float minX,
        float minZ,
        int floorNumber
    )
    {
        return new Vector3(
            minX +
                cell.x *
                NavigationGridSize,
            GetFloorMarkerY(
                floorNumber
            ),
            minZ +
                cell.y *
                NavigationGridSize
        );
    }

    Vector2Int FindNearestWalkableCell(
        int floorNumber,
        Vector2Int origin,
        float minX,
        float minZ,
        int width,
        int depth
    )
    {
        Vector3 originWorld =
            GridCellToWorld(
                origin,
                minX,
                minZ,
                floorNumber
            );

        if (
            IsWalkableFloorPoint(
                originWorld,
                floorNumber
            )
        )
        {
            return origin;
        }

        const int maxRadius =
            8;

        for (
            int radius = 1;
            radius <= maxRadius;
            radius++
        )
        {
            for (
                int x = -radius;
                x <= radius;
                x++
            )
            {
                for (
                    int z = -radius;
                    z <= radius;
                    z++
                )
                {
                    if (
                        Mathf.Abs(x) != radius &&
                        Mathf.Abs(z) != radius
                    )
                    {
                        continue;
                    }

                    Vector2Int candidate =
                        origin +
                        new Vector2Int(
                            x,
                            z
                        );

                    if (
                        candidate.x < 0 ||
                        candidate.y < 0 ||
                        candidate.x >= width ||
                        candidate.y >= depth
                    )
                    {
                        continue;
                    }

                    Vector3 world =
                        GridCellToWorld(
                            candidate,
                            minX,
                            minZ,
                            floorNumber
                        );

                    if (
                        IsWalkableFloorPoint(
                            world,
                            floorNumber
                        )
                    )
                    {
                        return candidate;
                    }
                }
            }
        }

        return new Vector2Int(
            -1,
            -1
        );
    }

    bool IsWalkableFloorPoint(
        Vector3 point,
        int floorNumber
    )
    {
        // Hallway-only rule:
        // any point covered by Nakul's room-category finish mesh is NOT
        // a legal walking cell. The uncovered continuous slab is corridor.
        if (
            IsInsideRoomFinish(
                point,
                floorNumber
            )
        )
        {
            return false;
        }

        // First require an actual generated floor surface underneath.
        // This prevents the pathfinder from "walking" outside the building
        // just because the area outside has no wall collider.
        Vector3 rayStart =
            new Vector3(
                point.x,
                GetFloorMarkerY(
                    floorNumber
                ) +
                0.55f,
                point.z
            );

        RaycastHit[] floorHits =
            Physics.RaycastAll(
                rayStart,
                Vector3.down,
                1.6f,
                ~0,
                QueryTriggerInteraction.Ignore
            );

        bool hasFloorSupport =
            false;

        foreach (
            RaycastHit floorHit
            in floorHits
        )
        {
            if (
                floorHit.collider == null
            )
            {
                continue;
            }

            Transform hitTransform =
                floorHit.collider.transform;

            if (
                !hitTransform.IsChildOf(
                    floors[
                        floorNumber
                    ]
                )
            )
            {
                continue;
            }

            string supportName =
                floorHit.collider
                    .gameObject
                    .name
                    .ToLowerInvariant();

            if (
                supportName.Contains("slab") ||
                supportName.Contains("floor") ||
                supportName.Contains("finish") ||
                supportName.Contains("landing")
            )
            {
                hasFloorSupport =
                    true;

                break;
            }
        }

        if (!hasFloorSupport)
        {
            return false;
        }

        // Then test a person-sized capsule above the floor surface.
        // Floor slabs are below this capsule, while walls intersect it.
        Vector3 bottom =
            new Vector3(
                point.x,
                GetFloorMarkerY(
                    floorNumber
                ) +
                0.12f,
                point.z
            );

        Vector3 top =
            bottom +
            Vector3.up *
            NavigationHeight;

        Collider[] hits =
            Physics.OverlapCapsule(
                bottom,
                top,
                NavigationRadius,
                ~0,
                QueryTriggerInteraction.Ignore
            );

        foreach (
            Collider hit
            in hits
        )
        {
            if (
                hit == null ||
                !hit.enabled ||
                hit.isTrigger
            )
            {
                continue;
            }

            Transform hitTransform =
                hit.transform;

            // Ignore runtime suspect/officer objects.
            if (
                suspect != null &&
                hitTransform.IsChildOf(
                    suspect.transform
                )
            )
            {
                continue;
            }

            if (
                officer != null &&
                hitTransform.IsChildOf(
                    officer.transform
                )
            )
            {
                continue;
            }

            string lowerName =
                hit.gameObject.name
                    .ToLowerInvariant();

            // Slabs / ground surfaces support walking and should
            // not be treated as obstacles.
            if (
                lowerName.Contains("slab") ||
                lowerName.Contains("ground") ||
                lowerName.Contains("landing") ||
                lowerName.Contains("floor finish") ||
                lowerName.Contains("source finishes")
            )
            {
                continue;
            }

            // Any remaining generated collider is treated as a blocker
            // (walls, furniture, columns, etc.).
            return false;
        }

        return true;
    }

    float GridHeuristic(
        Vector2Int a,
        Vector2Int b
    )
    {
        int dx =
            Mathf.Abs(
                a.x -
                b.x
            );

        int dz =
            Mathf.Abs(
                a.y -
                b.y
            );

        int diagonal =
            Mathf.Min(
                dx,
                dz
            );

        int straight =
            Mathf.Max(
                dx,
                dz
            ) -
            diagonal;

        return
            diagonal *
            1.4142f +
            straight;
    }

    List<Vector3> SimplifyWalkPath(
        List<Vector3> densePath,
        int floorNumber
    )
    {
        List<Vector3> simplified =
            new List<Vector3>();

        if (
            densePath == null ||
            densePath.Count == 0
        )
        {
            return simplified;
        }

        simplified.Add(
            densePath[0]
        );

        int anchorIndex =
            0;

        while (
            anchorIndex <
            densePath.Count - 1
        )
        {
            int bestIndex =
                anchorIndex + 1;

            // Look ahead, but keep enough points that the walk
            // still visibly follows hallways and turns.
            int furthest =
                Mathf.Min(
                    densePath.Count - 1,
                    anchorIndex + 8
                );

            for (
                int candidateIndex =
                    anchorIndex + 1;
                candidateIndex <=
                    furthest;
                candidateIndex++
            )
            {
                if (
                    HasClearWalkingSegment(
                        densePath[
                            anchorIndex
                        ],
                        densePath[
                            candidateIndex
                        ],
                        floorNumber
                    )
                )
                {
                    bestIndex =
                        candidateIndex;
                }
                else
                {
                    break;
                }
            }

            simplified.Add(
                densePath[
                    bestIndex
                ]
            );

            anchorIndex =
                bestIndex;
        }

        return simplified;
    }

    bool HasClearWalkingSegment(
        Vector3 start,
        Vector3 end,
        int floorNumber
    )
    {
        float distance =
            Vector3.Distance(
                start,
                end
            );

        int samples =
            Mathf.Max(
                2,
                Mathf.CeilToInt(
                    distance /
                    0.35f
                )
            );

        for (
            int i = 1;
            i < samples;
            i++
        )
        {
            float t =
                i /
                (float)samples;

            Vector3 sample =
                Vector3.Lerp(
                    start,
                    end,
                    t
                );

            sample.y =
                GetFloorMarkerY(
                    floorNumber
                );

            if (
                !IsWalkableFloorPoint(
                    sample,
                    floorNumber
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    Vector3 GetStartingPoint()
    {
        List<Vector3> floor1Treads =
            GetAllTreadCenters(1);

        if (floor1Treads.Count > 0)
        {
            Vector3 lowest =
                floor1Treads
                    .OrderBy(p => p.y)
                    .First();

            return new Vector3(
                lowest.x,
                GetFloorMarkerY(1),
                lowest.z
            );
        }

        Bounds bounds =
            GetFloorBounds(
                floors[1]
            );

        return new Vector3(
            bounds.center.x,
            GetFloorMarkerY(1),
            bounds.center.z
        );
    }

    List<Vector3> GetGeneratedStairTransition(
        int lowerFloor,
        Vector3 fromPosition
    )
    {
        List<Vector3> result =
            new List<Vector3>();

        List<Vector3> allTreads =
            GetAllTreadCenters(
                lowerFloor
            );

        if (allTreads.Count == 0)
            return result;

        float lowerBaseY =
            (lowerFloor - 1) *
            FloorHeight;

        float upperBaseY =
            lowerFloor *
            FloorHeight;

        List<Vector3> candidates =
            allTreads
                .Where(
                    p =>
                        p.y >= lowerBaseY - 0.2f &&
                        p.y <= upperBaseY + 0.6f
                )
                .ToList();

        if (candidates.Count == 0)
            return result;

        float minimumY =
            candidates.Min(p => p.y);

        List<Vector3> startCandidates =
            candidates
                .Where(
                    p =>
                        p.y <= minimumY + 0.55f
                )
                .ToList();

        Vector3 current =
            startCandidates
                .OrderBy(
                    p =>
                        HorizontalDistance(
                            p,
                            fromPosition
                        )
                )
                .First();

        int currentIndex =
            candidates.IndexOf(current);

        HashSet<int> used =
            new HashSet<int>();

        if (currentIndex >= 0)
            used.Add(currentIndex);

        result.Add(
            RaiseAboveTread(current)
        );

        int safety = 0;

        while (
            current.y <
                upperBaseY - 0.25f &&
            safety < 250
        )
        {
            safety++;

            int bestIndex = -1;
            float bestScore =
                float.MaxValue;

            for (int i = 0;
                 i < candidates.Count;
                 i++)
            {
                if (used.Contains(i))
                    continue;

                Vector3 candidate =
                    candidates[i];

                float dy =
                    candidate.y -
                    current.y;

                if (dy <= 0.01f)
                    continue;

                float horizontal =
                    HorizontalDistance(
                        candidate,
                        current
                    );

                if (horizontal > 4.5f)
                    continue;

                float score =
                    dy * 8f +
                    horizontal;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break;

            current =
                candidates[bestIndex];

            used.Add(bestIndex);

            result.Add(
                RaiseAboveTread(
                    current
                )
            );
        }

        // If it clearly did not reach the next floor,
        // let the fallback transition handle it.
        if (
            result.Count < 3 ||
            current.y <
                upperBaseY - 0.8f
        )
        {
            result.Clear();
        }

        return result;
    }

    List<Vector3> GetAllTreadCenters(
        int lowerFloor
    )
    {
        List<Vector3> points =
            new List<Vector3>();

        if (stairConnections == null)
            return points;

        Transform group =
            stairConnections.Find(
                "From Floor " +
                lowerFloor
            );

        if (group == null)
            return points;

        Renderer[] renderers =
            group.GetComponentsInChildren
                <Renderer>(true);

        foreach (Renderer renderer in renderers)
        {
            if (
                renderer.gameObject.name
                    .StartsWith("Tread")
            )
            {
                points.Add(
                    renderer.bounds.center
                );
            }
        }

        return points;
    }

    Vector3 RaiseAboveTread(
        Vector3 treadCenter
    )
    {
        return new Vector3(
            treadCenter.x,
            treadCenter.y + 0.75f,
            treadCenter.z
        );
    }

    void AddFloorPatrol(
        int floorNumber,
        Vector3 start
    )
    {
        Bounds bounds =
            GetFloorBounds(
                floors[floorNumber]
            );

        Vector3 center =
            new Vector3(
                bounds.center.x,
                GetFloorMarkerY(
                    floorNumber
                ),
                bounds.center.z
            );

        Vector3 forward =
            center - start;

        forward.y = 0f;

        if (
            forward.sqrMagnitude <
            0.01f
        )
        {
            forward =
                Vector3.right;
        }

        forward.Normalize();

        Vector3 side =
            new Vector3(
                -forward.z,
                0f,
                forward.x
            );

        // Keep the patrol small so it looks like the subject
        // spends time on the floor without wandering too far
        // from the stair landing.
        Vector3 p1 =
            start +
            forward * 2.2f;

        Vector3 p2 =
            p1 +
            side * 1.4f;

        Vector3 p3 =
            start +
            side * 1.2f;

        AddPathPoint(p1);
        AddPathPoint(p2);
        AddPathPoint(p3);
    }

    Vector3 GetSafeFloorPoint(
        int floorNumber,
        Vector3 arrival
    )
    {
        Bounds bounds =
            GetFloorBounds(
                floors[floorNumber]
            );

        Vector3 center =
            new Vector3(
                bounds.center.x,
                GetFloorMarkerY(
                    floorNumber
                ),
                bounds.center.z
            );

        Vector3 direction =
            center - arrival;

        direction.y = 0f;

        if (direction.sqrMagnitude < 0.01f)
            return arrival;

        direction.Normalize();

        return arrival +
            direction * 2.0f;
    }

    void AddPathPoint(
        Vector3 point
    )
    {
        if (
            trackingPath.Count == 0 ||
            Vector3.Distance(
                trackingPath[
                    trackingPath.Count - 1
                ],
                point
            ) > 0.03f
        )
        {
            trackingPath.Add(point);
        }
    }

    float HorizontalDistance(
        Vector3 a,
        Vector3 b
    )
    {
        return Vector2.Distance(
            new Vector2(a.x, a.z),
            new Vector2(b.x, b.z)
        );
    }

    // =========================================================
    // SIMULATION
    // SUSPECT FIRST, THEN LAW ENFORCEMENT
    // =========================================================

    IEnumerator RunSimulation()
    {
        phase =
            SimulationPhase.SuspectMoving;

        Debug.Log(
            "PHASE 1: SUSPECT MOVING"
        );

        if (roomTargets.Count > 0)
        {
            SetSuspectHeadingToRoom(
                0
            );
        }

        for (
            int i = 1;
            i < trackingPath.Count;
            i++
        )
        {
            suspectPreviousPosition =
                suspectCurrentPosition;

            Vector3 destination =
                CalculateMatrixTranslation(
                    suspectCurrentPosition,
                    trackingPath[i]
                );

            yield return StartCoroutine(
                SmoothMoveSuspect(
                    suspectCurrentPosition,
                    destination
                )
            );

            suspectCurrentPosition =
                destination;

            suspectHistory.Add(
                suspectCurrentPosition
            );

            UpdateSuspectTrail();

            int roomIndex =
                FindRoomTargetIndexAtPosition(
                    suspectCurrentPosition
                );

            if (roomIndex >= 0)
            {
                RoomDestination arrivedRoom =
                    roomTargets[
                        roomIndex
                    ];

                suspectRoomStatus =
                    "ARRIVED at hallway outside Room " +
                    arrivedRoom.roomNumber +
                    " on Floor " +
                    arrivedRoom.floorNumber;

                suspectDisplayLabel =
                    "SUSPECT | AT ROOM " +
                    arrivedRoom.roomNumber;

                Debug.Log(
                    "SUSPECT ARRIVED AT ROOM " +
                    arrivedRoom.roomNumber +
                    " - FLOOR " +
                    arrivedRoom.floorNumber
                );

                // Pause so the professor can clearly see the arrival.
                yield return new WaitForSeconds(
                    RoomArrivalPause
                );

                int nextRoomIndex =
                    roomIndex + 1;

                if (
                    nextRoomIndex <
                    roomTargets.Count
                )
                {
                    SetSuspectHeadingToRoom(
                        nextRoomIndex
                    );
                }
            }

            yield return new WaitForSeconds(
                pauseBetweenMoves
            );
        }

        Debug.Log(
            "SUSPECT STOPPED AT LAST-KNOWN POSITION: " +
            suspectCurrentPosition
        );

        // Law enforcement does NOT start until the suspect is done.
        phase =
            SimulationPhase.OfficerResponding;

        Debug.Log(
            "PHASE 2: LAW ENFORCEMENT RESPONDING"
        );

        officerCurrentPosition =
            trackingPath[0];

        officerPreviousPosition =
            officerCurrentPosition;

        officer.transform.position =
            officerCurrentPosition;

        UpdateObjectParent(
            officer,
            officerCurrentPosition
        );

        officer.SetActive(true);
        UpdateLocatorVisuals();

        officerHistory.Clear();

        officerHistory.Add(
            officerCurrentPosition
        );

        UpdateOfficerTrail();

        if (roomTargets.Count > 0)
        {
            SetOfficerHeadingToRoom(
                0
            );
        }

        for (
            int i = 1;
            i < trackingPath.Count;
            i++
        )
        {
            if (suspectDetectedByOfficer)
                break;

            officerPreviousPosition =
                officerCurrentPosition;

            Vector3 destination =
                trackingPath[i];

            yield return StartCoroutine(
                SmoothMoveOfficer(
                    officerCurrentPosition,
                    destination
                )
            );

            officerCurrentPosition =
                destination;

            officerHistory.Add(
                officerCurrentPosition
            );

            UpdateOfficerTrail();

            int roomIndex =
                FindRoomTargetIndexAtPosition(
                    officerCurrentPosition
                );

            if (roomIndex >= 0)
            {
                RoomDestination arrivedRoom =
                    roomTargets[
                        roomIndex
                    ];

                officerRoomStatus =
                    "ARRIVED at hallway outside Room " +
                    arrivedRoom.roomNumber +
                    " on Floor " +
                    arrivedRoom.floorNumber;

                officerDisplayLabel =
                    "LAW ENFORCEMENT | AT ROOM " +
                    arrivedRoom.roomNumber;

                yield return new WaitForSeconds(
                    RoomArrivalPause
                );

                int nextRoomIndex =
                    roomIndex + 1;

                if (
                    nextRoomIndex <
                    roomTargets.Count
                )
                {
                    SetOfficerHeadingToRoom(
                        nextRoomIndex
                    );
                }
            }

            CheckOfficerDetection();

            yield return new WaitForSeconds(
                pauseBetweenMoves
            );
        }

        phase =
            SimulationPhase.Complete;

        Debug.Log(
            "SIMULATION COMPLETE"
        );
    }

    // =========================================================
    // MATRIX
    // =========================================================

    Vector3 CalculateMatrixTranslation(
        Vector3 start,
        Vector3 destination
    )
    {
        float x = start.x;
        float y = start.y;
        float z = start.z;

        float dx =
            destination.x -
            start.x;

        float dy =
            destination.y -
            start.y;

        float dz =
            destination.z -
            start.z;

        /*
         * 3D homogeneous translation:
         *
         * | 1 0 0 dx |
         * | 0 1 0 dy |
         * | 0 0 1 dz |
         * | 0 0 0  1 |
         */

        float newX =
            x + dx;

        float newY =
            y + dy;

        float newZ =
            z + dz;

        return new Vector3(
            newX,
            newY,
            newZ
        );
    }

    // =========================================================
    // MOVEMENT
    // =========================================================

    IEnumerator SmoothMoveSuspect(
        Vector3 start,
        Vector3 end
    )
    {
        float elapsed = 0f;

        float segmentDuration =
            Mathf.Clamp(
                Vector3.Distance(
                    start,
                    end
                ) /
                2.2f,
                0.28f,
                1.35f
            );

        while (elapsed < segmentDuration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(
                    elapsed /
                    segmentDuration
                );

            Vector3 position =
                Vector3.Lerp(
                    start,
                    end,
                    t
                );

            suspect.transform.position =
                position;

            suspectCurrentPosition =
                position;

            UpdateObjectParent(
                suspect,
                position
            );

            UpdateLocatorVisuals();

            yield return null;
        }

        suspect.transform.position =
            end;

        suspectCurrentPosition =
            end;

        UpdateObjectParent(
            suspect,
            end
        );

        UpdateLocatorVisuals();
    }

    IEnumerator SmoothMoveOfficer(
        Vector3 start,
        Vector3 end
    )
    {
        float elapsed = 0f;

        float segmentDuration =
            Mathf.Clamp(
                Vector3.Distance(
                    start,
                    end
                ) /
                2.2f,
                0.28f,
                1.35f
            );

        while (elapsed < segmentDuration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(
                    elapsed /
                    segmentDuration
                );

            Vector3 position =
                Vector3.Lerp(
                    start,
                    end,
                    t
                );

            officer.transform.position =
                position;

            officerCurrentPosition =
                position;

            UpdateObjectParent(
                officer,
                position
            );

            UpdateLocatorVisuals();
            CheckOfficerDetection();

            if (suspectDetectedByOfficer)
                yield break;

            yield return null;
        }

        officer.transform.position =
            end;

        officerCurrentPosition =
            end;

        UpdateObjectParent(
            officer,
            end
        );

        UpdateLocatorVisuals();
    }

    // =========================================================
    // FLOOR / STAIR PARENTING
    // =========================================================

    void UpdateObjectParent(
        GameObject obj,
        Vector3 position
    )
    {
        if (IsOnFloorLevel(position))
        {
            int floorNumber =
                GetNearestFloorNumber(
                    position
                );

            Transform floorParent =
                floors[floorNumber];

            if (
                obj.transform.parent !=
                floorParent
            )
            {
                obj.transform.SetParent(
                    floorParent,
                    true
                );
            }

            return;
        }

        int lowerFloor =
            GetLowerFloorForPosition(
                position
            );

        if (stairConnections != null)
        {
            Transform stairParent =
                stairConnections.Find(
                    "From Floor " +
                    lowerFloor
                );

            if (stairParent != null)
            {
                if (
                    obj.transform.parent !=
                    stairParent
                )
                {
                    obj.transform.SetParent(
                        stairParent,
                        true
                    );
                }

                return;
            }
        }

        obj.transform.SetParent(
            buildingRoot,
            true
        );
    }

    bool IsOnFloorLevel(
        Vector3 position
    )
    {
        int nearest =
            GetNearestFloorNumber(
                position
            );

        return Mathf.Abs(
            position.y -
            GetFloorMarkerY(
                nearest
            )
        ) < 0.45f;
    }

    int GetNearestFloorNumber(
        Vector3 position
    )
    {
        float level =
            (position.y -
             FloorMarkerOffset) /
            FloorHeight;

        int floorNumber =
            Mathf.RoundToInt(level) + 1;

        return Mathf.Clamp(
            floorNumber,
            1,
            TotalFloors
        );
    }

    int GetLowerFloorForPosition(
        Vector3 position
    )
    {
        float level =
            (position.y -
             FloorMarkerOffset) /
            FloorHeight;

        int floorNumber =
            Mathf.FloorToInt(level) + 1;

        return Mathf.Clamp(
            floorNumber,
            1,
            TotalFloors - 1
        );
    }

    float GetFloorMarkerY(
        int floorNumber
    )
    {
        return
            (floorNumber - 1) *
            FloorHeight +
            FloorMarkerOffset;
    }

    string GetLocationLabel(
        Vector3 position
    )
    {
        if (IsOnFloorLevel(position))
        {
            return
                "Floor " +
                GetNearestFloorNumber(
                    position
                );
        }

        int lowerFloor =
            GetLowerFloorForPosition(
                position
            );

        return
            "Stair " +
            lowerFloor +
            " -> " +
            (lowerFloor + 1);
    }

    // =========================================================
    // MARKERS
    // =========================================================

    void CreateSuspect()
    {
        suspect =
            CreateRuntimePrimitive(
                PrimitiveType.Sphere
            );

        suspect.name =
            "Suspect";

        suspect.transform.localScale =
            Vector3.one *
            MarkerSize;

        suspectCurrentPosition =
            trackingPath[0];

        suspectPreviousPosition =
            suspectCurrentPosition;

        suspect.transform.position =
            suspectCurrentPosition;

        Renderer renderer =
            suspect.GetComponent<Renderer>();

        Material markerMaterial = renderer.material;
        ownedMaterials.Add(markerMaterial);markerMaterial.color = Color.red;

        UpdateObjectParent(
            suspect,
            suspectCurrentPosition
        );

        suspectHistory.Add(
            suspectCurrentPosition
        );
    }

    void CreateOfficer()
    {
        officer =
            CreateRuntimePrimitive(
                PrimitiveType.Sphere
            );

        officer.name =
            "Law Enforcement";

        officer.transform.localScale =
            Vector3.one *
            MarkerSize;

        officerCurrentPosition =
            trackingPath[0];

        officerPreviousPosition =
            officerCurrentPosition;

        officer.transform.position =
            officerCurrentPosition;

        Renderer renderer =
            officer.GetComponent<Renderer>();

        Material markerMaterial = renderer.material;
        ownedMaterials.Add(markerMaterial);markerMaterial.color = Color.blue;

        UpdateObjectParent(
            officer,
            officerCurrentPosition
        );

        officer.SetActive(false);
    }

    // =========================================================
    // VISIBLE LOCATOR POLES + LABELS
    // =========================================================

    void CreateLocators()
    {
        suspectLocator =
            CreateLocatorLine(
                "Suspect Locator",
                Color.red
            );

        officerLocator =
            CreateLocatorLine(
                "Officer Locator",
                Color.blue
            );

        // Do NOT use 3D TextMesh for the names.
        // The labels are drawn in OnGUI using the locator top
        // projected to screen space, which stays much sharper.

        UpdateLocatorVisuals();
    }

    LineRenderer CreateLocatorLine(
        string objectName,
        Color color
    )
    {
        GameObject lineObject =
            CreateRuntimeObject(
                objectName
            );

        LineRenderer line =
            lineObject.AddComponent<LineRenderer>();

        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = 0.22f;
        line.endWidth = 0.22f;

        Shader shader =
            Shader.Find(
                "Universal Render Pipeline/Unlit"
            );

        if (shader == null)
        {
            shader =
                Shader.Find(
                    "Sprites/Default"
                );
        }

        if (shader != null)
        {
            line.sharedMaterial = CreateRuntimeMaterial(shader, color);
        }

        line.startColor = color;
        line.endColor = color;

        return line;
    }

    TextMesh CreateTrackingLabel(
        string text,
        Color color
    )
    {
        GameObject labelObject =
            CreateRuntimeObject(
                text + " Label"
            );

        TextMesh label =
            labelObject.AddComponent<TextMesh>();

        label.text = text;
        label.color = color;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 72;
        label.characterSize = 0.10f;

        return label;
    }

    void UpdateLocatorVisuals()
    {
        UpdateOneLocator(
            suspect,
            suspectLocator,
            true
        );

        UpdateOneLocator(
            officer,
            officerLocator,
            officer != null &&
            officer.activeSelf
        );
    }

    void UpdateOneLocator(
        GameObject target,
        LineRenderer locator,
        bool visible
    )
    {
        if (locator == null)
            return;

        locator.enabled =
            visible;

        if (
            !visible ||
            target == null
        )
        {
            return;
        }

        Vector3 basePosition =
            target.transform.position;

        Vector3 topPosition =
            basePosition +
            Vector3.up * 5.0f;

        locator.SetPosition(
            0,
            basePosition
        );

        locator.SetPosition(
            1,
            topPosition
        );
    }

    void DrawTrackingScreenLabel(
        GameObject target,
        string title,
        Color color
    )
    {
        if (
            trackingCamera == null ||
            target == null ||
            !target.activeSelf
        )
        {
            return;
        }

        Vector3 locatorTop =
            target.transform.position +
            Vector3.up * 5.0f;

        Vector3 screenPoint =
            trackingCamera.WorldToScreenPoint(
                locatorTop
            );

        if (screenPoint.z <= 0f)
            return;

        float x =
            screenPoint.x;

        float y =
            Screen.height -
            screenPoint.y;

        string text;

        if (
            target ==
            suspect
        )
        {
            text =
                suspectDisplayLabel;
        }
        else if (
            target ==
            officer
        )
        {
            text =
                officerDisplayLabel;
        }
        else
        {
            text =
                title +
                " | " +
                GetLocationLabel(
                    target.transform.position
                );
        }

        Color oldColor =
            GUI.color;

        GUI.color =
            color;

        GUI.Box(
            new Rect(
                x - 125f,
                y - 24f,
                250f,
                30f
            ),
            text
        );

        GUI.color =
            oldColor;
    }

    // =========================================================
    // DETECTION
    // =========================================================

    void CheckOfficerDetection()
    {
        if (
            officer == null ||
            suspect == null ||
            !officer.activeSelf
        )
        {
            return;
        }

        float distance =
            Vector3.Distance(
                officer.transform.position,
                suspect.transform.position
            );

        if (distance <= detectionDistance)
        {
            suspectDetectedByOfficer =
                true;

            Debug.Log(
                "SUSPECT DETECTED BY LAW ENFORCEMENT"
            );
        }
    }

    // =========================================================
    // TRAILS
    // =========================================================

    void CreateTrails()
    {
        suspectTrail =
            CreateTrail(
                "Suspect Movement Path",
                Color.red
            );

        officerTrail =
            CreateTrail(
                "Officer Movement Path",
                Color.blue
            );

        UpdateSuspectTrail();
        UpdateOfficerTrail();
    }

    LineRenderer CreateTrail(
        string objectName,
        Color color
    )
    {
        GameObject trailObject =
            CreateRuntimeObject(
                objectName
            );

        LineRenderer line =
            trailObject.AddComponent
                <LineRenderer>();

        line.useWorldSpace = true;
        line.startWidth = 0.18f;
        line.endWidth = 0.18f;

        Shader shader =
            Shader.Find(
                "Universal Render Pipeline/Unlit"
            );

        if (shader == null)
        {
            shader =
                Shader.Find(
                    "Sprites/Default"
                );
        }

        if (shader != null)
        {
            line.sharedMaterial = CreateRuntimeMaterial(shader, color);
        }

        line.startColor = color;
        line.endColor = color;

        return line;
    }

    void UpdateSuspectTrail()
    {
        if (suspectTrail == null)
            return;

        suspectTrail.positionCount =
            suspectHistory.Count;

        for (int i = 0;
             i < suspectHistory.Count;
             i++)
        {
            suspectTrail.SetPosition(
                i,
                suspectHistory[i]
            );
        }
    }

    void UpdateOfficerTrail()
    {
        if (officerTrail == null)
            return;

        officerTrail.positionCount =
            officerHistory.Count;

        for (int i = 0;
             i < officerHistory.Count;
             i++)
        {
            officerTrail.SetPosition(
                i,
                officerHistory[i]
            );
        }
    }

    // =========================================================
    // FREE-FLIGHT CAMERA
    // =========================================================

    void Update()
    {
        EnsureSimulationStarted();
        if (!sessionReady) return;
        // Input is read once per rendered frame, outside OnGUI. The movement
        // coroutine continues independently while this camera is controlled.
        if (trackingCamera == null || !Application.isFocused)
        {
            EndFreeFly();
            return;
        }

        FlightInput input = ReadFlightInput();
        if (input.escape || !input.held)
        {
            EndFreeFly();
            return;
        }

        if (!freeFlyActive)
        {
            // Require a fresh click after Escape or focus loss. Do not use
            // mouse delta from before capture, which would cause a view jump.
            if (input.pressed && input.insideView) BeginFreeFly();
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            EndFreeFly();
            return;
        }

        float sensitivity = Mathf.Max(0.01f, flyMouseSensitivity);
        flyYaw = Mathf.Repeat(flyYaw + input.look.x * sensitivity, 360f);
        flyPitch = Mathf.Clamp(flyPitch - input.look.y * sensitivity, -89f, 89f);
        Transform cameraTransform = trackingCamera.transform;
        cameraTransform.rotation = Quaternion.Euler(flyPitch, flyYaw, 0f);

        Vector3 direction = cameraTransform.right * input.move.x +
            Vector3.up * input.move.y + cameraTransform.forward * input.move.z;
        direction = Vector3.ClampMagnitude(direction, 1f);
        float speed = Mathf.Max(0.1f, flySpeed) *
            (input.fast ? Mathf.Max(1f, flyFastMultiplier) : 1f);
        float deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        cameraTransform.position += direction * speed * deltaTime;

        // Existing zoom/orbit buttons resume from this pose instead of the
        // old overview. Reset View and floor buttons still deliberately frame
        // the building or the selected floor.
        cameraYaw = flyYaw;
        cameraPitch = flyPitch;
        cameraTarget = cameraTransform.position + cameraTransform.forward * cameraDistance;
        UpdateLocatorVisuals();
    }

    FlightInput ReadFlightInput()
    {
        FlightInput input = new FlightInput();
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        Keyboard keyboard = Keyboard.current;
        if (mouse != null)
        {
            input.held = mouse.rightButton.isPressed;
            input.pressed = mouse.rightButton.wasPressedThisFrame;
            input.look = mouse.delta.ReadValue();
            Vector2 pointer = mouse.position.ReadValue();
            input.insideView = pointer.x >= 0 && pointer.y >= 0 &&
                pointer.x < Screen.width && pointer.y < Screen.height;
        }
        if (keyboard != null)
        {
            input.escape = keyboard.escapeKey.wasPressedThisFrame;
            input.fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            input.move = new Vector3(
                (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                (keyboard.eKey.isPressed ? 1f : 0f) - (keyboard.qKey.isPressed ? 1f : 0f),
                (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        input.held = Input.GetMouseButton(1);
        input.pressed = Input.GetMouseButtonDown(1);
        input.escape = Input.GetKeyDown(KeyCode.Escape);
        input.fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        // Unity's default legacy mouse axes scale pixel motion by 0.1.
        // Convert to pixel-like units for the same sensitivity setting.
        input.look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
        Vector3 pointer = Input.mousePosition;
        input.insideView = pointer.x >= 0 && pointer.y >= 0 &&
            pointer.x < Screen.width && pointer.y < Screen.height;
        input.move = new Vector3(
            (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f),
            (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f),
            (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f));
#endif
        return input;
    }

    void BeginFreeFly()
    {
        if (trackingCamera == null) return;
        Vector3 angles = trackingCamera.transform.eulerAngles;
        flyYaw = angles.y;
        flyPitch = angles.x > 180f ? angles.x - 360f : angles.x;
        flyPitch = Mathf.Clamp(flyPitch, -89f, 89f);
        cursorBeforeFlight = Cursor.lockState;
        cursorVisibleBeforeFlight = Cursor.visible;
        freeFlyActive = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void EndFreeFly()
    {
        if (!freeFlyActive) return;
        freeFlyActive = false;
        // Always leave a usable cursor if another component had it locked
        // before this camera took control.
        Cursor.lockState = cursorBeforeFlight == CursorLockMode.Locked ?
            CursorLockMode.None : cursorBeforeFlight;
        Cursor.visible = cursorBeforeFlight == CursorLockMode.Locked || cursorVisibleBeforeFlight;
    }

    void OnApplicationFocus(bool focused) { if (!focused) EndFreeFly(); }
    void OnApplicationPause(bool paused) { if (paused) EndFreeFly(); }

    // =========================================================
    // MANUAL 3D CAMERA
    // =========================================================

    void SetupManual3DCamera()
    {
        Camera[] cameras =
            FindLoadedSceneComponents<Camera>(false);

        foreach (Camera cam in cameras)
        {
            originalCameras.Add(new CameraState { camera = cam, enabled = cam.enabled });
            cam.enabled = false;
        }

        GameObject cameraObject =
            CreateRuntimeObject(
                "SHIELD Manual 3D Camera"
            );

        trackingCamera =
            cameraObject.AddComponent
                <Camera>();

        cameraObject.tag =
            "MainCamera";

        trackingCamera.enabled = true;
        trackingCamera.orthographic = false;
        trackingCamera.fieldOfView = 55f;
        trackingCamera.nearClipPlane = 0.1f;
        trackingCamera.farClipPlane = 2000f;
        trackingCamera.depth = 100f;
    }

    void ShowAllFloors()
    {
        selectedView = 0;

        for (int i = 1;
             i <= TotalFloors;
             i++)
        {
            floors[i].gameObject.SetActive(
                true
            );
        }

        if (stairConnections != null)
        {
            stairConnections.gameObject.SetActive(
                true
            );

            for (int i = 1;
                 i <= TotalFloors;
                 i++)
            {
                Transform group =
                    stairConnections.Find(
                        "From Floor " + i
                    );

                if (group != null)
                {
                    group.gameObject.SetActive(
                        true
                    );
                }
            }
        }

        if (suspectTrail != null)
            suspectTrail.enabled = true;

        if (officerTrail != null)
            officerTrail.enabled = true;

        FrameWholeBuilding();
    }

    void ShowOnlyFloor(
        int floorNumber
    )
    {
        floorNumber =
            Mathf.Clamp(
                floorNumber,
                1,
                TotalFloors
            );

        selectedView =
            floorNumber;

        for (int i = 1;
             i <= TotalFloors;
             i++)
        {
            floors[i].gameObject.SetActive(
                i == floorNumber
            );
        }

        if (stairConnections != null)
        {
            stairConnections.gameObject.SetActive(
                true
            );

            for (int i = 1;
                 i <= TotalFloors;
                 i++)
            {
                Transform group =
                    stairConnections.Find(
                        "From Floor " + i
                    );

                if (group != null)
                {
                    // Matches Nakul's floor-only inspection:
                    // selected floor's departure stairs and
                    // arrival stairs from the floor below.
                    bool visible =
                        i == floorNumber ||
                        i == floorNumber - 1;

                    group.gameObject.SetActive(
                        visible
                    );
                }
            }
        }

        // Hide full cross-floor trails in single-floor mode
        // so the selected floor stays visually clean.
        if (suspectTrail != null)
            suspectTrail.enabled = false;

        if (officerTrail != null)
            officerTrail.enabled = false;

        FrameFloor(
            floorNumber
        );
    }

    void FrameWholeBuilding()
    {
        Bounds bounds =
            GetWholeBuildingBounds();

        cameraTarget =
            bounds.center;

        float size =
            Mathf.Max(
                bounds.size.x,
                bounds.size.z
            );

        cameraDistance =
            Mathf.Max(
                45f,
                size * 0.85f
            );

        cameraYaw = 225f;
        cameraPitch = 24f;

        ApplyManualCamera();
    }

    void FrameFloor(
        int floorNumber
    )
    {
        Bounds bounds =
            GetFloorBounds(
                floors[floorNumber]
            );

        cameraTarget =
            bounds.center +
            Vector3.up * 0.8f;

        float size =
            Mathf.Max(
                bounds.size.x,
                bounds.size.z
            );

        cameraDistance =
            Mathf.Max(
                25f,
                size * 0.65f
            );

        cameraYaw = 225f;
        cameraPitch = 22f;

        ApplyManualCamera();
    }

    void ApplyManualCamera()
    {
        if (trackingCamera == null)
            return;

        Quaternion rotation =
            Quaternion.Euler(
                cameraPitch,
                cameraYaw,
                0f
            );

        Vector3 direction =
            rotation *
            Vector3.back;

        trackingCamera.transform.position =
            cameraTarget +
            direction *
            cameraDistance;

        trackingCamera.transform.LookAt(
            cameraTarget
        );

        UpdateLocatorVisuals();
    }

    void RotateCamera(
        float yawDelta,
        float pitchDelta
    )
    {
        cameraYaw += yawDelta;

        cameraPitch =
            Mathf.Clamp(
                cameraPitch + pitchDelta,
                -89f,
                89f
            );

        ApplyManualCamera();
    }

    void ZoomCamera(
        float amount
    )
    {
        cameraDistance =
            Mathf.Clamp(
                cameraDistance + amount,
                10f,
                180f
            );

        ApplyManualCamera();
    }

    Bounds GetWholeBuildingBounds()
    {
        bool found = false;
        Bounds result = new Bounds();

        for (int floorNumber = 1;
             floorNumber <= TotalFloors;
             floorNumber++)
        {
            Renderer[] renderers =
                floors[floorNumber]
                    .GetComponentsInChildren
                        <Renderer>(true);

            foreach (Renderer renderer in renderers)
            {
                if (
                    renderer.GetComponent<TextMesh>() != null ||
                    renderer.gameObject.name.Contains(
                        "Reference"
                    )
                )
                {
                    continue;
                }

                if (!found)
                {
                    result =
                        renderer.bounds;

                    found = true;
                }
                else
                {
                    result.Encapsulate(
                        renderer.bounds
                    );
                }
            }
        }

        if (!found)
        {
            result =
                new Bounds(
                    buildingRoot.position,
                    new Vector3(
                        100f,
                        22f,
                        40f
                    )
                );
        }

        return result;
    }

    Bounds GetFloorBounds(
        Transform floor
    )
    {
        Renderer[] renderers =
            floor.GetComponentsInChildren
                <Renderer>(true);

        bool found = false;
        Bounds result = new Bounds();

        foreach (Renderer renderer in renderers)
        {
            if (
                renderer.GetComponent<TextMesh>() != null ||
                renderer.gameObject.name.Contains(
                    "Reference"
                )
            )
            {
                continue;
            }

            if (!found)
            {
                result =
                    renderer.bounds;

                found = true;
            }
            else
            {
                result.Encapsulate(
                    renderer.bounds
                );
            }
        }

        if (!found)
        {
            return new Bounds(
                floor.position,
                Vector3.one * 10f
            );
        }

        return result;
    }

    // =========================================================
    // FUTURE SDT INPUT HOOK
    // =========================================================

    public void DisplaySuspectPositionFromSDT(
        float x,
        float y,
        float z
    )
    {
        suspectPreviousPosition =
            suspectCurrentPosition;

        suspectCurrentPosition =
            new Vector3(
                x,
                y,
                z
            );

        suspect.transform.position =
            suspectCurrentPosition;

        UpdateObjectParent(
            suspect,
            suspectCurrentPosition
        );

        suspectHistory.Add(
            suspectCurrentPosition
        );

        UpdateSuspectTrail();
    }

    // =========================================================
    // GUI
    // =========================================================

    void OnGUI()
    {
        if (!Application.isPlaying) return;
        if (!sessionReady)
        {
            GUI.Box(new Rect(15, 15, 455, 155), "SHIELD v1.3.1 - Simulation startup");
            GUIStyle messageStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            GUI.Label(new Rect(30, 48, 410, 72), string.IsNullOrEmpty(startupError) ?
                "Starting a fresh simulation..." : startupError, messageStyle);
            if (GUI.Button(new Rect(30, 128, 175, 28), "Retry simulation")) restartRequested = true;
            return;
        }
        GUI.Box(
            new Rect(
                15,
                15,
                530,
                500
            ),
            "SHIELD v1.3.1 - Building 9 Tracking"
        );

        GUI.Label(
            new Rect(30, 45, 410, 22),
            "Phase: " + phase
        );

        GUI.Label(
            new Rect(30, 68, 410, 22),
            "Suspect: " +
            GetLocationLabel(
                suspectCurrentPosition
            ) +
            "  " +
            suspectCurrentPosition
        );

        GUI.Label(
            new Rect(30, 91, 410, 22),
            "Law Enforcement: " +
            (
                officer != null &&
                officer.activeSelf
                    ? GetLocationLabel(
                        officerCurrentPosition
                    ) +
                    "  " +
                    officerCurrentPosition
                    : "Not deployed yet"
            )
        );

        GUI.Label(
            new Rect(30, 114, 410, 22),
            "Selected View: " +
            (
                selectedView == 0
                    ? "All Floors"
                    : "Floor " +
                      selectedView
            )
        );

        GUI.Label(
            new Rect(30, 137, 455, 22),
            "Detected: " +
            suspectDetectedByOfficer
        );

        GUI.Label(
            new Rect(30, 160, 485, 22),
            roomRouteText
        );

        GUI.Label(
            new Rect(30, 183, 485, 22),
            "Suspect Room Status: " +
            suspectRoomStatus
        );

        GUI.Label(
            new Rect(30, 206, 485, 22),
            "Officer Room Status: " +
            (
                officer != null &&
                officer.activeSelf
                    ? officerRoomStatus
                    : "Not deployed yet"
            )
        );

        if (
            GUI.Button(
                new Rect(
                    30,
                    241,
                    85,
                    30
                ),
                "All Floors"
            )
        )
        {
            ShowAllFloors();
        }

        for (
            int floorNumber = 1;
            floorNumber <= TotalFloors;
            floorNumber++
        )
        {
            int buttonFloor =
                floorNumber;

            if (
                GUI.Button(
                    new Rect(
                        120 +
                        ((floorNumber - 1) * 60),
                        241,
                        55,
                        30
                    ),
                    "F" + floorNumber
                )
            )
            {
                ShowOnlyFloor(
                    buttonFloor
                );
            }
        }

        GUI.Label(
            new Rect(30, 281, 455, 22),
            "3D Camera - right mouse for free flight"
        );

        if (
            GUI.Button(
                new Rect(30, 309, 75, 30),
                "Left"
            )
        )
        {
            RotateCamera(-12f, 0f);
        }

        if (
            GUI.Button(
                new Rect(110, 309, 75, 30),
                "Right"
            )
        )
        {
            RotateCamera(12f, 0f);
        }

        if (
            GUI.Button(
                new Rect(190, 309, 75, 30),
                "Up"
            )
        )
        {
            RotateCamera(0f, 7f);
        }

        if (
            GUI.Button(
                new Rect(270, 309, 75, 30),
                "Down"
            )
        )
        {
            RotateCamera(0f, -7f);
        }

        if (
            GUI.Button(
                new Rect(30, 344, 100, 30),
                "Zoom In"
            )
        )
        {
            ZoomCamera(-8f);
        }

        if (
            GUI.Button(
                new Rect(140, 344, 100, 30),
                "Zoom Out"
            )
        )
        {
            ZoomCamera(8f);
        }

        if (
            GUI.Button(
                new Rect(250, 344, 100, 30),
                "Reset View"
            )
        )
        {
            if (selectedView == 0)
                FrameWholeBuilding();
            else
                FrameFloor(selectedView);
        }

        GUI.Label(
            new Rect(30, 384, 485, 22),
            "Hold RMB: mouse look + WASD fly | Q down / E up"
        );

        GUI.Label(
            new Rect(30, 407, 485, 22),
            "Shift: faster | Release RMB / Esc: cursor | Reset: overview"
        );

        if (GUI.Button(new Rect(30, 445, 175, 28), "Restart simulation")) restartRequested = true;

        // Sharp screen-space labels anchored to the top
        // of each red/blue locator pole.
        DrawTrackingScreenLabel(
            suspect,
            "SUSPECT",
            Color.red
        );

        DrawTrackingScreenLabel(
            officer,
            "LAW ENFORCEMENT",
            Color.blue
        );
    }

}
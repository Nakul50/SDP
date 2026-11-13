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
        WaitingForInput,
        SuspectMoving,
        OfficerResponding,
        Complete,
        Blocked
    }

    // =========================================================
    // BUILDING 9 v5.4 SETTINGS
    // =========================================================

    private const int TotalFloors = 5;
    private const float FloorHeight = 4.2f;
    private const float FloorMarkerOffset = 0.65f;

    private const float MarkerSize = 1.25f;

    [System.NonSerialized] private float moveDuration = 0.40f;
    [System.NonSerialized] private float pauseBetweenMoves = 0.08f;
    [System.NonSerialized] private float detectionDistance = 1.8f;

    // Demo speed: 2x by default and stays there unless the user changes it.
    [System.NonSerialized] private float simulationSpeed = 2.0f;

    // Movement is distance-based. At 1x this is deliberately slow enough
    // to watch hallway turns; default 2x doubles it.
    private const float BaseWalkSpeed = 0.75f;

    // Geometry navigation settings.
    private const float NavGridStep = 0.25f;
    private const float WallClearance = 0.12f;
    private const float RoomTraversalPenalty = 40.0f;
    private const float TargetRoomCost = 1.20f;
    private const float StairVestibuleAllowance = 5.0f;

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
    // DYNAMIC DESTINATION / GEOMETRY NAVIGATION
    // =========================================================

    [System.NonSerialized] private string destinationInput = "";
    [System.NonSerialized] private string destinationStatus =
        "Enter a room like 301, a floor 1-5, or x,y,z.";
    [System.NonSerialized] private string activeTargetLabel = "";
    [System.NonSerialized] private string suspectDisplayText = "SUSPECT";
    [System.NonSerialized] private string officerDisplayText = "LAW ENFORCEMENT";
    [System.NonSerialized] private Vector3 simulationStartPosition;

    private struct Triangle2D
    {
        public Vector2 a, b, c;
        public float minX, maxX, minZ, maxZ;
    }

    private sealed class FinishRegion
    {
        public readonly List<Triangle2D> triangles =
            new List<Triangle2D>();

        public readonly List<Transform> labels =
            new List<Transform>();
    }

    private sealed class FloorNavigation
    {
        public int floorNumber;
        public float minX, minZ, step;
        public int width, depth;
        public bool[] valid;
        public int[] finishRegion;
        public int[] connectivity;
        public int mainComponent = -1;
        public readonly List<Triangle2D> slabTriangles =
            new List<Triangle2D>();
        public readonly List<Triangle2D> wallTriangles =
            new List<Triangle2D>();
        public readonly List<FinishRegion> regions =
            new List<FinishRegion>();
    }

    private sealed class StairOption
    {
        public string name;
        public readonly List<Vector3> points =
            new List<Vector3>();
    }

    [System.NonSerialized] private readonly FloorNavigation[] floorNavigation =
        new FloorNavigation[TotalFloors + 1];

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
            // Start in a known Floor-1 location, but DO NOT begin an
            // automatic/random route. The professor demo now waits for input.
            trackingPath.Clear();

            FloorNavigation initialNavigation =
                GetOrBuildFloorNavigation(1);

            if (initialNavigation == null)
                throw new System.InvalidOperationException("Could not build Floor 1 navigation.");

            Vector3 initialPosition;

            if (
                !FindNearestMainCellWorld(
                    initialNavigation,
                    GetStartingPoint(),
                    out initialPosition
                )
            )
            {
                throw new System.InvalidOperationException("Could not find a reachable Floor 1 start position.");
            }

            AddPathPoint(initialPosition);

            CaptureVisibility();
            runtimeRoot = new GameObject("SHIELD Simulation Runtime");
            SetupManual3DCamera();
            CreateSuspect();CreateOfficer();CreateTrails();CreateLocators();
            ShowAllFloors();

            simulationStartPosition = trackingPath[0];
            phase = SimulationPhase.WaitingForInput;
            destinationStatus = "READY - enter a room like 301, a floor 1-5, or x,y,z.";
            suspectDisplayText = "SUSPECT";
            officerDisplayText = "LAW ENFORCEMENT";
            Time.timeScale = simulationSpeed;

            sessionReady = true;
            Debug.Log("SHIELD dynamic destination simulation READY - Play session " + playSessionSerial);
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
        phase = SimulationPhase.WaitingForInput;
        suspectDetectedByOfficer = false;
        trackingPath.Clear();suspectHistory.Clear();officerHistory.Clear();
        suspectCurrentPosition = Vector3.zero;suspectPreviousPosition = Vector3.zero;
        officerCurrentPosition = Vector3.zero;officerPreviousPosition = Vector3.zero;
        selectedView = 0;cameraTarget = Vector3.zero;cameraDistance = 55f;cameraYaw = 225f;cameraPitch = 24f;
        flyYaw = 0f;flyPitch = 0f;freeFlyActive = false;
        buildingRoot = null;stairConnections = null;
        System.Array.Clear(floors, 0, floors.Length);
        System.Array.Clear(floorNavigation, 0, floorNavigation.Length);
        destinationInput = "";
        destinationStatus = "Enter a room like 301, a floor 1-5, or x,y,z.";
        activeTargetLabel = "";
        suspectDisplayText = "SUSPECT";
        officerDisplayText = "LAW ENFORCEMENT";
        simulationStartPosition = Vector3.zero;
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
        Time.timeScale = 1f;
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


    // =========================================================
    // PROFESSOR REQUEST: DYNAMIC DESTINATION
    // Hallway/door/stair routing from Building 9 geometry.
    //
    // Hard rule: walls are NEVER traversable.
    // Hallway/corridor cells have low cost.
    // Non-target room interiors have a very high cost, so the path
    // stays in hallways except when it must leave/enter a stair area.
    // The requested room is low cost so the path enters it through
    // a real wall opening and stops near its actual room label.
    // =========================================================

    void ProcessDestinationInput()
    {
        string input = destinationInput.Trim();

        if (string.IsNullOrEmpty(input))
        {
            destinationStatus =
                "Enter a room like 301, a floor 1-5, or x,y,z.";
            return;
        }

        // Optional coordinate format in the SAME input box:
        // x,y,z
        string[] coordinateParts = input.Split(',');
        if (coordinateParts.Length == 3)
        {
            float x, y, z;
            if (
                float.TryParse(coordinateParts[0].Trim(), out x) &&
                float.TryParse(coordinateParts[1].Trim(), out y) &&
                float.TryParse(coordinateParts[2].Trim(), out z)
            )
            {
                StartCoordinateDestination(
                    new Vector3(x, y, z),
                    "Coordinate " + input
                );
                return;
            }
        }

        // Single digit 1-5 = floor destination.
        int floorOnly;
        if (
            input.Length == 1 &&
            int.TryParse(input, out floorOnly) &&
            floorOnly >= 1 &&
            floorOnly <= TotalFloors
        )
        {
            StartFloorDestination(floorOnly);
            return;
        }

        // Otherwise treat input as a room label.
        Transform roomTransform;
        int roomFloor;

        if (!TryFindRoomTransform(input, out roomTransform, out roomFloor))
        {
            destinationStatus =
                "Room " + input + " was not found in Building 9.";
            return;
        }

        StartRoomDestination(
            roomTransform,
            roomFloor,
            input
        );
    }

    bool TryFindRoomTransform(
        string roomNumber,
        out Transform roomTransform,
        out int floorNumber
    )
    {
        roomTransform = null;
        floorNumber = -1;

        for (int floor = 1; floor <= TotalFloors; floor++)
        {
            Transform labels =
                floors[floor].Find("Room labels");

            if (labels == null)
                continue;

            foreach (Transform child in labels)
            {
                if (
                    string.Equals(
                        child.name.Trim(),
                        roomNumber.Trim(),
                        System.StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    roomTransform = child;
                    floorNumber = floor;
                    return true;
                }
            }
        }

        return false;
    }

    void StartRoomDestination(
        Transform roomTransform,
        int roomFloor,
        string roomNumber
    )
    {
        destinationStatus =
            "Calculating hallway + stair + doorway route to Room " +
            roomNumber + "...";

        List<Vector3> route;
        string error;

        ResetActorsForNewDestination();

        if (
            !BuildRouteToDestination(
                simulationStartPosition,
                1,
                roomTransform.position,
                roomFloor,
                roomTransform,
                out route,
                out error
            )
        )
        {
            phase = SimulationPhase.Blocked;
            destinationStatus =
                "BLOCKED: " + error +
                " No wall/jump fallback was used.";
            return;
        }

        activeTargetLabel =
            "Room " + roomNumber;

        destinationStatus =
            "Route ready: Room " + roomNumber +
            " automatically detected on Floor " + roomFloor +
            ". RED first, BLUE second.";

        StartCoroutine(
            RunDynamicRoute(
                route,
                activeTargetLabel,
                roomFloor
            )
        );
    }

    void StartFloorDestination(
        int targetFloor
    )
    {
        FloorNavigation nav =
            GetOrBuildFloorNavigation(targetFloor);

        if (nav == null)
        {
            destinationStatus =
                "Could not build navigation for Floor " + targetFloor + ".";
            return;
        }

        Vector3 desired =
            new Vector3(
                floors[targetFloor].GetComponentInChildren<Renderer>(true).bounds.center.x,
                GetFloorMarkerY(targetFloor),
                floors[targetFloor].GetComponentInChildren<Renderer>(true).bounds.center.z
            );

        Vector3 floorTarget;
        if (!FindNearestMainCellWorld(nav, desired, out floorTarget))
        {
            destinationStatus =
                "No reachable hallway point found on Floor " + targetFloor + ".";
            return;
        }

        ResetActorsForNewDestination();

        List<Vector3> route;
        string error;

        if (
            !BuildRouteToDestination(
                simulationStartPosition,
                1,
                floorTarget,
                targetFloor,
                null,
                out route,
                out error
            )
        )
        {
            phase = SimulationPhase.Blocked;
            destinationStatus =
                "BLOCKED: " + error;
            return;
        }

        activeTargetLabel =
            "Floor " + targetFloor;

        destinationStatus =
            "Route ready to Floor " + targetFloor +
            ". RED first, BLUE second.";

        StartCoroutine(
            RunDynamicRoute(
                route,
                activeTargetLabel,
                targetFloor
            )
        );
    }

    void StartCoordinateDestination(
        Vector3 coordinate,
        string label
    )
    {
        int targetFloor =
            GetNearestFloorNumber(coordinate);

        FloorNavigation nav =
            GetOrBuildFloorNavigation(targetFloor);

        if (nav == null)
        {
            destinationStatus =
                "Could not build navigation for coordinate target.";
            return;
        }

        Vector3 target;
        if (!FindNearestMainCellWorld(nav, coordinate, out target))
        {
            destinationStatus =
                "No legal walkable point exists near " + coordinate + ".";
            return;
        }

        ResetActorsForNewDestination();

        List<Vector3> route;
        string error;

        if (
            !BuildRouteToDestination(
                simulationStartPosition,
                1,
                target,
                targetFloor,
                null,
                out route,
                out error
            )
        )
        {
            phase = SimulationPhase.Blocked;
            destinationStatus =
                "BLOCKED: " + error;
            return;
        }

        activeTargetLabel = label;

        destinationStatus =
            "Coordinate route ready on Floor " + targetFloor +
            ". RED first, BLUE second.";

        StartCoroutine(
            RunDynamicRoute(
                route,
                activeTargetLabel,
                targetFloor
            )
        );
    }

    void ResetActorsForNewDestination()
    {
        StopAllCoroutines();

        Time.timeScale = simulationSpeed;

        phase = SimulationPhase.WaitingForInput;
        suspectDetectedByOfficer = false;

        trackingPath.Clear();
        suspectHistory.Clear();
        officerHistory.Clear();

        suspectCurrentPosition = simulationStartPosition;
        suspectPreviousPosition = simulationStartPosition;

        suspect.transform.position = simulationStartPosition;
        UpdateObjectParent(suspect, simulationStartPosition);
        suspect.SetActive(true);

        suspectHistory.Add(simulationStartPosition);
        UpdateSuspectTrail();

        officerCurrentPosition = simulationStartPosition;
        officerPreviousPosition = simulationStartPosition;

        officer.transform.position = simulationStartPosition;
        UpdateObjectParent(officer, simulationStartPosition);
        officer.SetActive(false);

        officerHistory.Add(simulationStartPosition);
        UpdateOfficerTrail();

        suspectDisplayText = "SUSPECT";
        officerDisplayText = "LAW ENFORCEMENT";

        UpdateLocatorVisuals();
    }

    IEnumerator RunDynamicRoute(
        List<Vector3> route,
        string targetLabel,
        int targetFloor
    )
    {
        if (route == null || route.Count < 2)
        {
            phase = SimulationPhase.Blocked;
            destinationStatus =
                "BLOCKED: generated route was empty.";
            yield break;
        }

        trackingPath.Clear();
        foreach (Vector3 point in route)
            AddPathPoint(point);

        phase = SimulationPhase.SuspectMoving;
        suspectDisplayText =
            "SUSPECT -> " + targetLabel.ToUpperInvariant();

        destinationStatus =
            "RED moving to " + targetLabel +
            " using hallway/door/stair route.";

        for (int i = 1; i < route.Count; i++)
        {
            Vector3 start = suspectCurrentPosition;
            Vector3 end = route[i];

            if (
                IsSameFloorSegment(start, end) &&
                SegmentCrossesWall(start, end)
            )
            {
                phase = SimulationPhase.Blocked;
                destinationStatus =
                    "BLOCKED: RED route touched a wall. " +
                    "Movement stopped instead of crossing it.";
                suspectDisplayText = "SUSPECT | BLOCKED";
                yield break;
            }

            suspectPreviousPosition = suspectCurrentPosition;

            yield return StartCoroutine(
                SmoothMoveSuspectDistanceBased(
                    start,
                    end
                )
            );

            suspectCurrentPosition = end;
            suspectHistory.Add(end);
            UpdateSuspectTrail();
        }

        suspectDisplayText =
            "SUSPECT | AT " + targetLabel.ToUpperInvariant();

        destinationStatus =
            "RED arrived at " + targetLabel +
            " on Floor " + targetFloor +
            ". BLUE deploying next.";

        yield return new WaitForSeconds(1.25f);

        phase = SimulationPhase.OfficerResponding;
        officer.SetActive(true);
        UpdateLocatorVisuals();

        officerCurrentPosition = route[0];
        officerPreviousPosition = route[0];
        officer.transform.position = route[0];
        UpdateObjectParent(officer, route[0]);

        officerHistory.Clear();
        officerHistory.Add(route[0]);
        UpdateOfficerTrail();

        officerDisplayText =
            "LAW ENFORCEMENT -> " +
            targetLabel.ToUpperInvariant();

        for (int i = 1; i < route.Count; i++)
        {
            Vector3 start = officerCurrentPosition;
            Vector3 end = route[i];

            if (
                IsSameFloorSegment(start, end) &&
                SegmentCrossesWall(start, end)
            )
            {
                phase = SimulationPhase.Blocked;
                destinationStatus =
                    "BLOCKED: BLUE route touched a wall. " +
                    "Movement stopped instead of crossing it.";
                officerDisplayText =
                    "LAW ENFORCEMENT | BLOCKED";
                yield break;
            }

            officerPreviousPosition = officerCurrentPosition;

            yield return StartCoroutine(
                SmoothMoveOfficerDistanceBased(
                    start,
                    end
                )
            );

            officerCurrentPosition = end;
            officerHistory.Add(end);
            UpdateOfficerTrail();
        }

        suspectDetectedByOfficer = true;
        phase = SimulationPhase.Complete;

        officerDisplayText =
            "LAW ENFORCEMENT | ARRIVED";

        destinationStatus =
            "COMPLETE: RED reached " + targetLabel +
            " through a legal doorway route; BLUE followed afterward.";
    }

    IEnumerator SmoothMoveSuspectDistanceBased(
        Vector3 start,
        Vector3 end
    )
    {
        float distance = Vector3.Distance(start, end);
        float duration =
            Mathf.Max(
                0.05f,
                distance / BaseWalkSpeed
            );

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(elapsed / duration);

            Vector3 position =
                Vector3.Lerp(start, end, t);

            suspect.transform.position = position;
            suspectCurrentPosition = position;

            UpdateObjectParent(suspect, position);
            UpdateLocatorVisuals();

            yield return null;
        }

        suspect.transform.position = end;
        suspectCurrentPosition = end;

        UpdateObjectParent(suspect, end);
        UpdateLocatorVisuals();
    }

    IEnumerator SmoothMoveOfficerDistanceBased(
        Vector3 start,
        Vector3 end
    )
    {
        float distance = Vector3.Distance(start, end);
        float duration =
            Mathf.Max(
                0.05f,
                distance / BaseWalkSpeed
            );

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(elapsed / duration);

            Vector3 position =
                Vector3.Lerp(start, end, t);

            officer.transform.position = position;
            officerCurrentPosition = position;

            UpdateObjectParent(officer, position);
            UpdateLocatorVisuals();

            yield return null;
        }

        officer.transform.position = end;
        officerCurrentPosition = end;

        UpdateObjectParent(officer, end);
        UpdateLocatorVisuals();
    }

    bool BuildRouteToDestination(
        Vector3 start,
        int startFloor,
        Vector3 target,
        int targetFloor,
        Transform targetRoomLabel,
        out List<Vector3> route,
        out string error
    )
    {
        route = new List<Vector3>();
        error = "";

        Vector3 current = start;
        int currentFloor = startFloor;

        route.Add(current);

        // Move upward floor by floor.
        while (currentFloor < targetFloor)
        {
            List<Vector3> floorPath;
            StairOption stair;
            Vector3 lowerEntry;
            Vector3 upperExit;

            if (
                !ChooseReachableStairUp(
                    currentFloor,
                    current,
                    out floorPath,
                    out stair,
                    out lowerEntry,
                    out upperExit
                )
            )
            {
                error =
                    "No hallway-reachable staircase from Floor " +
                    currentFloor + ".";
                return false;
            }

            AppendRoute(route, floorPath);
            AppendRoute(route, stair.points);

            currentFloor++;

            route.Add(upperExit);
            current = upperExit;
        }

        // Move downward if the future SDT/user target is below.
        while (currentFloor > targetFloor)
        {
            List<Vector3> floorPath;
            StairOption stair;
            Vector3 upperEntry;
            Vector3 lowerExit;

            if (
                !ChooseReachableStairDown(
                    currentFloor,
                    current,
                    out floorPath,
                    out stair,
                    out upperEntry,
                    out lowerExit
                )
            )
            {
                error =
                    "No hallway-reachable staircase down from Floor " +
                    currentFloor + ".";
                return false;
            }

            AppendRoute(route, floorPath);

            List<Vector3> reverse =
                new List<Vector3>(stair.points);
            reverse.Reverse();
            AppendRoute(route, reverse);

            currentFloor--;

            route.Add(lowerExit);
            current = lowerExit;
        }

        // Final floor route. If targetRoomLabel is present, the requested
        // room's finish region becomes low-cost so A* enters that room
        // through a real wall opening instead of stopping outside.
        List<Vector3> finalPath;
        float finalCost;

        if (
            !FindFloorPath(
                currentFloor,
                current,
                target,
                targetRoomLabel,
                out finalPath,
                out finalCost
            )
        )
        {
            error =
                targetRoomLabel != null
                    ? "No legal hallway + doorway path to Room " +
                      targetRoomLabel.name + "."
                    : "No legal path to the requested Floor " +
                      currentFloor + " position.";
            return false;
        }

        AppendRoute(route, finalPath);

        if (route.Count < 2)
        {
            error = "Route contained fewer than two points.";
            return false;
        }

        return true;
    }

    void AppendRoute(
        List<Vector3> destination,
        List<Vector3> points
    )
    {
        if (points == null)
            return;

        foreach (Vector3 point in points)
        {
            if (
                destination.Count == 0 ||
                Vector3.Distance(
                    destination[destination.Count - 1],
                    point
                ) > 0.03f
            )
            {
                destination.Add(point);
            }
        }
    }

    // -------------------------
    // FLOOR GRID CONSTRUCTION
    // -------------------------

    FloorNavigation GetOrBuildFloorNavigation(
        int floorNumber
    )
    {
        if (
            floorNumber < 1 ||
            floorNumber > TotalFloors
        )
            return null;

        if (floorNavigation[floorNumber] != null)
            return floorNavigation[floorNumber];

        FloorNavigation nav =
            BuildFloorNavigation(floorNumber);

        floorNavigation[floorNumber] = nav;
        return nav;
    }

    FloorNavigation BuildFloorNavigation(
        int floorNumber
    )
    {
        Transform floor =
            floors[floorNumber];

        Transform surfaces =
            floor.Find("Floors");

        Transform walls =
            floor.Find("Traced walls");

        if (surfaces == null || walls == null)
            return null;

        MeshFilter slab =
            surfaces.Find("Continuous slab") != null
                ? surfaces.Find("Continuous slab").GetComponent<MeshFilter>()
                : null;

        if (slab == null || slab.sharedMesh == null)
            return null;

        FloorNavigation nav =
            new FloorNavigation();

        nav.floorNumber = floorNumber;
        nav.step = NavGridStep;

        nav.slabTriangles.AddRange(
            BuildWorldTriangles(
                slab,
                null
            )
        );

        foreach (
            MeshFilter wallMesh
            in walls.GetComponentsInChildren<MeshFilter>(true)
        )
        {
            if (
                wallMesh == null ||
                wallMesh.sharedMesh == null
            )
                continue;

            // IMPORTANT:
            // "Lintel at source gap" is the wall material ABOVE a door.
            // It must not block the walkable doorway below it.
            if (
                wallMesh.gameObject.name.Contains(
                    "Lintel at source gap"
                )
            )
                continue;

            nav.wallTriangles.AddRange(
                BuildWorldTriangles(
                    wallMesh,
                    null
                )
            );
        }

        // Build connected components for each flat source-finish mesh.
        foreach (Transform child in surfaces)
        {
            if (
                !child.name.Contains("source finishes")
            )
                continue;

            MeshFilter finishMesh =
                child.GetComponent<MeshFilter>();

            if (
                finishMesh == null ||
                finishMesh.sharedMesh == null
            )
                continue;

            BuildFinishRegions(
                finishMesh,
                nav.regions
            );
        }

        // Attach room labels to their actual finish region.
        Transform labels =
            floor.Find("Room labels");

        if (labels != null)
        {
            foreach (Transform label in labels)
            {
                int region =
                    FindRegionContainingPoint(
                        nav,
                        new Vector2(
                            label.position.x,
                            label.position.z
                        )
                    );

                if (
                    region >= 0 &&
                    !nav.regions[region].labels.Contains(label)
                )
                {
                    nav.regions[region].labels.Add(label);
                }
            }
        }

        Renderer slabRenderer =
            slab.GetComponent<Renderer>();

        Bounds bounds =
            slabRenderer != null
                ? slabRenderer.bounds
                : GetFloorBounds(floor);

        nav.minX = bounds.min.x;
        nav.minZ = bounds.min.z;

        nav.width =
            Mathf.Max(
                1,
                Mathf.CeilToInt(
                    bounds.size.x /
                    nav.step
                ) + 1
            );

        nav.depth =
            Mathf.Max(
                1,
                Mathf.CeilToInt(
                    bounds.size.z /
                    nav.step
                ) + 1
            );

        int count =
            nav.width *
            nav.depth;

        nav.valid =
            new bool[count];

        nav.finishRegion =
            new int[count];

        nav.connectivity =
            new int[count];

        for (int i = 0; i < count; i++)
        {
            nav.finishRegion[i] = -1;
            nav.connectivity[i] = -1;
        }

        for (int z = 0; z < nav.depth; z++)
        {
            for (int x = 0; x < nav.width; x++)
            {
                Vector2 point =
                    CellPoint(nav, x, z);

                int index =
                    GridIndex(nav, x, z);

                if (
                    !PointInsideTriangles(
                        nav.slabTriangles,
                        point
                    )
                )
                    continue;

                if (
                    PointBlockedByWall(
                        nav,
                        point
                    )
                )
                    continue;

                nav.valid[index] = true;

                nav.finishRegion[index] =
                    FindRegionContainingPoint(
                        nav,
                        point
                    );
            }
        }

        BuildConnectivity(nav);

        Debug.Log(
            "SHIELD navigation Floor " +
            floorNumber +
            ": " +
            nav.width +
            "x" +
            nav.depth +
            ", regions=" +
            nav.regions.Count +
            ", mainComponent=" +
            nav.mainComponent
        );

        return nav;
    }

    List<Triangle2D> BuildWorldTriangles(
        MeshFilter meshFilter,
        List<int> selectedTriangleIds
    )
    {
        List<Triangle2D> result =
            new List<Triangle2D>();

        Mesh mesh =
            meshFilter.sharedMesh;

        Vector3[] vertices =
            mesh.vertices;

        int[] triangles =
            mesh.triangles;

        int triangleCount =
            triangles.Length / 3;

        for (int triangleId = 0;
             triangleId < triangleCount;
             triangleId++)
        {
            if (
                selectedTriangleIds != null &&
                !selectedTriangleIds.Contains(triangleId)
            )
                continue;

            int baseIndex =
                triangleId * 3;

            Vector3 wa =
                meshFilter.transform.TransformPoint(
                    vertices[
                        triangles[baseIndex]
                    ]
                );

            Vector3 wb =
                meshFilter.transform.TransformPoint(
                    vertices[
                        triangles[baseIndex + 1]
                    ]
                );

            Vector3 wc =
                meshFilter.transform.TransformPoint(
                    vertices[
                        triangles[baseIndex + 2]
                    ]
                );

            Vector2 a =
                new Vector2(wa.x, wa.z);
            Vector2 b =
                new Vector2(wb.x, wb.z);
            Vector2 c =
                new Vector2(wc.x, wc.z);

            float area =
                Mathf.Abs(
                    Cross2D(
                        b - a,
                        c - a
                    )
                );

            // Vertical side triangles project to nearly a line in XZ;
            // ignore them. Floor/wall top footprints remain.
            if (area < 0.00001f)
                continue;

            Triangle2D triangle =
                new Triangle2D();

            triangle.a = a;
            triangle.b = b;
            triangle.c = c;

            triangle.minX =
                Mathf.Min(
                    a.x,
                    Mathf.Min(b.x, c.x)
                );

            triangle.maxX =
                Mathf.Max(
                    a.x,
                    Mathf.Max(b.x, c.x)
                );

            triangle.minZ =
                Mathf.Min(
                    a.y,
                    Mathf.Min(b.y, c.y)
                );

            triangle.maxZ =
                Mathf.Max(
                    a.y,
                    Mathf.Max(b.y, c.y)
                );

            result.Add(triangle);
        }

        return result;
    }

    void BuildFinishRegions(
        MeshFilter meshFilter,
        List<FinishRegion> output
    )
    {
        Mesh mesh =
            meshFilter.sharedMesh;

        int[] triangles =
            mesh.triangles;

        int triangleCount =
            triangles.Length / 3;

        Dictionary<long, List<int>> edgeOwners =
            new Dictionary<long, List<int>>();

        for (int triangleId = 0;
             triangleId < triangleCount;
             triangleId++)
        {
            int baseIndex =
                triangleId * 3;

            int a = triangles[baseIndex];
            int b = triangles[baseIndex + 1];
            int c = triangles[baseIndex + 2];

            AddEdgeOwner(edgeOwners, a, b, triangleId);
            AddEdgeOwner(edgeOwners, b, c, triangleId);
            AddEdgeOwner(edgeOwners, c, a, triangleId);
        }

        List<int>[] adjacency =
            new List<int>[triangleCount];

        for (int i = 0; i < triangleCount; i++)
            adjacency[i] = new List<int>();

        foreach (
            KeyValuePair<long, List<int>> pair
            in edgeOwners
        )
        {
            List<int> owners =
                pair.Value;

            for (int i = 0; i < owners.Count; i++)
            {
                for (int j = i + 1; j < owners.Count; j++)
                {
                    int left = owners[i];
                    int right = owners[j];

                    if (!adjacency[left].Contains(right))
                        adjacency[left].Add(right);

                    if (!adjacency[right].Contains(left))
                        adjacency[right].Add(left);
                }
            }
        }

        bool[] used =
            new bool[triangleCount];

        for (int seed = 0;
             seed < triangleCount;
             seed++)
        {
            if (used[seed])
                continue;

            List<int> componentTriangles =
                new List<int>();

            Queue<int> queue =
                new Queue<int>();

            queue.Enqueue(seed);
            used[seed] = true;

            while (queue.Count > 0)
            {
                int current =
                    queue.Dequeue();

                componentTriangles.Add(current);

                foreach (int next in adjacency[current])
                {
                    if (used[next])
                        continue;

                    used[next] = true;
                    queue.Enqueue(next);
                }
            }

            FinishRegion region =
                new FinishRegion();

            region.triangles.AddRange(
                BuildWorldTriangles(
                    meshFilter,
                    componentTriangles
                )
            );

            if (region.triangles.Count > 0)
                output.Add(region);
        }
    }

    void AddEdgeOwner(
        Dictionary<long, List<int>> map,
        int a,
        int b,
        int triangleId
    )
    {
        int min =
            Mathf.Min(a, b);

        int max =
            Mathf.Max(a, b);

        long key =
            ((long)min << 32) |
            (uint)max;

        List<int> owners;

        if (!map.TryGetValue(key, out owners))
        {
            owners = new List<int>();
            map.Add(key, owners);
        }

        owners.Add(triangleId);
    }

    float Cross2D(
        Vector2 a,
        Vector2 b
    )
    {
        return
            a.x * b.y -
            a.y * b.x;
    }

    bool PointInsideTriangles(
        List<Triangle2D> triangles,
        Vector2 point
    )
    {
        foreach (Triangle2D triangle in triangles)
        {
            if (
                point.x < triangle.minX ||
                point.x > triangle.maxX ||
                point.y < triangle.minZ ||
                point.y > triangle.maxZ
            )
                continue;

            if (
                PointInTriangle(
                    point,
                    triangle
                )
            )
                return true;
        }

        return false;
    }

    bool PointInTriangle(
        Vector2 point,
        Triangle2D triangle
    )
    {
        Vector2 v0 =
            triangle.c -
            triangle.a;

        Vector2 v1 =
            triangle.b -
            triangle.a;

        Vector2 v2 =
            point -
            triangle.a;

        float denominator =
            Cross2D(v0, v1);

        if (
            Mathf.Abs(denominator) <
            0.000001f
        )
            return false;

        float u =
            Cross2D(v2, v1) /
            denominator;

        float v =
            Cross2D(v0, v2) /
            denominator;

        const float epsilon = -0.0001f;

        return
            u >= epsilon &&
            v >= epsilon &&
            u + v <= 1.0001f;
    }

    bool PointBlockedByWall(
        FloorNavigation nav,
        Vector2 center
    )
    {
        Vector2[] offsets =
        {
            Vector2.zero,
            new Vector2( WallClearance, 0f),
            new Vector2(-WallClearance, 0f),
            new Vector2(0f,  WallClearance),
            new Vector2(0f, -WallClearance),
            new Vector2( WallClearance * 0.70f,  WallClearance * 0.70f),
            new Vector2(-WallClearance * 0.70f,  WallClearance * 0.70f),
            new Vector2( WallClearance * 0.70f, -WallClearance * 0.70f),
            new Vector2(-WallClearance * 0.70f, -WallClearance * 0.70f)
        };

        foreach (Vector2 offset in offsets)
        {
            if (
                PointInsideTriangles(
                    nav.wallTriangles,
                    center + offset
                )
            )
                return true;
        }

        return false;
    }

    int FindRegionContainingPoint(
        FloorNavigation nav,
        Vector2 point
    )
    {
        for (int i = 0;
             i < nav.regions.Count;
             i++)
        {
            if (
                PointInsideTriangles(
                    nav.regions[i].triangles,
                    point
                )
            )
                return i;
        }

        return -1;
    }

    Vector2 CellPoint(
        FloorNavigation nav,
        int x,
        int z
    )
    {
        return new Vector2(
            nav.minX +
            x * nav.step,
            nav.minZ +
            z * nav.step
        );
    }

    int GridIndex(
        FloorNavigation nav,
        int x,
        int z
    )
    {
        return
            z * nav.width +
            x;
    }

    void GridCoordinates(
        FloorNavigation nav,
        int index,
        out int x,
        out int z
    )
    {
        z = index / nav.width;
        x = index -
            z * nav.width;
    }

    void BuildConnectivity(
        FloorNavigation nav
    )
    {
        int componentId = 0;
        int largestId = -1;
        int largestSize = 0;

        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int z = 0; z < nav.depth; z++)
        {
            for (int x = 0; x < nav.width; x++)
            {
                int startIndex =
                    GridIndex(nav, x, z);

                if (
                    !nav.valid[startIndex] ||
                    nav.connectivity[startIndex] >= 0
                )
                    continue;

                Queue<int> queue =
                    new Queue<int>();

                queue.Enqueue(startIndex);
                nav.connectivity[startIndex] = componentId;

                int size = 0;

                while (queue.Count > 0)
                {
                    int current =
                        queue.Dequeue();

                    size++;

                    int cx, cz;
                    GridCoordinates(
                        nav,
                        current,
                        out cx,
                        out cz
                    );

                    for (int direction = 0;
                         direction < 4;
                         direction++)
                    {
                        int nx = cx + dx[direction];
                        int nz = cz + dz[direction];

                        if (
                            nx < 0 ||
                            nz < 0 ||
                            nx >= nav.width ||
                            nz >= nav.depth
                        )
                            continue;

                        int next =
                            GridIndex(nav, nx, nz);

                        if (
                            !nav.valid[next] ||
                            nav.connectivity[next] >= 0
                        )
                            continue;

                        nav.connectivity[next] =
                            componentId;

                        queue.Enqueue(next);
                    }
                }

                if (size > largestSize)
                {
                    largestSize = size;
                    largestId = componentId;
                }

                componentId++;
            }
        }

        nav.mainComponent = largestId;
    }

    bool FindNearestMainCellWorld(
        FloorNavigation nav,
        Vector3 desired,
        out Vector3 world
    )
    {
        int cell;

        if (
            !FindNearestCell(
                nav,
                desired,
                -1,
                null,
                false,
                out cell
            )
        )
        {
            world = Vector3.zero;
            return false;
        }

        world =
            CellWorld(
                nav,
                cell
            );

        return true;
    }

    bool FindNearestMainCellWithClearAccess(
        FloorNavigation nav,
        Vector3 desired,
        out Vector3 world
    )
    {
        int centerX =
            Mathf.RoundToInt(
                (desired.x - nav.minX) /
                nav.step
            );

        int centerZ =
            Mathf.RoundToInt(
                (desired.z - nav.minZ) /
                nav.step
            );

        float bestDistance =
            float.MaxValue;

        bool found =
            false;

        world =
            Vector3.zero;

        int maxRadius =
            Mathf.Min(
                Mathf.Max(
                    nav.width,
                    nav.depth
                ),
                Mathf.CeilToInt(
                    5.0f /
                    nav.step
                )
            );

        Vector3 desiredOnFloor =
            new Vector3(
                desired.x,
                GetFloorMarkerY(
                    nav.floorNumber
                ),
                desired.z
            );

        for (int radius = 0;
             radius <= maxRadius;
             radius++)
        {
            for (int dx = -radius;
                 dx <= radius;
                 dx++)
            {
                for (int dz = -radius;
                     dz <= radius;
                     dz++)
                {
                    if (
                        radius > 0 &&
                        Mathf.Abs(dx) != radius &&
                        Mathf.Abs(dz) != radius
                    )
                        continue;

                    int x =
                        centerX + dx;

                    int z =
                        centerZ + dz;

                    if (
                        x < 0 ||
                        z < 0 ||
                        x >= nav.width ||
                        z >= nav.depth
                    )
                        continue;

                    int cell =
                        GridIndex(
                            nav,
                            x,
                            z
                        );

                    if (
                        !nav.valid[cell] ||
                        nav.connectivity[cell] !=
                        nav.mainComponent
                    )
                        continue;

                    Vector3 candidate =
                        CellWorld(
                            nav,
                            cell
                        );

                    if (
                        SegmentCrossesWallOnFloor(
                            nav,
                            candidate,
                            desiredOnFloor
                        )
                    )
                        continue;

                    float distance =
                        HorizontalDistance(
                            candidate,
                            desiredOnFloor
                        );

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        world = candidate;
                        found = true;
                    }
                }
            }

            if (found)
                return true;
        }

        return false;
    }

    bool FindNearestCell(
        FloorNavigation nav,
        Vector3 desired,
        int requiredTargetRegion,
        Transform targetLabel,
        bool requireTargetRoomRegion,
        out int resultCell
    )
    {
        int centerX =
            Mathf.RoundToInt(
                (desired.x - nav.minX) /
                nav.step
            );

        int centerZ =
            Mathf.RoundToInt(
                (desired.z - nav.minZ) /
                nav.step
            );

        resultCell = -1;
        float bestDistance =
            float.MaxValue;

        int maxRadius =
            Mathf.Max(
                nav.width,
                nav.depth
            );

        for (int radius = 0;
             radius <= maxRadius;
             radius++)
        {
            bool foundAtRadius = false;

            for (int dx = -radius;
                 dx <= radius;
                 dx++)
            {
                for (int dz = -radius;
                     dz <= radius;
                     dz++)
                {
                    if (
                        radius > 0 &&
                        Mathf.Abs(dx) != radius &&
                        Mathf.Abs(dz) != radius
                    )
                        continue;

                    int x = centerX + dx;
                    int z = centerZ + dz;

                    if (
                        x < 0 ||
                        z < 0 ||
                        x >= nav.width ||
                        z >= nav.depth
                    )
                        continue;

                    int cell =
                        GridIndex(nav, x, z);

                    if (
                        !nav.valid[cell] ||
                        nav.connectivity[cell] !=
                        nav.mainComponent
                    )
                        continue;

                    if (
                        requireTargetRoomRegion &&
                        !CellBelongsToTargetRoom(
                            nav,
                            cell,
                            requiredTargetRegion,
                            targetLabel
                        )
                    )
                        continue;

                    Vector3 candidate =
                        CellWorld(nav, cell);

                    float distance =
                        HorizontalDistance(
                            candidate,
                            desired
                        );

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        resultCell = cell;
                        foundAtRadius = true;
                    }
                }
            }

            if (foundAtRadius)
                return true;
        }

        return false;
    }

    Vector3 CellWorld(
        FloorNavigation nav,
        int cell
    )
    {
        int x, z;

        GridCoordinates(
            nav,
            cell,
            out x,
            out z
        );

        Vector2 point =
            CellPoint(
                nav,
                x,
                z
            );

        return new Vector3(
            point.x,
            GetFloorMarkerY(
                nav.floorNumber
            ),
            point.y
        );
    }

    bool CellBelongsToTargetRoom(
        FloorNavigation nav,
        int cell,
        int targetRegion,
        Transform targetLabel
    )
    {
        if (
            targetRegion < 0 ||
            nav.finishRegion[cell] !=
            targetRegion
        )
            return false;

        if (targetLabel == null)
            return true;

        FinishRegion region =
            nav.regions[targetRegion];

        if (region.labels.Count <= 1)
            return true;

        Vector3 world =
            CellWorld(nav, cell);

        Transform nearest =
            null;

        float nearestDistance =
            float.MaxValue;

        foreach (Transform label in region.labels)
        {
            float distance =
                HorizontalDistance(
                    world,
                    label.position
                );

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = label;
            }
        }

        return nearest == targetLabel;
    }

    bool CellAllowedForThisPath(
        FloorNavigation nav,
        int cell,
        int targetRegion,
        Transform targetLabel,
        Vector3 pathStart,
        Vector3 pathTarget
    )
    {
        int region =
            nav.finishRegion[cell];

        // Corridor/hallway.
        if (region < 0)
            return true;

        // The requested room itself is allowed so the marker can
        // enter through its real door and stop inside.
        if (
            CellBelongsToTargetRoom(
                nav,
                cell,
                targetRegion,
                targetLabel
            )
        )
            return true;

        // Some generated stair vestibules / arrival pockets are
        // represented by a source-finish region. Allow only a small
        // local area near the start or stair destination so the marker
        // can exit/enter that vestibule through its doorway.
        Vector3 world =
            CellWorld(
                nav,
                cell
            );

        float fromStart =
            HorizontalDistance(
                world,
                pathStart
            );

        float fromTarget =
            HorizontalDistance(
                world,
                pathTarget
            );

        return
            fromStart <= StairVestibuleAllowance ||
            fromTarget <= StairVestibuleAllowance;
    }

    float CellTraversalCost(
        FloorNavigation nav,
        int cell,
        int targetRegion,
        Transform targetLabel
    )
    {
        int region =
            nav.finishRegion[cell];

        if (region < 0)
            return 1f; // corridor / hallway

        if (
            CellBelongsToTargetRoom(
                nav,
                cell,
                targetRegion,
                targetLabel
            )
        )
            return TargetRoomCost;

        // Rooms remain technically traversable ONLY through real door gaps,
        // but the very high cost makes the solver strongly prefer hallways.
        return RoomTraversalPenalty;
    }

    bool FindFloorPath(
        int floorNumber,
        Vector3 start,
        Vector3 target,
        Transform targetRoomLabel,
        out List<Vector3> path,
        out float totalCost
    )
    {
        path =
            new List<Vector3>();

        totalCost = 0f;

        FloorNavigation nav =
            GetOrBuildFloorNavigation(
                floorNumber
            );

        if (
            nav == null ||
            nav.mainComponent < 0
        )
            return false;

        int targetRegion = -1;

        if (targetRoomLabel != null)
        {
            targetRegion =
                FindRegionContainingPoint(
                    nav,
                    new Vector2(
                        targetRoomLabel.position.x,
                        targetRoomLabel.position.z
                    )
                );
        }

        int startCell;
        if (
            !FindNearestCell(
                nav,
                start,
                -1,
                null,
                false,
                out startCell
            )
        )
            return false;

        int goalCell;

        bool needTargetRoom =
            targetRoomLabel != null &&
            targetRegion >= 0;

        if (
            !FindNearestCell(
                nav,
                target,
                targetRegion,
                targetRoomLabel,
                needTargetRoom,
                out goalCell
            )
        )
            return false;

        int count =
            nav.width *
            nav.depth;

        float[] gScore =
            new float[count];

        int[] cameFrom =
            new int[count];

        bool[] closed =
            new bool[count];

        for (int i = 0; i < count; i++)
        {
            gScore[i] = float.PositiveInfinity;
            cameFrom[i] = -1;
        }

        List<int> open =
            new List<int>();

        gScore[startCell] = 0f;
        open.Add(startCell);

        int[] dx =
        {
            1, -1, 0, 0,
            1, 1, -1, -1
        };

        int[] dz =
        {
            0, 0, 1, -1,
            1, -1, 1, -1
        };

        float[] stepCost =
        {
            1f, 1f, 1f, 1f,
            1.41421356f,
            1.41421356f,
            1.41421356f,
            1.41421356f
        };

        int safety = 0;
        int safetyLimit =
            count * 4;

        while (
            open.Count > 0 &&
            safety < safetyLimit
        )
        {
            safety++;

            int bestOpenIndex = 0;
            float bestF =
                float.PositiveInfinity;

            for (int i = 0; i < open.Count; i++)
            {
                int candidate =
                    open[i];

                int cx, cz;
                GridCoordinates(
                    nav,
                    candidate,
                    out cx,
                    out cz
                );

                int gx, gz;
                GridCoordinates(
                    nav,
                    goalCell,
                    out gx,
                    out gz
                );

                float heuristic =
                    Mathf.Sqrt(
                        (cx - gx) * (cx - gx) +
                        (cz - gz) * (cz - gz)
                    );

                float f =
                    gScore[candidate] +
                    heuristic;

                if (f < bestF)
                {
                    bestF = f;
                    bestOpenIndex = i;
                }
            }

            int current =
                open[bestOpenIndex];

            open.RemoveAt(bestOpenIndex);

            if (closed[current])
                continue;

            if (current == goalCell)
            {
                List<int> cells =
                    new List<int>();

                int trace = goalCell;
                cells.Add(trace);

                while (
                    trace != startCell &&
                    trace >= 0
                )
                {
                    trace =
                        cameFrom[trace];

                    if (trace >= 0)
                        cells.Add(trace);
                }

                if (
                    cells.Count == 0 ||
                    cells[cells.Count - 1] !=
                    startCell
                )
                    return false;

                cells.Reverse();

                path =
                    SimplifyGridPath(
                        nav,
                        cells
                    );

                totalCost =
                    gScore[goalCell];

                // For room input, finish close to the actual label,
                // but only if the final short segment does not cross a wall.
                if (targetRoomLabel != null)
                {
                    Vector3 exactRoomPoint =
                        new Vector3(
                            targetRoomLabel.position.x,
                            GetFloorMarkerY(floorNumber),
                            targetRoomLabel.position.z
                        );

                    if (
                        path.Count > 0 &&
                        !SegmentCrossesWallOnFloor(
                            nav,
                            path[path.Count - 1],
                            exactRoomPoint
                        )
                    )
                    {
                        path.Add(exactRoomPoint);
                    }
                }

                return path.Count > 0;
            }

            closed[current] = true;

            int currentX, currentZ;
            GridCoordinates(
                nav,
                current,
                out currentX,
                out currentZ
            );

            for (int direction = 0;
                 direction < 8;
                 direction++)
            {
                int nextX =
                    currentX +
                    dx[direction];

                int nextZ =
                    currentZ +
                    dz[direction];

                if (
                    nextX < 0 ||
                    nextZ < 0 ||
                    nextX >= nav.width ||
                    nextZ >= nav.depth
                )
                    continue;

                int next =
                    GridIndex(
                        nav,
                        nextX,
                        nextZ
                    );

                if (
                    !nav.valid[next] ||
                    nav.connectivity[next] !=
                    nav.mainComponent ||
                    closed[next]
                )
                    continue;

                // Never cut diagonally through a wall corner.
                if (
                    dx[direction] != 0 &&
                    dz[direction] != 0
                )
                {
                    int sideA =
                        GridIndex(
                            nav,
                            currentX +
                            dx[direction],
                            currentZ
                        );

                    int sideB =
                        GridIndex(
                            nav,
                            currentX,
                            currentZ +
                            dz[direction]
                        );

                    if (
                        !nav.valid[sideA] ||
                        !nav.valid[sideB]
                    )
                        continue;
                }

                if (
                    !CellAllowedForThisPath(
                        nav,
                        next,
                        targetRegion,
                        targetRoomLabel,
                        start,
                        target
                    )
                )
                    continue;

                float currentCost =
                    CellTraversalCost(
                        nav,
                        current,
                        targetRegion,
                        targetRoomLabel
                    );

                float nextCost =
                    CellTraversalCost(
                        nav,
                        next,
                        targetRegion,
                        targetRoomLabel
                    );

                float tentative =
                    gScore[current] +
                    stepCost[direction] *
                    (currentCost + nextCost) *
                    0.5f;

                if (tentative >= gScore[next])
                    continue;

                cameFrom[next] = current;
                gScore[next] = tentative;

                if (!open.Contains(next))
                    open.Add(next);
            }
        }

        return false;
    }

    List<Vector3> SimplifyGridPath(
        FloorNavigation nav,
        List<int> cells
    )
    {
        List<Vector3> result =
            new List<Vector3>();

        if (cells == null || cells.Count == 0)
            return result;

        result.Add(
            CellWorld(
                nav,
                cells[0]
            )
        );

        int previousDX = 0;
        int previousDZ = 0;

        for (int i = 1;
             i < cells.Count;
             i++)
        {
            int previousX, previousZ;
            int currentX, currentZ;

            GridCoordinates(
                nav,
                cells[i - 1],
                out previousX,
                out previousZ
            );

            GridCoordinates(
                nav,
                cells[i],
                out currentX,
                out currentZ
            );

            int dx =
                currentX -
                previousX;

            int dz =
                currentZ -
                previousZ;

            if (
                i == 1
            )
            {
                previousDX = dx;
                previousDZ = dz;
            }
            else if (
                dx != previousDX ||
                dz != previousDZ
            )
            {
                result.Add(
                    CellWorld(
                        nav,
                        cells[i - 1]
                    )
                );

                previousDX = dx;
                previousDZ = dz;
            }
        }

        Vector3 last =
            CellWorld(
                nav,
                cells[cells.Count - 1]
            );

        if (
            result.Count == 0 ||
            Vector3.Distance(
                result[result.Count - 1],
                last
            ) > 0.03f
        )
        {
            result.Add(last);
        }

        return result;
    }

    // -------------------------
    // STAIRS
    // -------------------------

    List<StairOption> GetStairOptions(
        int lowerFloor
    )
    {
        List<StairOption> result =
            new List<StairOption>();

        if (stairConnections == null)
            return result;

        Transform group =
            stairConnections.Find(
                "From Floor " +
                lowerFloor
            );

        if (group == null)
            return result;

        Dictionary<string, List<Transform>> flightGroups =
            new Dictionary<string, List<Transform>>();

        foreach (Transform child in group)
        {
            Renderer[] renderers =
                child.GetComponentsInChildren<Renderer>(true);

            bool hasTread =
                renderers.Any(
                    renderer =>
                        renderer.gameObject.name.StartsWith("Tread")
                );

            if (!hasTread)
                continue;

            string name =
                child.name.Trim();

            int space =
                name.IndexOf(' ');

            string key =
                space > 0
                    ? name.Substring(0, space)
                    : name;

            List<Transform> flights;

            if (!flightGroups.TryGetValue(key, out flights))
            {
                flights =
                    new List<Transform>();

                flightGroups.Add(
                    key,
                    flights
                );
            }

            flights.Add(child);
        }

        foreach (
            KeyValuePair<string, List<Transform>> pair
            in flightGroups
        )
        {
            List<Transform> flights =
                pair.Value
                    .OrderBy(
                        flight =>
                            GetLowestTreadY(flight)
                    )
                    .ToList();

            StairOption option =
                new StairOption();

            option.name =
                pair.Key;

            foreach (Transform flight in flights)
            {
                List<Vector3> treadPoints =
                    flight
                        .GetComponentsInChildren<Renderer>(true)
                        .Where(
                            renderer =>
                                renderer.gameObject.name.StartsWith("Tread")
                        )
                        .Select(
                            renderer =>
                                RaiseAboveTread(
                                    renderer.bounds.center
                                )
                        )
                        .OrderBy(
                            point =>
                                point.y
                        )
                        .ToList();

                if (treadPoints.Count == 0)
                    continue;

                if (option.points.Count > 0)
                {
                    Vector3 from =
                        option.points[
                            option.points.Count - 1
                        ];

                    Vector3 to =
                        treadPoints[0];

                    float gap =
                        Vector3.Distance(
                            from,
                            to
                        );

                    int inserts =
                        Mathf.Max(
                            1,
                            Mathf.CeilToInt(
                                gap /
                                0.25f
                            )
                        );

                    for (int step = 1;
                         step < inserts;
                         step++)
                    {
                        option.points.Add(
                            Vector3.Lerp(
                                from,
                                to,
                                step /
                                (float)inserts
                            )
                        );
                    }
                }

                option.points.AddRange(
                    treadPoints
                );
            }

            if (option.points.Count < 3)
                continue;

            float verticalSpan =
                option.points[
                    option.points.Count - 1
                ].y -
                option.points[0].y;

            if (
                verticalSpan <
                FloorHeight *
                0.55f
            )
                continue;

            result.Add(option);
        }

        return result;
    }

    float GetLowestTreadY(
        Transform flight
    )
    {
        Renderer[] renderers =
            flight.GetComponentsInChildren<Renderer>(true);

        float result =
            float.MaxValue;

        foreach (Renderer renderer in renderers)
        {
            if (
                renderer.gameObject.name.StartsWith("Tread")
            )
            {
                result =
                    Mathf.Min(
                        result,
                        renderer.bounds.center.y
                    );
            }
        }

        return result;
    }

    bool ChooseReachableStairUp(
        int lowerFloor,
        Vector3 current,
        out List<Vector3> floorPath,
        out StairOption chosen,
        out Vector3 lowerEntry,
        out Vector3 upperExit
    )
    {
        floorPath =
            null;
        chosen =
            null;
        lowerEntry =
            Vector3.zero;
        upperExit =
            Vector3.zero;

        float bestCost =
            float.MaxValue;

        foreach (
            StairOption option
            in GetStairOptions(lowerFloor)
        )
        {
            Vector3 rawEntry =
                option.points[0];

            FloorNavigation lowerNav =
                GetOrBuildFloorNavigation(
                    lowerFloor
                );

            Vector3 entry;

            if (
                lowerNav == null ||
                !FindNearestMainCellWithClearAccess(
                    lowerNav,
                    rawEntry,
                    out entry
                )
            )
                continue;

            List<Vector3> candidatePath;
            float candidateCost;

            if (
                !FindFloorPath(
                    lowerFloor,
                    current,
                    entry,
                    null,
                    out candidatePath,
                    out candidateCost
                )
            )
                continue;

            FloorNavigation upperNav =
                GetOrBuildFloorNavigation(
                    lowerFloor + 1
                );

            Vector3 rawExit =
                option.points[
                    option.points.Count - 1
                ];

            Vector3 exit;

            if (
                upperNav == null ||
                !FindNearestMainCellWithClearAccess(
                    upperNav,
                    rawExit,
                    out exit
                )
            )
                continue;

            if (candidateCost < bestCost)
            {
                bestCost = candidateCost;
                floorPath = candidatePath;
                chosen = option;
                lowerEntry = entry;
                upperExit = exit;
            }
        }

        return
            chosen != null &&
            floorPath != null;
    }

    bool ChooseReachableStairDown(
        int currentFloor,
        Vector3 current,
        out List<Vector3> floorPath,
        out StairOption chosen,
        out Vector3 upperEntry,
        out Vector3 lowerExit
    )
    {
        floorPath =
            null;
        chosen =
            null;
        upperEntry =
            Vector3.zero;
        lowerExit =
            Vector3.zero;

        int lowerFloor =
            currentFloor - 1;

        float bestCost =
            float.MaxValue;

        foreach (
            StairOption option
            in GetStairOptions(lowerFloor)
        )
        {
            Vector3 rawUpper =
                option.points[
                    option.points.Count - 1
                ];

            FloorNavigation currentNav =
                GetOrBuildFloorNavigation(
                    currentFloor
                );

            Vector3 entry;

            if (
                currentNav == null ||
                !FindNearestMainCellWithClearAccess(
                    currentNav,
                    rawUpper,
                    out entry
                )
            )
                continue;

            List<Vector3> candidatePath;
            float candidateCost;

            if (
                !FindFloorPath(
                    currentFloor,
                    current,
                    entry,
                    null,
                    out candidatePath,
                    out candidateCost
                )
            )
                continue;

            FloorNavigation lowerNav =
                GetOrBuildFloorNavigation(
                    lowerFloor
                );

            Vector3 rawLower =
                option.points[0];

            Vector3 exit;

            if (
                lowerNav == null ||
                !FindNearestMainCellWithClearAccess(
                    lowerNav,
                    rawLower,
                    out exit
                )
            )
                continue;

            if (candidateCost < bestCost)
            {
                bestCost = candidateCost;
                floorPath = candidatePath;
                chosen = option;
                upperEntry = entry;
                lowerExit = exit;
            }
        }

        return
            chosen != null &&
            floorPath != null;
    }

    // -------------------------
    // FINAL WALL GUARD
    // -------------------------

    bool IsSameFloorSegment(
        Vector3 a,
        Vector3 b
    )
    {
        return
            IsOnFloorLevel(a) &&
            IsOnFloorLevel(b) &&
            GetNearestFloorNumber(a) ==
            GetNearestFloorNumber(b);
    }

    bool SegmentCrossesWall(
        Vector3 a,
        Vector3 b
    )
    {
        if (!IsSameFloorSegment(a, b))
            return false;

        int floor =
            GetNearestFloorNumber(a);

        FloorNavigation nav =
            GetOrBuildFloorNavigation(
                floor
            );

        if (nav == null)
            return true;

        return
            SegmentCrossesWallOnFloor(
                nav,
                a,
                b
            );
    }

    bool SegmentCrossesWallOnFloor(
        FloorNavigation nav,
        Vector3 a,
        Vector3 b
    )
    {
        float distance =
            HorizontalDistance(a, b);

        int samples =
            Mathf.Max(
                2,
                Mathf.CeilToInt(
                    distance /
                    0.08f
                )
            );

        for (int i = 1;
             i < samples;
             i++)
        {
            float t =
                i /
                (float)samples;

            Vector3 world =
                Vector3.Lerp(
                    a,
                    b,
                    t
                );

            if (
                PointBlockedByWall(
                    nav,
                    new Vector2(
                        world.x,
                        world.z
                    )
                )
            )
                return true;
        }

        return false;
    }

    void BuildFiveFloorPath()
    {
        trackingPath.Clear();

        Vector3 current =
            GetStartingPoint();

        AddPathPoint(current);

        // Short patrol on Floor 1 before taking the first stair.
        AddFloorPatrol(
            1,
            current
        );

        // Return to the original stair-side point before going upstairs.
        AddPathPoint(current);

        for (int lowerFloor = 1;
             lowerFloor < TotalFloors;
             lowerFloor++)
        {
            List<Vector3> stairRoute =
                GetGeneratedStairTransition(
                    lowerFloor,
                    current
                );

            if (stairRoute.Count > 0)
            {
                foreach (Vector3 point in stairRoute)
                {
                    AddPathPoint(point);
                }

                current =
                    trackingPath[
                        trackingPath.Count - 1
                    ];
            }
            else
            {
                // Fallback only if Unity cannot reconstruct
                // Nakul's generated stair treads automatically.
                float upperY =
                    GetFloorMarkerY(
                        lowerFloor + 1
                    );

                for (int step = 1;
                     step <= 16;
                     step++)
                {
                    float t =
                        step / 16f;

                    AddPathPoint(
                        new Vector3(
                            current.x,
                            Mathf.Lerp(
                                current.y,
                                upperY,
                                t
                            ),
                            current.z
                        )
                    );
                }

                current =
                    trackingPath[
                        trackingPath.Count - 1
                    ];
            }

            int newFloor =
                lowerFloor + 1;

            Vector3 arrival =
                new Vector3(
                    current.x,
                    GetFloorMarkerY(newFloor),
                    current.z
                );

            AddPathPoint(arrival);

            current = arrival;

            // Walk around a little on the new floor before
            // moving on to the next staircase.
            AddFloorPatrol(
                newFloor,
                arrival
            );

            // Return near the stair/arrival point so the next
            // stair transition starts cleanly.
            AddPathPoint(arrival);

            current = arrival;
        }
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

        for (int i = 1;
             i < trackingPath.Count;
             i++)
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

        for (int i = 1;
             i < trackingPath.Count;
             i++)
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

        while (elapsed < moveDuration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(
                    elapsed /
                    moveDuration
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

        while (elapsed < moveDuration)
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(
                    elapsed /
                    moveDuration
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

        string text =
            title +
            " | " +
            GetLocationLabel(
                target.transform.position
            );

        Color oldColor =
            GUI.color;

        GUI.color =
            color;

        GUI.Box(
            new Rect(
                x - 85f,
                y - 24f,
                170f,
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
        // Future Step 2: SDT can call this every 5-10 seconds.
        // It uses the same legal geometry route instead of teleporting.
        StartCoordinateDestination(
            new Vector3(x, y, z),
            "SDT Position"
        );
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
                455,
                445
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
            new Rect(30, 137, 410, 22),
            "Detected: " +
            suspectDetectedByOfficer
        );

        if (
            GUI.Button(
                new Rect(
                    30,
                    172,
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
                        172,
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
            new Rect(30, 212, 410, 22),
            "3D Camera - right mouse for free flight"
        );

        if (
            GUI.Button(
                new Rect(30, 240, 75, 30),
                "Left"
            )
        )
        {
            RotateCamera(-12f, 0f);
        }

        if (
            GUI.Button(
                new Rect(110, 240, 75, 30),
                "Right"
            )
        )
        {
            RotateCamera(12f, 0f);
        }

        if (
            GUI.Button(
                new Rect(190, 240, 75, 30),
                "Up"
            )
        )
        {
            RotateCamera(0f, 7f);
        }

        if (
            GUI.Button(
                new Rect(270, 240, 75, 30),
                "Down"
            )
        )
        {
            RotateCamera(0f, -7f);
        }

        if (
            GUI.Button(
                new Rect(30, 275, 100, 30),
                "Zoom In"
            )
        )
        {
            ZoomCamera(-8f);
        }

        if (
            GUI.Button(
                new Rect(140, 275, 100, 30),
                "Zoom Out"
            )
        )
        {
            ZoomCamera(8f);
        }

        if (
            GUI.Button(
                new Rect(250, 275, 100, 30),
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
            new Rect(30, 315, 410, 22),
            "Hold RMB: mouse look + WASD fly | Q down / E up"
        );

        GUI.Label(
            new Rect(30, 338, 410, 22),
            "Shift: faster | Release RMB / Esc: cursor | Reset: overview"
        );

        if (GUI.Button(new Rect(30, 370, 175, 28), "Restart simulation")) restartRequested = true;

        GUI.Label(
            new Rect(
                30,
                402,
                410,
                22
            ),
            "Speed: " +
            simulationSpeed.ToString("F0") +
            "x (default 2x)"
        );

        // -----------------------------------------------------
        // PROFESSOR REQUEST - USER DESTINATION INPUT
        // -----------------------------------------------------
        GUI.Box(
            new Rect(
                480,
                15,
                430,
                245
            ),
            "Dynamic Destination"
        );

        GUI.Label(
            new Rect(
                495,
                48,
                395,
                42
            ),
            "Enter room (301), floor (3), or coordinate (x,y,z):"
        );

        destinationInput =
            GUI.TextField(
                new Rect(
                    495,
                    92,
                    185,
                    30
                ),
                destinationInput
            );

        if (
            GUI.Button(
                new Rect(
                    690,
                    92,
                    195,
                    30
                ),
                "Run RED -> BLUE"
            )
        )
        {
            ProcessDestinationInput();
        }

        GUIStyle destinationStyle =
            new GUIStyle(
                GUI.skin.label
            );

        destinationStyle.wordWrap =
            true;

        GUI.Label(
            new Rect(
                495,
                132,
                390,
                65
            ),
            destinationStatus,
            destinationStyle
        );

        GUI.Label(
            new Rect(
                495,
                198,
                55,
                22
            ),
            "Speed:"
        );

        if (
            GUI.Button(
                new Rect(
                    550,
                    195,
                    55,
                    28
                ),
                "1x"
            )
        )
        {
            simulationSpeed = 1f;
            Time.timeScale = simulationSpeed;
        }

        if (
            GUI.Button(
                new Rect(
                    612,
                    195,
                    55,
                    28
                ),
                "2x"
            )
        )
        {
            simulationSpeed = 2f;
            Time.timeScale = simulationSpeed;
        }

        if (
            GUI.Button(
                new Rect(
                    674,
                    195,
                    55,
                    28
                ),
                "3x"
            )
        )
        {
            simulationSpeed = 3f;
            Time.timeScale = simulationSpeed;
        }

        if (
            GUI.Button(
                new Rect(
                    736,
                    195,
                    55,
                    28
                ),
                "4x"
            )
        )
        {
            simulationSpeed = 4f;
            Time.timeScale = simulationSpeed;
        }

        GUI.Label(
            new Rect(
                800,
                198,
                85,
                22
            ),
            "Now: " +
            simulationSpeed.ToString("F0") +
            "x"
        );

        // Sharp screen-space labels anchored to the top
        // of each red/blue locator pole.
        DrawTrackingScreenLabel(
            suspect,
            suspectDisplayText,
            Color.red
        );

        DrawTrackingScreenLabel(
            officer,
            officerDisplayText,
            Color.blue
        );
    }

}
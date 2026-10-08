using System.Collections.Generic;
using UnityEngine;

public class SimulationManager : MonoBehaviour
{
    private enum SimulationPhase
    {
        SuspectMoving,
        OfficerEntering,
        Complete
    }

    private SimulationPhase phase = SimulationPhase.SuspectMoving;

    // =========================
    // SUSPECT
    // =========================

    private GameObject suspect;

    private Vector2 suspectCurrentPosition;
    private Vector2 suspectPreviousPosition;

    private bool suspectMovementDetected = false;

    private List<Vector2> suspectHistory =
        new List<Vector2>();

    // =========================
    // LAW ENFORCEMENT
    // =========================

    private GameObject officer;

    private Vector2 officerCurrentPosition;
    private Vector2 officerPreviousPosition;

    private List<Vector2> officerHistory =
        new List<Vector2>();

    private bool suspectDetectedByOfficer = false;

    private float detectionDistance = 1.5f;

    // =========================
    // TIMING
    // =========================

    private float moveInterval = 2.0f;

    // =========================
    // SUSPECT +2 MOVEMENT
    // =========================

    private int regularMoveCount = 0;
    private int numberOfRegularMoves = 6;

    // =========================
    // SUSPECT HARD-CODED PATH
    // =========================

    private Vector2[] suspectHardCodedPositions =
    {
        new Vector2(-4, 2),
        new Vector2(-6, 0),
        new Vector2(-4, -3),
        new Vector2(0, -3),
        new Vector2(4, -3),
        new Vector2(6, 0),
        new Vector2(4, 2),
        new Vector2(0, 2),
        new Vector2(0, 6)
    };

    private int suspectHardCodedIndex = 0;

    // =========================
    // OFFICER PATH
    // =========================

    private Vector2[] officerPath =
    {
        new Vector2(0, -12),  // outside
        new Vector2(0, -10),  // entrance
        new Vector2(0, -8),
        new Vector2(0, -6),
        new Vector2(0, -4),
        new Vector2(0, -2),
        new Vector2(0, 0),
        new Vector2(0, 2),
        new Vector2(0, 4),
        new Vector2(0, 6)
    };

    private int officerPathIndex = 0;

    // =========================
    // TRAILS
    // =========================

    private LineRenderer suspectTrail;
    private LineRenderer officerTrail;

    // =========================
    // START
    // =========================

    void Start()
    {

        CreateSuspect();
        CreateOfficer();

        CreateSuspectTrail();
        CreateOfficerTrail();

        Debug.Log("SIMULATION STARTED");
        Debug.Log("PHASE 1: SUSPECT MOVEMENT");

        InvokeRepeating(
            nameof(SimulationRound),
            moveInterval,
            moveInterval
        );
    }

    // =========================
    // MAIN LOOP
    // =========================

    void SimulationRound()
    {
        if (phase == SimulationPhase.SuspectMoving)
        {
            RunSuspectPhase();
        }
        else if (phase == SimulationPhase.OfficerEntering)
        {
            RunOfficerPhase();
        }
        else
        {
            CancelInvoke(nameof(SimulationRound));
        }
    }

    // =========================
    // SUSPECT PHASE
    // =========================

    void RunSuspectPhase()
    {
        // First: move Y +2 using matrix
        if (regularMoveCount < numberOfRegularMoves)
        {
            MoveSuspectUsingMatrix(
                new Vector2(0, 2)
            );

            regularMoveCount++;

            return;
        }

        // Then: hard-coded jump positions
        if (
            suspectHardCodedIndex <
            suspectHardCodedPositions.Length
        )
        {
            MoveSuspectToPosition(
                suspectHardCodedPositions[
                    suspectHardCodedIndex
                ]
            );

            suspectHardCodedIndex++;

            return;
        }

        // Suspect movement finished
        Debug.Log("SUSPECT MOVEMENT COMPLETE");

        Debug.Log(
            "LAST KNOWN SUSPECT POSITION = "
            + suspectCurrentPosition
        );

        PrintSuspectHistory();

        // Start officer phase
        officer.SetActive(true);

        officerPathIndex = 0;

        officerCurrentPosition =
            officerPath[0];

        officerPreviousPosition =
            officerCurrentPosition;

        officer.transform.position =
            ConvertOfficerToUnityPosition(
                officerCurrentPosition
            );

        officerHistory.Clear();

        officerHistory.Add(
            officerCurrentPosition
        );

        UpdateOfficerTrail();

        Debug.Log(
            "PHASE 2: LAW ENFORCEMENT ENTERING"
        );

        Debug.Log(
            "OFFICER STARTING OUTSIDE AT "
            + officerCurrentPosition
        );

        phase =
            SimulationPhase.OfficerEntering;
    }

    // =========================
    // OFFICER PHASE
    // =========================

    void RunOfficerPhase()
    {
        CheckOfficerDetection();

        if (suspectDetectedByOfficer)
        {
            FinishSimulation();
            return;
        }

        // Follow entry path
        if (
            officerPathIndex <
            officerPath.Length - 1
        )
        {
            officerPreviousPosition =
                officerCurrentPosition;

            officerPathIndex++;

            officerCurrentPosition =
                officerPath[
                    officerPathIndex
                ];

            officer.transform.position =
                ConvertOfficerToUnityPosition(
                    officerCurrentPosition
                );

            officerHistory.Add(
                officerCurrentPosition
            );

            UpdateOfficerTrail();

            Debug.Log(
                "OFFICER MOVED FROM "
                + officerPreviousPosition
                + " TO "
                + officerCurrentPosition
            );

            CheckOfficerDetection();

            return;
        }

        // If safe route ends,
        // move to last known suspect position
        if (!suspectDetectedByOfficer)
        {
            officerPreviousPosition =
                officerCurrentPosition;

            officerCurrentPosition =
                suspectCurrentPosition;

            officer.transform.position =
                ConvertOfficerToUnityPosition(
                    officerCurrentPosition
                );

            officerHistory.Add(
                officerCurrentPosition
            );

            UpdateOfficerTrail();

            Debug.Log(
                "OFFICER MOVING TO LAST KNOWN SUSPECT POSITION"
            );

            CheckOfficerDetection();
        }

        FinishSimulation();
    }

    // =========================
    // MATRIX SUSPECT MOVEMENT
    // =========================

    void MoveSuspectUsingMatrix(
        Vector2 translation
    )
    {
        suspectPreviousPosition =
            suspectCurrentPosition;

        float x =
            suspectCurrentPosition.x;

        float y =
            suspectCurrentPosition.y;

        float dx =
            translation.x;

        float dy =
            translation.y;

        /*
         * Translation matrix:
         *
         * | 1 0 dx |
         * | 0 1 dy |
         * | 0 0  1 |
         */

        float newX =
            (1 * x) +
            (0 * y) +
            (dx * 1);

        float newY =
            (0 * x) +
            (1 * y) +
            (dy * 1);

        Vector2 calculatedPosition =
            new Vector2(
                newX,
                newY
            );

        Debug.Log(
            "----- MATRIX CALCULATION -----"
        );

        Debug.Log(
            "START = "
            + suspectPreviousPosition
        );

        Debug.Log(
            "[1 0 " + dx + "]"
        );

        Debug.Log(
            "[0 1 " + dy + "]"
        );

        Debug.Log(
            "[0 0 1]"
        );

        Debug.Log(
            "END = "
            + calculatedPosition
        );

        // Move only after calculation
        suspectCurrentPosition =
            calculatedPosition;

        UpdateSuspect();
    }

    // =========================
    // HARD-CODED SUSPECT MOVE
    // =========================

    void MoveSuspectToPosition(
        Vector2 destination
    )
    {
        suspectPreviousPosition =
            suspectCurrentPosition;

        Debug.Log(
            "HARD-CODED MOVE: "
            + suspectPreviousPosition
            + " -> "
            + destination
        );

        suspectCurrentPosition =
            destination;

        UpdateSuspect();
    }

    // =========================
    // UPDATE SUSPECT
    // =========================

    void UpdateSuspect()
    {
        float distance =
            Vector2.Distance(
                suspectPreviousPosition,
                suspectCurrentPosition
            );

        suspectMovementDetected =
            distance > 0.001f;

        if (suspectMovementDetected)
        {
            suspect.transform.position =
                ConvertSuspectToUnityPosition(
                    suspectCurrentPosition
                );

            suspectHistory.Add(
                suspectCurrentPosition
            );

            UpdateSuspectTrail();

            Debug.Log(
                "MOVEMENT DETECTED: "
                + suspectPreviousPosition
                + " -> "
                + suspectCurrentPosition
            );
        }
    }

    // =========================
    // OFFICER DETECTION
    // =========================

    void CheckOfficerDetection()
    {
        float distance =
            Vector2.Distance(
                officerCurrentPosition,
                suspectCurrentPosition
            );

        Debug.Log(
            "Officer = "
            + officerCurrentPosition
            + " | Suspect = "
            + suspectCurrentPosition
            + " | Distance = "
            + distance
        );

        if (distance <= detectionDistance)
        {
            suspectDetectedByOfficer = true;

            Debug.Log(
                "***** SUSPECT DETECTED BY LAW ENFORCEMENT *****"
            );

            Debug.Log(
                "DETECTION POSITION = "
                + suspectCurrentPosition
            );
        }
        else
        {
            suspectDetectedByOfficer = false;
        }
    }

    // =========================
    // FINISH
    // =========================

    void FinishSimulation()
    {
        phase =
            SimulationPhase.Complete;

        CancelInvoke(
            nameof(SimulationRound)
        );

        Debug.Log("SIMULATION COMPLETE");

        Debug.Log(
            "FINAL SUSPECT POSITION = "
            + suspectCurrentPosition
        );

        Debug.Log(
            "FINAL OFFICER POSITION = "
            + officerCurrentPosition
        );

        Debug.Log(
            "SUSPECT DETECTED = "
            + suspectDetectedByOfficer
        );

        PrintOfficerHistory();
    }

    // =========================
    // BUILDING
    // =========================

    void CreateBuilding()
    {
        CreateCube(
            "Floor",
            new Vector3(0, -0.1f, 0),
            new Vector3(20, 0.2f, 20),
            new Color(
                0.8f,
                0.8f,
                0.8f
            )
        );

        // Outer walls
        CreateCube(
            "TopWall",
            new Vector3(0, 1, 10),
            new Vector3(20, 2, 0.3f),
            Color.white
        );

        CreateCube(
            "BottomWallLeft",
            new Vector3(-6, 1, -10),
            new Vector3(8, 2, 0.3f),
            Color.white
        );

        CreateCube(
            "BottomWallRight",
            new Vector3(6, 1, -10),
            new Vector3(8, 2, 0.3f),
            Color.white
        );

        CreateCube(
            "LeftWall",
            new Vector3(-10, 1, 0),
            new Vector3(0.3f, 2, 20),
            Color.white
        );

        CreateCube(
            "RightWall",
            new Vector3(10, 1, 0),
            new Vector3(0.3f, 2, 20),
            Color.white
        );

        // Internal walls
        CreateCube(
            "LeftRoomWall",
            new Vector3(-6.5f, 1, 3),
            new Vector3(5, 2, 0.3f),
            Color.white
        );

        CreateCube(
            "RightRoomWall",
            new Vector3(6.5f, 1, 3),
            new Vector3(5, 2, 0.3f),
            Color.white
        );

        CreateCube(
            "LeftVerticalWall",
            new Vector3(-3, 1, 7),
            new Vector3(0.3f, 2, 6),
            Color.white
        );

        CreateCube(
            "RightVerticalWall",
            new Vector3(3, 1, 7),
            new Vector3(0.3f, 2, 6),
            Color.white
        );

        CreateCube(
            "LowerLeftWall",
            new Vector3(-6, 1, -4),
            new Vector3(6, 2, 0.3f),
            Color.white
        );

        CreateCube(
            "LowerRightWall",
            new Vector3(6, 1, -4),
            new Vector3(6, 2, 0.3f),
            Color.white
        );
    }

    // =========================
    // COORDINATE AXES
    // =========================

    void CreateCoordinateAxes()
    {
        CreateCube(
            "X Axis",
            new Vector3(0, 0.03f, 0),
            new Vector3(19, 0.03f, 0.08f),
            Color.red
        );

        CreateCube(
            "Y Axis",
            new Vector3(0, 0.04f, 0),
            new Vector3(0.08f, 0.03f, 19),
            Color.blue
        );
    }

    // =========================
    // CREATE SUSPECT
    // =========================

    void CreateSuspect()
    {
        suspect =
            GameObject.CreatePrimitive(
                PrimitiveType.Sphere
            );

        suspect.name =
            "Suspect";

        suspectCurrentPosition =
            new Vector2(
                0,
                -10
            );

        suspectPreviousPosition =
            suspectCurrentPosition;

        suspect.transform.position =
            ConvertSuspectToUnityPosition(
                suspectCurrentPosition
            );

        suspect.transform.localScale =
            new Vector3(
                0.7f,
                0.7f,
                0.7f
            );

        Renderer renderer =
            suspect.GetComponent<Renderer>();

        renderer.material.color =
            Color.red;

        suspectHistory.Add(
            suspectCurrentPosition
        );
    }

    // =========================
    // CREATE OFFICER
    // =========================

    void CreateOfficer()
    {
        officer =
            GameObject.CreatePrimitive(
                PrimitiveType.Capsule
            );

        officer.name =
            "Law Enforcement";

        officerCurrentPosition =
            officerPath[0];

        officerPreviousPosition =
            officerCurrentPosition;

        officer.transform.position =
            ConvertOfficerToUnityPosition(
                officerCurrentPosition
            );

        officer.transform.localScale =
            new Vector3(
                0.5f,
                0.8f,
                0.5f
            );

        Renderer renderer =
            officer.GetComponent<Renderer>();

        renderer.material.color =
            Color.blue;

        officerHistory.Add(
            officerCurrentPosition
        );

        // Hide until suspect movement is done
        officer.SetActive(false);
    }

    // =========================
    // SUSPECT TRAIL
    // =========================

    void CreateSuspectTrail()
    {
        GameObject trailObject =
            new GameObject(
                "Suspect Movement Path"
            );

        suspectTrail =
            trailObject.AddComponent<LineRenderer>();

        SetupTrail(
            suspectTrail,
            Color.red
        );

        suspectTrail.positionCount = 1;

        suspectTrail.SetPosition(
            0,
            ConvertToSuspectTrailPosition(
                suspectCurrentPosition
            )
        );
    }

    // =========================
    // OFFICER TRAIL
    // =========================

    void CreateOfficerTrail()
    {
        GameObject trailObject =
            new GameObject(
                "Officer Movement Path"
            );

        officerTrail =
            trailObject.AddComponent<LineRenderer>();

        SetupTrail(
            officerTrail,
            Color.blue
        );

        officerTrail.positionCount = 0;
    }

    // =========================
    // TRAIL SETUP
    // =========================

    void SetupTrail(
        LineRenderer line,
        Color color
    )
    {
        line.startWidth = 0.15f;
        line.endWidth = 0.15f;

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

        line.material =
            new Material(shader);

        line.startColor = color;
        line.endColor = color;
    }

    // =========================
    // UPDATE SUSPECT TRAIL
    // =========================

    void UpdateSuspectTrail()
    {
        suspectTrail.positionCount =
            suspectHistory.Count;

        for (
            int i = 0;
            i < suspectHistory.Count;
            i++
        )
        {
            suspectTrail.SetPosition(
                i,
                ConvertToSuspectTrailPosition(
                    suspectHistory[i]
                )
            );
        }
    }

    // =========================
    // UPDATE OFFICER TRAIL
    // =========================

    void UpdateOfficerTrail()
    {
        officerTrail.positionCount =
            officerHistory.Count;

        for (
            int i = 0;
            i < officerHistory.Count;
            i++
        )
        {
            officerTrail.SetPosition(
                i,
                ConvertToOfficerTrailPosition(
                    officerHistory[i]
                )
            );
        }
    }

    // =========================
    // HISTORY
    // =========================

    void PrintSuspectHistory()
    {
        Debug.Log(
            "----- WHERE HAS THE SUSPECT GONE? -----"
        );

        for (
            int i = 0;
            i < suspectHistory.Count;
            i++
        )
        {
            Debug.Log(
                "Suspect Position "
                + i
                + " = "
                + suspectHistory[i]
            );
        }
    }

    void PrintOfficerHistory()
    {
        Debug.Log(
            "----- LAW ENFORCEMENT PATH -----"
        );

        for (
            int i = 0;
            i < officerHistory.Count;
            i++
        )
        {
            Debug.Log(
                "Officer Position "
                + i
                + " = "
                + officerHistory[i]
            );
        }
    }

    // =========================
    // COORDINATE CONVERSION
    // =========================

    Vector3 ConvertSuspectToUnityPosition(
        Vector2 position
    )
    {
        return new Vector3(
            position.x,
            0.5f,
            position.y
        );
    }

    Vector3 ConvertOfficerToUnityPosition(
        Vector2 position
    )
    {
        return new Vector3(
            position.x,
            0.8f,
            position.y
        );
    }

    Vector3 ConvertToSuspectTrailPosition(
        Vector2 position
    )
    {
        return new Vector3(
            position.x,
            0.15f,
            position.y
        );
    }

    Vector3 ConvertToOfficerTrailPosition(
        Vector2 position
    )
    {
        return new Vector3(
            position.x,
            0.18f,
            position.y
        );
    }

    // =========================
    // CREATE OBJECT
    // =========================

    void CreateCube(
        string objectName,
        Vector3 position,
        Vector3 scale,
        Color color
    )
    {
        GameObject cube =
            GameObject.CreatePrimitive(
                PrimitiveType.Cube
            );

        cube.name =
            objectName;

        cube.transform.position =
            position;

        cube.transform.localScale =
            scale;

        Renderer renderer =
            cube.GetComponent<Renderer>();

        renderer.material.color =
            color;
    }

    // =========================
    // GUI
    // =========================

    void OnGUI()
    {
        GUI.Box(
            new Rect(
                15,
                15,
                400,
                260
            ),
            "Building Suspect Tracking System"
        );

        GUI.Label(
            new Rect(30, 45, 360, 25),
            "Phase: " + phase
        );

        GUI.Label(
            new Rect(30, 70, 360, 25),
            "Suspect Previous: "
            + suspectPreviousPosition
        );

        GUI.Label(
            new Rect(30, 95, 360, 25),
            "Suspect Current: "
            + suspectCurrentPosition
        );

        GUI.Label(
            new Rect(30, 120, 360, 25),
            "Suspect Movement Detected: "
            + suspectMovementDetected
        );

        GUI.Label(
            new Rect(30, 145, 360, 25),
            "Officer Current: "
            + officerCurrentPosition
        );

        GUI.Label(
            new Rect(30, 170, 360, 25),
            "Suspect Detected By Officer: "
            + suspectDetectedByOfficer
        );

        GUI.Label(
            new Rect(30, 195, 360, 25),
            "Last Known Suspect Position: "
            + suspectCurrentPosition
        );

        GUI.Label(
            new Rect(30, 220, 360, 25),
            "Red = Suspect | Blue = Law Enforcement"
        );

        GUI.Label(
            new Rect(30, 245, 360, 25),
            "Red/Blue Lines = Movement History"
        );
    }
}
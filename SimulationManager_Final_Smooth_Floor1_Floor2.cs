using System.Collections;
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

    private float moveDuration = 0.8f;
    private float pauseBetweenMoves = 0.15f;
    private float detectionDistance = 1.5f;

    private const float floor2Elevation = 3.35f;
    private const float floorThickness = 0.12f;

    private const float stairStartX = 10.3f;
    private const float stairStartZ = 14.7f;
    private const int stairStepCount = 18;
    private const float stairRun = 0.25f;

    private const float markerSize = 0.60f;
    private const float floorMarkerOffset = 0.50f;

    private GameObject suspect;
    private GameObject officer;

    private Vector3 suspectCurrentPosition;
    private Vector3 suspectPreviousPosition;
    private Vector3 officerCurrentPosition;
    private Vector3 officerPreviousPosition;

    private bool suspectMovementDetected = false;
    private bool suspectDetectedByOfficer = false;

    private readonly List<Vector3> suspectHistory = new List<Vector3>();
    private readonly List<Vector3> officerHistory = new List<Vector3>();
    private readonly List<Vector3> suspectPath = new List<Vector3>();
    private readonly List<Vector3> officerPath = new List<Vector3>();

    private LineRenderer suspectTrail;
    private LineRenderer officerTrail;

    void Start()
    {
        if (GameObject.Find("Engineering Building") == null)
        {
            Debug.LogError("Engineering Building not found. Generate Nakul's Building 9 model first.");
            enabled = false;
            return;
        }

        BuildPaths();
        CreateSuspect();
        CreateOfficer();
        CreateSuspectTrail();
        CreateOfficerTrail();

        Debug.Log("SIMULATION STARTED");
        Debug.Log("PHASE 1: SUSPECT MOVEMENT");

        StartCoroutine(RunSimulation());
    }

    void BuildPaths()
    {
        suspectPath.Clear();
        officerPath.Clear();

        // FLOOR 1
        AddSuspectPoint(0.0f, floorMarkerOffset, 18.3f);
        AddSuspectPoint(4.0f, floorMarkerOffset, 18.3f);
        AddSuspectPoint(7.5f, floorMarkerOffset, 18.3f);
        AddSuspectPoint(7.5f, floorMarkerOffset, 16.5f);
        AddSuspectPoint(8.5f, floorMarkerOffset, 15.5f);
        AddSuspectPoint(9.5f, floorMarkerOffset, 14.9f);
        AddSuspectPoint(stairStartX, floorMarkerOffset, stairStartZ);

        // STAIR TRANSITION
        float stairRise = floor2Elevation / stairStepCount;

        for (int i = 1; i <= stairStepCount; i++)
        {
            float stepTopY = floorThickness + (stairRise * i);
            float markerY = stepTopY + (markerSize * 0.5f);
            float z = stairStartZ + ((i - 1) * stairRun);

            AddSuspectPoint(stairStartX, markerY, z);
        }

        // FLOOR 2
        float floor2Y = floor2Elevation + floorMarkerOffset;

        AddSuspectPoint(15.0f, floor2Y, 19.8f);
        AddSuspectPoint(22.0f, floor2Y, 19.8f);
        AddSuspectPoint(29.0f, floor2Y, 19.8f);
        AddSuspectPoint(38.0f, floor2Y, 19.8f);
        AddSuspectPoint(46.0f, floor2Y, 19.8f);
        AddSuspectPoint(50.0f, floor2Y, 16.0f);
        AddSuspectPoint(55.0f, floor2Y, 12.0f);
        AddSuspectPoint(59.2f, floor2Y, 9.1f);
        AddSuspectPoint(69.0f, floor2Y, 9.1f);
        AddSuspectPoint(69.0f, floor2Y, 14.0f);
        AddSuspectPoint(75.0f, floor2Y, 20.0f);
        AddSuspectPoint(82.5f, floor2Y, 20.0f);

        // OFFICER STARTS OUTSIDE, THEN FOLLOWS SAME ROUTE
        officerPath.Add(new Vector3(-3.0f, floorMarkerOffset, 18.3f));

        foreach (Vector3 point in suspectPath)
        {
            officerPath.Add(point);
        }
    }

    void AddSuspectPoint(float x, float y, float z)
    {
        suspectPath.Add(new Vector3(x, y, z));
    }

    IEnumerator RunSimulation()
    {
        // SUSPECT
        for (int i = 1; i < suspectPath.Count; i++)
        {
            Vector3 destination = suspectPath[i];

            Vector3 calculatedEnd =
                CalculateMatrixTranslation(
                    suspectCurrentPosition,
                    destination
                );

            yield return StartCoroutine(
                SmoothMoveSuspect(
                    suspectCurrentPosition,
                    calculatedEnd
                )
            );

            suspectPreviousPosition = suspectCurrentPosition;
            suspectCurrentPosition = calculatedEnd;

            suspectHistory.Add(suspectCurrentPosition);
            suspectMovementDetected = true;
            UpdateSuspectTrail();

            Debug.Log(
                "SUSPECT MOVED TO " +
                suspectCurrentPosition +
                " | " +
                GetFloorName(suspectCurrentPosition)
            );

            yield return new WaitForSeconds(pauseBetweenMoves);
        }

        Debug.Log("SUSPECT MOVEMENT COMPLETE");
        Debug.Log("LAST KNOWN SUSPECT POSITION = " + suspectCurrentPosition);

        PrintSuspectHistory();

        // OFFICER
        phase = SimulationPhase.OfficerEntering;

        officer.SetActive(true);

        officerCurrentPosition = officerPath[0];
        officerPreviousPosition = officerCurrentPosition;
        officer.transform.position = officerCurrentPosition;

        officerHistory.Clear();
        officerHistory.Add(officerCurrentPosition);
        UpdateOfficerTrail();

        Debug.Log("PHASE 2: LAW ENFORCEMENT ENTERING BUILDING 9");

        for (int i = 1; i < officerPath.Count; i++)
        {
            if (suspectDetectedByOfficer)
                break;

            officerPreviousPosition = officerCurrentPosition;
            Vector3 destination = officerPath[i];

            yield return StartCoroutine(
                SmoothMoveOfficer(
                    officerCurrentPosition,
                    destination
                )
            );

            officerCurrentPosition = destination;
            officerHistory.Add(officerCurrentPosition);
            UpdateOfficerTrail();

            Debug.Log(
                "OFFICER MOVED TO " +
                officerCurrentPosition +
                " | " +
                GetFloorName(officerCurrentPosition)
            );

            CheckOfficerDetection();

            yield return new WaitForSeconds(pauseBetweenMoves);
        }

        if (!suspectDetectedByOfficer)
        {
            Debug.Log("OFFICER MOVING TO LAST KNOWN SUSPECT POSITION");

            officerPreviousPosition = officerCurrentPosition;

            yield return StartCoroutine(
                SmoothMoveOfficer(
                    officerCurrentPosition,
                    suspectCurrentPosition
                )
            );

            officerCurrentPosition = suspectCurrentPosition;
            officerHistory.Add(officerCurrentPosition);
            UpdateOfficerTrail();
            CheckOfficerDetection();
        }

        FinishSimulation();
    }

    Vector3 CalculateMatrixTranslation(Vector3 start, Vector3 destination)
    {
        float x = start.x;
        float y = start.y;
        float z = start.z;

        float dx = destination.x - start.x;
        float dy = destination.y - start.y;
        float dz = destination.z - start.z;

        float newX = (1 * x) + (0 * y) + (0 * z) + dx;
        float newY = (0 * x) + (1 * y) + (0 * z) + dy;
        float newZ = (0 * x) + (0 * y) + (1 * z) + dz;

        Vector3 calculatedPosition = new Vector3(newX, newY, newZ);

        Debug.Log("----- 3D MATRIX CALCULATION -----");
        Debug.Log("START = " + start);
        Debug.Log("[1 0 0 " + dx + "]");
        Debug.Log("[0 1 0 " + dy + "]");
        Debug.Log("[0 0 1 " + dz + "]");
        Debug.Log("[0 0 0 1]");
        Debug.Log("END = " + calculatedPosition);

        return calculatedPosition;
    }

    IEnumerator SmoothMoveSuspect(Vector3 start, Vector3 end)
    {
        suspectPreviousPosition = start;
        float elapsed = 0f;

        while (elapsed < moveDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / moveDuration);

            Vector3 position = Vector3.Lerp(start, end, t);

            suspect.transform.position = position;
            suspectCurrentPosition = position;

            yield return null;
        }

        suspect.transform.position = end;
        suspectCurrentPosition = end;
    }

    IEnumerator SmoothMoveOfficer(Vector3 start, Vector3 end)
    {
        float elapsed = 0f;

        while (elapsed < moveDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / moveDuration);

            Vector3 position = Vector3.Lerp(start, end, t);

            officer.transform.position = position;
            officerCurrentPosition = position;

            CheckOfficerDetection();

            if (suspectDetectedByOfficer)
                yield break;

            yield return null;
        }

        officer.transform.position = end;
        officerCurrentPosition = end;
    }

    void CheckOfficerDetection()
    {
        if (!officer.activeSelf)
            return;

        float distance =
            Vector3.Distance(
                officer.transform.position,
                suspect.transform.position
            );

        if (distance <= detectionDistance)
        {
            suspectDetectedByOfficer = true;

            Debug.Log("***** SUSPECT DETECTED BY LAW ENFORCEMENT *****");
            Debug.Log("DETECTION POSITION = " + suspect.transform.position);
        }
    }

    void CreateSuspect()
    {
        suspect = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        suspect.name = "Suspect";
        suspect.transform.localScale = Vector3.one * markerSize;

        suspectCurrentPosition = suspectPath[0];
        suspectPreviousPosition = suspectCurrentPosition;
        suspect.transform.position = suspectCurrentPosition;

        Renderer renderer = suspect.GetComponent<Renderer>();
        renderer.material.color = Color.red;

        suspectHistory.Add(suspectCurrentPosition);
    }

    void CreateOfficer()
    {
        officer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        officer.name = "Law Enforcement";
        officer.transform.localScale = Vector3.one * markerSize;

        officerCurrentPosition = officerPath[0];
        officerPreviousPosition = officerCurrentPosition;
        officer.transform.position = officerCurrentPosition;

        Renderer renderer = officer.GetComponent<Renderer>();
        renderer.material.color = Color.blue;

        officerHistory.Add(officerCurrentPosition);
        officer.SetActive(false);
    }

    void CreateSuspectTrail()
    {
        GameObject trailObject = new GameObject("Suspect Movement Path");
        suspectTrail = trailObject.AddComponent<LineRenderer>();

        SetupTrail(suspectTrail, Color.red);

        suspectTrail.positionCount = 1;
        suspectTrail.SetPosition(0, GetTrailPosition(suspectCurrentPosition));
    }

    void CreateOfficerTrail()
    {
        GameObject trailObject = new GameObject("Officer Movement Path");
        officerTrail = trailObject.AddComponent<LineRenderer>();

        SetupTrail(officerTrail, Color.blue);
        officerTrail.positionCount = 0;
    }

    void SetupTrail(LineRenderer line, Color color)
    {
        line.startWidth = 0.15f;
        line.endWidth = 0.15f;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");

        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        line.material = new Material(shader);
        line.startColor = color;
        line.endColor = color;
    }

    void UpdateSuspectTrail()
    {
        suspectTrail.positionCount = suspectHistory.Count;

        for (int i = 0; i < suspectHistory.Count; i++)
        {
            suspectTrail.SetPosition(
                i,
                GetTrailPosition(suspectHistory[i])
            );
        }
    }

    void UpdateOfficerTrail()
    {
        officerTrail.positionCount = officerHistory.Count;

        for (int i = 0; i < officerHistory.Count; i++)
        {
            officerTrail.SetPosition(
                i,
                GetTrailPosition(officerHistory[i])
            );
        }
    }

    Vector3 GetTrailPosition(Vector3 position)
    {
        return new Vector3(
            position.x,
            position.y - 0.28f,
            position.z
        );
    }

    string GetFloorName(Vector3 position)
    {
        if (position.y < 1.0f)
            return "Floor 1";

        if (position.y < 3.60f)
            return "Stair Transition";

        return "Floor 2";
    }

    void PrintSuspectHistory()
    {
        Debug.Log("----- WHERE HAS THE SUSPECT GONE? -----");

        for (int i = 0; i < suspectHistory.Count; i++)
        {
            Debug.Log(
                "Suspect Position " +
                i +
                " = " +
                suspectHistory[i] +
                " | " +
                GetFloorName(suspectHistory[i])
            );
        }
    }

    void PrintOfficerHistory()
    {
        Debug.Log("----- LAW ENFORCEMENT PATH -----");

        for (int i = 0; i < officerHistory.Count; i++)
        {
            Debug.Log(
                "Officer Position " +
                i +
                " = " +
                officerHistory[i] +
                " | " +
                GetFloorName(officerHistory[i])
            );
        }
    }

    void FinishSimulation()
    {
        phase = SimulationPhase.Complete;

        Debug.Log("SIMULATION COMPLETE");
        Debug.Log("FINAL SUSPECT POSITION = " + suspect.transform.position);
        Debug.Log("FINAL OFFICER POSITION = " + officer.transform.position);
        Debug.Log("SUSPECT DETECTED = " + suspectDetectedByOfficer);

        PrintOfficerHistory();
    }

    void OnGUI()
    {
        GUI.Box(
            new Rect(15, 15, 390, 260),
            "Building 9 Suspect Tracking System"
        );

        GUI.Label(
            new Rect(30, 45, 360, 25),
            "Phase: " + phase
        );

        GUI.Label(
            new Rect(30, 70, 360, 25),
            "Suspect Previous: " + suspectPreviousPosition
        );

        GUI.Label(
            new Rect(30, 95, 360, 25),
            "Suspect Current: " + suspectCurrentPosition
        );

        GUI.Label(
            new Rect(30, 120, 360, 25),
            "Suspect Floor: " + GetFloorName(suspect.transform.position)
        );

        GUI.Label(
            new Rect(30, 145, 360, 25),
            "Officer Current: " + officerCurrentPosition
        );

        GUI.Label(
            new Rect(30, 170, 360, 25),
            "Officer Floor: " + GetFloorName(officer.transform.position)
        );

        GUI.Label(
            new Rect(30, 195, 360, 25),
            "Suspect Detected: " + suspectDetectedByOfficer
        );

        GUI.Label(
            new Rect(30, 220, 360, 25),
            "Last Known Position: " +
            suspectHistory[suspectHistory.Count - 1]
        );

        GUI.Label(
            new Rect(30, 245, 360, 25),
            "Red = Suspect | Blue = Law Enforcement"
        );
    }
}

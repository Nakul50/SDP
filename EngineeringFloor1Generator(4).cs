// Engineering Building generator v4.1 - enclosed stair cores; lobby L starts on Floor 2.
// Save as Assets/Editor/EngineeringFloor1Generator.cs. Keep only ONE copy of this class.
// Tools > Engineering Building > Building Generator
// Coordinates come from the supplied drawings; dimensions and small utility areas are approximate.
// Small service cupboards 126/124A/128/114 and 252/254 are simplified into stair vestibules.
// Unclear door positions are inferred; stair sizes are blockout dimensions, not a construction survey.
// North = +X; south = -X; plan-up = +Z. Do not independently scale either level.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class EngineeringFloor1Generator : EditorWindow
{
    const string RootName = "Engineering Building";
    const string MaterialFolder = "Assets/EngineeringBuildingGenerated/V4Materials";
    const float PixelsPerMetre = 15.2f;
    const float Epsilon = 0.0001f;
    [SerializeField] float buildingScale = 1f;
    [SerializeField] float storeyHeight = 3.6f;
    [SerializeField] float slabDepth = 0.22f;
    [SerializeField] float wallDepth = 0.16f;
    [SerializeField] float openingWidth = 0.95f;
    [SerializeField] bool showLabels = true;
    [SerializeField] int topStairLanding = 4;
    [SerializeField] bool includeSite = true;
    [SerializeField] bool addCamera = false;
    Vector2 scroll;
    string status = "v4.1: both plans use one coordinate system. North points right on the drawings.";
    readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

    sealed class Room
    {
        public string Name, Category, DoorSide;
        public Rect Bounds;
        public Room(string name, Rect bounds, string category, string side)
        { Name = name; Bounds = bounds; Category = category; DoorSide = side; }
    }
    sealed class Plan
    {
        public readonly List<Room> Rooms = new List<Room>();
        public readonly List<Rect> Footprints = new List<Rect>();
        public readonly List<Rect> Outdoor = new List<Rect>();
        public readonly List<Portal> ExtraDoors = new List<Portal>();
    }
    sealed class Wall
    {
        public bool Horizontal;
        public float Fixed, Start, End;
        public int A, B;
        public Wall(bool horizontal, float fixedAt, float start, float end, int a, int b)
        { Horizontal = horizontal; Fixed = fixedAt; Start = start; End = end; A = a; B = b; }
    }
    sealed class Portal
    {
        public bool Horizontal;
        public float Fixed, Start, End;
        public string Name;
        public Portal(bool horizontal, float fixedAt, float center, float width, string name)
        { Horizontal = horizontal; Fixed = fixedAt; Start = center - width / 2; End = center + width / 2; Name = name; }
    }
    sealed class Grid
    {
        public float[] X, Z;
        // -1 outside, -3 outdoor, <= -10 enclosed stair core, 0 corridor, positive room ID.
        public bool[,] Aperture;
        public int[,] Owner;
        public readonly List<Wall> Walls = new List<Wall>();
        public readonly List<Portal> Doors = new List<Portal>();
    }
    sealed class Stair
    {
        public string Name;
        public Rect Envelope;
        public bool IsL, Reverse;
        public float Lane, Run, Apron, Landing, LowerRun, UpperRun;
        public readonly List<Rect> Reserved = new List<Rect>();
        public readonly List<Rect> Holes = new List<Rect>();
        public Stair(string name, Rect envelope, bool isL)
        {
            Name = name; Envelope = envelope; IsL = isL;
            if (isL)
            {
                // L core fits the notch around room 214. Its entrance apron is in lobby 216.
                const float margin = .12f;
                Lane = 1.34f;
                LowerRun = envelope.width - margin - Lane;
                UpperRun = envelope.height - margin - Lane;
                float notchX = P(1464, 512).x, notchZ = P(1464, 512).y;
                Reserved.Add(Rect.MinMaxRect(envelope.xMin, envelope.yMin, envelope.xMax, notchZ));
                Reserved.Add(Rect.MinMaxRect(envelope.xMin, notchZ, notchX, envelope.yMax));
                Reserved.Add(Rect.MinMaxRect(envelope.xMax, envelope.yMin, envelope.xMax + 1.6f, notchZ));
                // Only the receiving Floor 3 slab has these openings. Floor 2 stays solid here.
                Holes.Add(Rect.MinMaxRect(envelope.xMin + margin, envelope.yMin + margin,
                    envelope.xMax, envelope.yMin + margin + Lane));
                Holes.Add(Rect.MinMaxRect(envelope.xMin + margin, envelope.yMin + margin + Lane,
                    envelope.xMin + margin + Lane, envelope.yMax));
            }
            else
            {
                const float margin = .12f;
                const float gap = .14f;
                Lane = (envelope.width - 2 * margin - gap) / 2;
                Landing = Mathf.Max(1f, Lane);
                Apron = Mathf.Max(.55f, Mathf.Min(1.1f, envelope.height * .2f));
                Run = envelope.height - 2 * margin - Apron - Landing;
                if (Run < 1.5f || Lane < .7f) throw new InvalidOperationException("Stair reserve is too small: " + name);
                Reserved.Add(envelope);
                Holes.Add(Rect.MinMaxRect(envelope.xMin + margin, envelope.yMin + margin + Apron,
                    envelope.xMax - margin, envelope.yMax - margin));
            }
        }
    }

    [MenuItem("Tools/Engineering Building/Building Generator")]
    static void Open() { GetWindow<EngineeringFloor1Generator>("Building v4"); }
    void OnGUI()
    {
        SyncLabels(FindBuilding()!=null?FindBuilding().transform:null);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("Engineering Building | v4.1", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(status, MessageType.Info);
        buildingScale = EditorGUILayout.FloatField("Whole-building scale", buildingScale);
        storeyHeight = EditorGUILayout.FloatField("Floor-to-floor height (m)", storeyHeight);
        slabDepth = EditorGUILayout.FloatField("Slab thickness (m)", slabDepth);
        wallDepth = EditorGUILayout.FloatField("Wall thickness (m)", wallDepth);
        openingWidth = EditorGUILayout.FloatField("Door width (m)", openingWidth);
        topStairLanding = EditorGUILayout.IntSlider("Top stair landing", topStairLanding, 3, 8);
        EditorGUILayout.HelpBox("Floors 1 and 2 have room layouts. Above Floor 2, only stair cores and landings are generated. North lobby: L from 2 to 3, U from 3 onward.", MessageType.None);
        showLabels = EditorGUILayout.Toggle("Room numbers", showLabels);
        includeSite = EditorGUILayout.Toggle("Raised north ground + path", includeSite);
        addCamera = EditorGUILayout.Toggle("Add overview camera", addCamera);
        EditorGUILayout.HelpBox("Regeneration replaces the named generated roots in the active scene. " +
            "Rename a manually edited building before regenerating if you want to keep it. Ctrl+Z undoes regeneration.", MessageType.Warning);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            if (GUILayout.Button("Generate / Replace Complete Building", GUILayout.Height(32))) Generate();
            if (GUILayout.Button("Validate Layout")) ValidateOnly();
        }
        GUILayout.Space(12);
        EditorGUILayout.LabelField("Inspect generated objects", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Both floors")) Preset(true, true);
        if (GUILayout.Button("Floor 1 only")) Preset(true, false);
        if (GUILayout.Button("Floor 2 only")) Preset(false, true);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox("Render hides surfaces but keeps colliders. Active enables/disables the whole group. " +
            "Flights belong to their starting floor. Upper stair cores can be hidden separately. Room labels show on the highest visible full floor.", MessageType.None);
        DrawGroup("Floor 1"); DrawGroup("Floor 2"); DrawGroup("Upper stair cores"); DrawGroup("Site");
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Despawn Floor 1")) Despawn("Floor 1");
        if (GUILayout.Button("Despawn Floor 2")) Despawn("Floor 2");
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndScrollView();
    }
    void ClampSettings()
    {
        topStairLanding = Mathf.Clamp(topStairLanding, 3, 8);
        buildingScale = Mathf.Clamp(buildingScale, .05f, 20f);
        storeyHeight = Mathf.Clamp(storeyHeight, 3f, 4.2f);
        slabDepth = Mathf.Clamp(slabDepth, .1f, .4f);
        wallDepth = Mathf.Clamp(wallDepth, .08f, .22f);
        openingWidth = Mathf.Clamp(openingWidth, .75f, 1.1f);
    }
    static float Snap(float v) { return Mathf.Round(v * 1000f) / 1000f; }
    static Vector2 P(float x, float y) { return new Vector2(Snap((x - 101f) / PixelsPerMetre), Snap((720f - y) / PixelsPerMetre)); }
    static Rect B(float x1, float y1, float x2, float y2)
    { Vector2 a = P(x1, y2), b = P(x2, y1); return Rect.MinMaxRect(a.x, a.y, b.x, b.y); }
    static bool In(Rect r, float x, float z) { return x > r.xMin - Epsilon && x < r.xMax + Epsilon && z > r.yMin - Epsilon && z < r.yMax + Epsilon; }
    static bool Overlap(Rect a, Rect b) { return Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin) > .002f && Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin) > .002f; }
    static void R(Plan p, string n, float x1, float y1, float x2, float y2, string category, string side)
    { p.Rooms.Add(new Room(n, B(x1, y1, x2, y2), category, side)); }
    static void D(Plan p, bool horizontal, float px, float py, float width, string name)
    { Vector2 point = P(px, py); p.ExtraDoors.Add(new Portal(horizontal, horizontal ? point.y : point.x, horizontal ? point.x : point.y, width, name)); }
    static List<Stair> StairData()
    {
        return new List<Stair> {
            new Stair("S1", B(233f, 445f, 295f, 563f), false),
            new Stair("S2", B(694f, 484f, 749f, 563f), false),
            new Stair("S3", B(1053f, 298f, 1084f, 392f), false),
            new Stair("S4", B(1143f, 470f, 1198f, 563f), false),
            new Stair("S5 Lobby", B(1440f, 464f, 1498f, 539f), true),
        };
    }
    static List<Stair> StairsAtFloor(List<Stair> source, int floor)
    {
        var result = source.Where(s => !s.IsL || floor >= 2).ToList();
        if (floor >= 3)
        {
            Stair lobby = source.First(s => s.IsL);
            Rect e = lobby.Envelope;
            // Front apron is at the L stair's upper exit; upper flights return clockwise.
            Stair upper = new Stair(lobby.Name, Rect.MinMaxRect(e.xMin, e.yMin, e.xMax, e.yMax + 1.22f), false);
            upper.Reverse = true;
            upper.Holes.Clear();
            upper.Holes.Add(Rect.MinMaxRect(upper.Envelope.xMin + .12f, upper.Envelope.yMin + .12f,
                upper.Envelope.xMax - .12f, upper.Envelope.yMax - .12f - upper.Apron));
            result[result.Count - 1] = upper;
        }
        return result;
    }
    static Plan UpperCorePlan(List<Stair> cores)
    {
        var p = new Plan();
        foreach (Stair core in cores)
        {
            p.Footprints.AddRange(core.Reserved);
            // Exterior floor-level landing outside BOTH doors. No invented Floor 3 room plan.
            Rect e = core.Envelope;
            p.Footprints.Add(Rect.MinMaxRect(e.xMin, core.Reverse ? e.yMax : e.yMin - 1.25f,
                e.xMax, core.Reverse ? e.yMax + 1.25f : e.yMin));
        }
        return p;
    }
    void AddStairDoors(Grid g, List<Stair> cores)
    {
        foreach (Stair s in cores)
        {
            Rect e = s.Envelope;
            if (s.IsL)
            {
                Rect apron = s.Reserved[2];
                g.Doors.Add(new Portal(true, apron.yMin, apron.center.x, openingWidth, s.Name + " entry A"));
                g.Doors.Add(new Portal(false, apron.xMax, apron.center.y, openingWidth, s.Name + " entry B"));
            }
            else
            {
                float width = Mathf.Min(openingWidth, s.Lane - .08f);
                float front = s.Reverse ? e.yMax : e.yMin;
                g.Doors.Add(new Portal(true, front, e.xMin + .12f + s.Lane / 2, width, s.Name + " entry A"));
                g.Doors.Add(new Portal(true, front, e.xMax - .12f - s.Lane / 2, width, s.Name + " entry B"));
            }
        }
    }
    static Plan Floor1Data()
    {
        Plan p = new Plan();
        p.Footprints.Add(B(101f, 250f, 884f, 720f));
        p.Footprints.Add(B(884f, 324.182f, 955.559f, 526.357f));
        p.Footprints.Add(B(884f, 616.5f, 955.559f, 708f));
        p.Footprints.Add(B(1053f, 229f, 1621f, 720f));
        p.Footprints.Add(B(884f, 563f, 1053f, 606f));
        R(p, "141", 101f, 250f, 186.953f, 305.636f, "Imagination", "R");
        R(p, "139", 101f, 305.636f, 186.953f, 403f, "Realization", "R");
        R(p, "137", 101f, 403f, 186.953f, 466.35f, "Office", "R");
        R(p, "133", 101f, 466.35f, 186.953f, 563f, "Lecture", "R");
        R(p, "131", 101f, 563f, 186.953f, 606f, "Office", "R");
        R(p, "129", 101f, 606f, 186.953f, 669f, "Imagination", "R");
        R(p, "127A", 101f, 669f, 150.116f, 720f, "Office", "T");
        R(p, "127", 150.116f, 669f, 186.953f, 720f, "Office", "R");
        R(p, "142 Lower Lobby", 186.953f, 267f, 274.333f, 403f, "Public", "B");
        R(p, "143", 274.333f, 250f, 325.93f, 327.273f, "Office", "L");
        R(p, "145", 274.333f, 327.273f, 325.93f, 403f, "Office", "B");
        R(p, "147", 325.93f, 250f, 528.523f, 403f, "Realization", "B");
        R(p, "151", 528.523f, 250f, 673.895f, 403f, "Lecture", "B");
        R(p, "153A", 673.895f, 250f, 739.833f, 335f, "Department", "B");
        R(p, "153", 673.895f, 335f, 739.833f, 403f, "Department", "B");
        R(p, "155", 739.833f, 250f, 884f, 403f, "Department", "B");
        R(p, "167", 884f, 324.182f, 955.559f, 526.357f, "Department", "L");
        R(p, "159", 793.489f, 403f, 884f, 455.675f, "Office", "L");
        R(p, "161", 793.489f, 455.675f, 884f, 499.9f, "Office", "L");
        R(p, "163", 793.489f, 499.9f, 884f, 538.571f, "Office", "L");
        R(p, "165", 793.489f, 538.571f, 884f, 577.333f, "Office", "L");
        R(p, "130 Women", 295f, 445f, 356.86f, 504.475f, "Public", "T");
        R(p, "124 Men", 295f, 504.475f, 356.86f, 563f, "Public", "B");
        R(p, "122", 369.233f, 445f, 468.209f, 563f, "Operations", "B");
        R(p, "120", 468.209f, 445f, 489.86f, 563f, "Operations", "B");
        R(p, "118", 489.86f, 445f, 599.663f, 563f, "Operations", "B");
        R(p, "132", 599.663f, 445f, 694f, 538.571f, "Operations", "T");
        R(p, "116", 599.663f, 538.571f, 694f, 563f, "Operations", "B");
        R(p, "134", 719.972f, 445f, 749f, 484.65f, "Operations", "T");
        R(p, "123", 233f, 606f, 274.333f, 636f, "Operations", "T");
        R(p, "125", 233f, 636f, 274.333f, 720f, "Office", "R");
        R(p, "119", 274.333f, 606f, 471.302f, 720f, "Realization", "T");
        R(p, "117", 471.302f, 606f, 588.837f, 720f, "Realization", "T");
        R(p, "115", 588.837f, 606f, 676.988f, 661.5f, "Office", "T");
        R(p, "117A", 588.837f, 661.5f, 676.988f, 720f, "Realization", "L");
        R(p, "111", 676.988f, 606f, 884f, 720f, "Realization", "T");
        R(p, "169A", 884f, 663f, 923.586f, 708f, "Department", "L");
        R(p, "169B", 884f, 616.5f, 923.586f, 663f, "Department", "L");
        R(p, "169", 923.586f, 616.5f, 955.559f, 708f, "Department", "L");
        R(p, "101", 1082.48f, 229f, 1244.17f, 410.636f, "Realization", "B");
        R(p, "101A", 1053f, 262.364f, 1082.48f, 324.182f, "Operations", "R");
        R(p, "105", 1244.17f, 229f, 1483.5f, 410.636f, "Operations", "B");
        R(p, "105A", 1483.5f, 229f, 1621f, 364.364f, "Operations", "L");
        R(p, "105B", 1498f, 364.364f, 1621f, 525f, "Operations", "T");
        R(p, "105", 1244.17f, 410.636f, 1440f, 486.175f, "Operations", "T");
        R(p, "107A", 1440f, 410.636f, 1464.17f, 486.175f, "Operations", "L");
        R(p, "105C", 1464.17f, 410.636f, 1498f, 525f, "Operations", "B");
        R(p, "101B", 1132.14f, 410.636f, 1198f, 451.1f, "Operations", "B");
        R(p, "101C", 1198f, 410.636f, 1244.17f, 549.429f, "Operations", "B");
        R(p, "101D", 1132.14f, 451.1f, 1198f, 486.175f, "Operations", "L");
        R(p, "Elevator 1", 1091.79f, 451.1f, 1132.14f, 486.175f, "Operations", "R");
        R(p, "Elevator 2", 1091.79f, 486.175f, 1132.14f, 519.571f, "Operations", "R");
        R(p, "Elevator 3", 1091.79f, 519.571f, 1132.14f, 549.429f, "Operations", "R");
        R(p, "102A", 1053f, 591.667f, 1110.41f, 720f, "Imagination", "R");
        R(p, "102", 1110.41f, 591.667f, 1244.17f, 720f, "Realization", "T");
        R(p, "104", 1244.17f, 486.175f, 1326.96f, 519.571f, "Department", "B");
        R(p, "104H", 1326.96f, 486.175f, 1368.36f, 546.714f, "Operations", "B");
        R(p, "103H", 1368.36f, 486.175f, 1408.16f, 546.714f, "Department", "B");
        R(p, "103A", 1408.16f, 525f, 1469f, 556.214f, "Department", "B");
        R(p, "103L", 1518.5f, 525f, 1621f, 618f, "Department", "L");
        R(p, "103V", 1326.96f, 601.222f, 1384.28f, 643.5f, "Department", "T");
        R(p, "103T", 1384.28f, 601.222f, 1440f, 643.5f, "Department", "T");
        R(p, "103U", 1440f, 601.222f, 1498f, 643.5f, "Department", "T");
        R(p, "103R", 1244.17f, 661.5f, 1304.67f, 720f, "Department", "T");
        R(p, "103Q", 1304.67f, 661.5f, 1355.62f, 720f, "Department", "T");
        R(p, "103P", 1355.62f, 661.5f, 1408.16f, 720f, "Department", "T");
        R(p, "103N", 1427.26f, 661.5f, 1518.5f, 720f, "Department", "T");
        R(p, "103M", 1518.5f, 618f, 1621f, 720f, "Department", "L");
        R(p, "103B", 1326.96f, 572.556f, 1355.35f, 601.222f, "Department", "T");
        R(p, "103C", 1355.35f, 572.556f, 1383.75f, 601.222f, "Department", "T");
        R(p, "103D", 1383.75f, 572.556f, 1412.14f, 601.222f, "Department", "T");
        R(p, "103E", 1412.14f, 572.556f, 1440.54f, 601.222f, "Department", "T");
        R(p, "103F", 1440.54f, 572.556f, 1469.27f, 601.222f, "Department", "T");
        R(p, "103G", 1469.27f, 572.556f, 1498f, 601.222f, "Department", "T");
        R(p, "103S", 1384.28f, 643.5f, 1427.26f, 661.5f, "Department", "B");
        p.Outdoor.Add(B(884, 563, 1053, 606));
        D(p, false, 1053, 585, 1.6f, "Breezeway entrance");
        D(p, false, 101, 580, 1.5f, "Lower west entrance");
        D(p, true, 192, 250, 1.3f, "Lower lobby entrance");
        D(p, false, 1244.171f, 460.25f, 1.1f, "105 lab entrance");
        D(p, false, 1132.138f, 429.727f, .95f, "101B access");
        D(p, true, 1165.647f, 451.1f, .95f, "101D access");
        return p;
    }
    static Plan Floor2Data()
    {
        Plan p = new Plan();
        p.Footprints.Add(B(101f, 250f, 884f, 720f));
        p.Footprints.Add(B(884f, 321f, 1053f, 720f));
        p.Footprints.Add(B(1053f, 229f, 1621f, 720f));
        R(p, "269", 101f, 250f, 234f, 403f, "Lecture", "B");
        R(p, "271", 234f, 250f, 320f, 403f, "Lecture", "B");
        R(p, "273", 320f, 250f, 406f, 403f, "Lecture", "B");
        R(p, "275", 406f, 250f, 493f, 403f, "Lecture", "B");
        R(p, "277", 493f, 250f, 578f, 403f, "Lecture", "B");
        R(p, "279", 578f, 250f, 664f, 403f, "Lecture", "B");
        R(p, "281", 664f, 250f, 751f, 403f, "Lecture", "B");
        R(p, "283", 751f, 250f, 884f, 403f, "Lecture", "B");
        R(p, "267", 101f, 403f, 189f, 446f, "Office", "R");
        R(p, "263", 101f, 446f, 189f, 563f, "Lecture", "R");
        R(p, "261", 101f, 563f, 189f, 606f, "Office", "R");
        R(p, "259", 101f, 606f, 205f, 720f, "Realization", "T");
        R(p, "257", 205f, 606f, 277f, 720f, "Department", "T");
        R(p, "255", 277f, 606f, 357f, 720f, "Lecture", "T");
        R(p, "253", 357f, 606f, 441f, 720f, "Imagination", "T");
        R(p, "251", 441f, 606f, 532f, 720f, "Lecture", "T");
        R(p, "247 (formerly 249)", 532f, 606f, 694f, 720f, "Lecture", "T");
        R(p, "245", 694f, 606f, 773f, 720f, "Lecture", "T");
        R(p, "243", 773f, 606f, 884f, 720f, "Lecture", "T");
        R(p, "241", 884f, 606f, 972f, 720f, "Lecture", "T");
        R(p, "239", 972f, 606f, 1055f, 720f, "Lecture", "T");
        R(p, "256 Women", 295f, 445f, 355f, 484f, "Public", "T");
        R(p, "250 Men", 295f, 484f, 355f, 563f, "Public", "B");
        R(p, "258", 367f, 445f, 455f, 530f, "Operations", "T");
        R(p, "248", 367f, 530f, 455f, 563f, "Office", "B");
        R(p, "260B", 455f, 445f, 493f, 506f, "Office", "R");
        R(p, "260", 493f, 445f, 535f, 506f, "Public", "T");
        R(p, "260A", 535f, 445f, 574f, 506f, "Office", "L");
        R(p, "246B", 455f, 506f, 493f, 563f, "Office", "R");
        R(p, "246", 493f, 506f, 535f, 563f, "Public", "B");
        R(p, "246A", 535f, 506f, 574f, 563f, "Office", "L");
        R(p, "244D", 574f, 445f, 619f, 498f, "Department", "T");
        R(p, "262", 619f, 445f, 650f, 488f, "Department", "T");
        R(p, "262A", 650f, 445f, 694f, 488f, "Department", "T");
        R(p, "264", 721f, 445f, 749f, 484f, "Department", "T");
        R(p, "244", 574f, 498f, 650f, 563f, "Department", "B");
        R(p, "244B", 650f, 488f, 694f, 529f, "Department", "L");
        R(p, "244A", 650f, 529f, 694f, 563f, "Department", "B");
        R(p, "285", 794f, 403f, 884f, 563f, "Lecture", "L");
        R(p, "205", 884f, 321f, 1053f, 433f, "Imagination", "L");
        R(p, "201", 884f, 433f, 968f, 563f, "Department", "B");
        R(p, "203", 968f, 433f, 1053f, 563f, "Department", "B");
        R(p, "207A", 1053f, 229f, 1084f, 260f, "Operations", "R");
        R(p, "221", 1546f, 392f, 1621f, 435f, "Operations", "L");
        R(p, "202", 1095f, 435f, 1169f, 470f, "Operations", "T");
        R(p, "204 Women", 1169f, 435f, 1232f, 497f, "Public", "T");
        R(p, "206", 1232f, 435f, 1269f, 497f, "Operations", "T");
        R(p, "208", 1269f, 435f, 1318f, 497f, "Office", "T");
        R(p, "210", 1318f, 435f, 1404f, 470f, "Operations", "T");
        R(p, "212", 1404f, 435f, 1440f, 470f, "Operations", "T");
        R(p, "HVAC", 1318f, 470f, 1440f, 497f, "Operations", "B");
        R(p, "214", 1464f, 478f, 1498f, 512f, "Operations", "T");
        R(p, "218", 1440f, 539f, 1498f, 563f, "Operations", "B");
        R(p, "226 Men", 1198f, 497f, 1308f, 563f, "Public", "B");
        R(p, "226A", 1308f, 497f, 1341f, 519f, "Public", "B");
        R(p, "222", 1341f, 497f, 1405f, 563f, "Office", "B");
        R(p, "220", 1405f, 497f, 1440f, 563f, "Department", "B");
        R(p, "Elevator 1", 1095f, 470f, 1125f, 500f, "Operations", "R");
        R(p, "Elevator 2", 1095f, 500f, 1125f, 532f, "Operations", "R");
        R(p, "Elevator 3", 1095f, 532f, 1125f, 563f, "Operations", "R");
        R(p, "216 Main Lobby", 1498f, 435f, 1621f, 563f, "Public", "R");
        R(p, "223", 1550f, 563f, 1621f, 606f, "Department", "L");
        R(p, "207", 1084f, 229f, 1169f, 392f, "Lecture", "B");
        R(p, "209", 1169f, 229f, 1240f, 392f, "Lecture", "B");
        R(p, "211", 1240f, 229f, 1341f, 392f, "Lecture", "B");
        R(p, "213", 1341f, 229f, 1428f, 392f, "Lecture", "B");
        R(p, "217", 1428f, 229f, 1521f, 392f, "Lecture", "B");
        R(p, "219", 1521f, 229f, 1621f, 392f, "Lecture", "B");
        R(p, "233I", 1075f, 606f, 1118f, 653f, "Office", "B");
        R(p, "233G", 1118f, 606f, 1161f, 653f, "Office", "B");
        R(p, "233E", 1161f, 606f, 1203f, 653f, "Office", "B");
        R(p, "S", 1203f, 606f, 1232f, 633f, "Operations", "B");
        R(p, "233J", 1055f, 668f, 1100f, 720f, "Office", "T");
        R(p, "233H", 1100f, 668f, 1142f, 720f, "Office", "T");
        R(p, "233F", 1142f, 668f, 1186f, 720f, "Office", "T");
        R(p, "233D", 1186f, 668f, 1229f, 720f, "Office", "T");
        R(p, "233C", 1229f, 668f, 1271f, 720f, "Office", "T");
        R(p, "233B", 1271f, 668f, 1318f, 720f, "Department", "T");
        R(p, "231B", 1318f, 668f, 1361f, 720f, "Department", "T");
        R(p, "231A", 1361f, 668f, 1404f, 720f, "Department", "T");
        R(p, "227C", 1404f, 668f, 1450f, 720f, "Department", "T");
        R(p, "227B", 1450f, 668f, 1521f, 720f, "Department", "T");
        R(p, "225C", 1521f, 668f, 1579f, 720f, "Department", "T");
        R(p, "225B", 1579f, 668f, 1621f, 720f, "Department", "T");
        R(p, "233A", 1271f, 606f, 1318f, 653f, "Department", "T");
        R(p, "231", 1318f, 606f, 1404f, 653f, "Department", "T");
        R(p, "227", 1404f, 606f, 1470f, 653f, "Department", "T");
        R(p, "227A", 1470f, 606f, 1521f, 653f, "Department", "T");
        R(p, "225", 1521f, 606f, 1579f, 653f, "Department", "T");
        R(p, "225A", 1579f, 606f, 1621f, 653f, "Department", "T");
        D(p, true, 1518, 435, 1.25f, "Lobby to 219 hallway");
        D(p, true, 1518, 563, 1.25f, "Lobby to 225 hallway");
        D(p, false, 1621, 464, 1.5f, "North ground entrance");
        return p;
    }
    static void UniqueAdd(List<float> values, float value) { values.Add(Snap(value)); }
    static float[] SortedCuts(List<float> v) { return v.Distinct().OrderBy(a => a).ToArray(); }
    Grid MakeGrid(Plan p, List<Stair> stairs, List<Stair> incoming)
    {
        for (int a = 0; a < p.Rooms.Count; a++)
            for (int b = a + 1; b < p.Rooms.Count; b++)
                if (Overlap(p.Rooms[a].Bounds, p.Rooms[b].Bounds))
                    throw new InvalidOperationException("Overlapping rooms: " + p.Rooms[a].Name + " and " + p.Rooms[b].Name);
        var xx = new List<float>(); var zz = new List<float>();
        var allRects = new List<Rect>(p.Footprints);
        allRects.AddRange(p.Rooms.Select(r => r.Bounds)); allRects.AddRange(p.Outdoor);
        foreach (Stair s in stairs) allRects.AddRange(s.Reserved);
        foreach (Stair s in incoming) allRects.AddRange(s.Holes);
        foreach (Rect r in allRects) { UniqueAdd(xx, r.xMin); UniqueAdd(xx, r.xMax); UniqueAdd(zz, r.yMin); UniqueAdd(zz, r.yMax); }
        Grid g = new Grid { X = SortedCuts(xx), Z = SortedCuts(zz) };
        int[] canonical = p.Rooms.Select(r => p.Rooms.FindIndex(other => other.Name == r.Name) + 1).ToArray();
        int nx = g.X.Length - 1, nz = g.Z.Length - 1;
        g.Owner = new int[nx, nz]; g.Aperture = new bool[nx, nz];
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++)
        {
            float x = (g.X[i] + g.X[i + 1]) / 2, z = (g.Z[j] + g.Z[j + 1]) / 2;
            int owner = p.Footprints.Any(r => In(r, x, z)) ? 0 : -1;
            if (owner != -1)
            {
                if (p.Outdoor.Any(r => In(r, x, z))) owner = -3;
                for (int k = 0; k < p.Rooms.Count; k++) if (In(p.Rooms[k].Bounds, x, z)) { owner = canonical[k]; break; }
                // The stair reserves take precedence over small adjoining utility subdivisions.
                for (int k = 0; k < stairs.Count; k++)
                    if (stairs[k].Reserved.Any(r => In(r, x, z))) { owner = -10 - k; break; }
                g.Aperture[i, j] = incoming.Any(s => s.Holes.Any(r => In(r, x, z)));
            }
            g.Owner[i, j] = owner;
        }
        BuildWallEdges(g);
        g.Doors.AddRange(p.ExtraDoors);
        AddStairDoors(g, stairs);
        for (int id = 1; id <= p.Rooms.Count; id++)
        {
            if (canonical[id - 1] != id) continue;
            Room room = p.Rooms[id - 1];
            // Prefer the indicated side, then a corridor rather than an adjacent suite.
            Wall best = null; float scoreBest = float.NegativeInfinity;
            foreach (Wall w in g.Walls)
            {
                if (w.A != id && w.B != id) continue;
                int neighbor = w.A == id ? w.B : w.A;
                if (neighbor <= -10 || w.End - w.Start < openingWidth + wallDepth * 2) continue;
                string side = w.Horizontal ? (w.A == id ? "T" : "B") : (w.A == id ? "R" : "L");
                float score = (side == room.DoorSide ? 100 : 0) + (neighbor == 0 || neighbor == -3 ? 40 : 0) - (neighbor == -1 ? 100 : 0);
                score += Mathf.Min(w.End - w.Start, 8f);
                if (score > scoreBest) { scoreBest = score; best = w; }
            }
            if (best == null) throw new InvalidOperationException("No usable doorway for " + room.Name);
            g.Doors.Add(new Portal(best.Horizontal, best.Fixed, (best.Start + best.End) / 2, openingWidth, room.Name));
        }
        ValidateStairApertures(g, incoming);
        ValidateStairDoors(g, stairs);
        return g;
    }
    static int Cell(Grid g, int x, int z)
    { return x < 0 || z < 0 || x >= g.Owner.GetLength(0) || z >= g.Owner.GetLength(1) ? -1 : g.Owner[x,z]; }
    static bool NeedWall(int a, int b)
    {
        if (a == b) return false;
        if ((a == -3 || b == -3) && a >= -3 && b >= -3 && a <= 0 && b <= 0) return false;
        return true;
    }
    static void BuildWallEdges(Grid g)
    {
        int nx = g.Owner.GetLength(0), nz = g.Owner.GetLength(1);
        // Each grid boundary is visited ONCE. Shared walls cannot be doubled.
        for (int j = 0; j <= nz; j++)
        {
            Wall run = null;
            for (int i = 0; i < nx; i++)
            {
                int a = Cell(g, i, j - 1), b = Cell(g, i, j);
                if (!NeedWall(a,b)) { run = null; continue; }
                if (run != null && run.A == a && run.B == b) run.End = g.X[i+1];
                else { run = new Wall(true, g.Z[j], g.X[i], g.X[i+1], a,b); g.Walls.Add(run); }
            }
        }
        for (int i = 0; i <= nx; i++)
        {
            Wall run = null;
            for (int j = 0; j < nz; j++)
            {
                int a = Cell(g, i - 1, j), b = Cell(g, i, j);
                if (!NeedWall(a,b)) { run = null; continue; }
                if (run != null && run.A == a && run.B == b) run.End = g.Z[j+1];
                else { run = new Wall(false, g.X[i], g.Z[j], g.Z[j+1], a,b); g.Walls.Add(run); }
            }
        }
    }
    static void ValidateStairApertures(Grid g, List<Stair> incoming)
    {
        for (int i=0;i<g.Owner.GetLength(0);i++) for(int j=0;j<g.Owner.GetLength(1);j++)
        {
            float x=(g.X[i]+g.X[i+1])/2, z=(g.Z[j]+g.Z[j+1])/2;
            if (incoming.Any(s=>s.Holes.Any(r=>In(r,x,z))) && (!g.Aperture[i,j] || g.Owner[i,j] > -10))
                throw new InvalidOperationException("A stair opening is blocked or lies outside its enclosed core.");
        }
    }
    static void ValidateStairDoors(Grid g, List<Stair> cores)
    {
        for(int k=0;k<cores.Count;k++)
        {
            int id=-10-k; var distinct=new HashSet<Portal>();
            foreach(Wall w in g.Walls.Where(w=>w.A==id||w.B==id))
                foreach(Portal d in g.Doors)
                    if(w.Horizontal==d.Horizontal && Mathf.Abs(w.Fixed-d.Fixed)<.002f &&
                        Mathf.Min(w.End,d.End)-Mathf.Max(w.Start,d.Start)>.01f) distinct.Add(d);
            if(distinct.Count!=2) throw new InvalidOperationException(cores[k].Name+" needs exactly two floor-level doorways; found "+distinct.Count);
        }
    }
    void ValidateOnly()
    {
        try
        {
            ClampSettings(); var source=StairData(); var incoming=new List<Stair>();
            for(int f=1;f<=topStairLanding;f++)
            {
                var cores=StairsAtFloor(source,f);
                MakeGrid(f==1?Floor1Data():f==2?Floor2Data():UpperCorePlan(cores),cores,incoming);
                incoming=cores;
            }
            status="Layout valid: shared walls once; exactly two doors per enclosed core; lobby L begins on Floor 2. Higher floors are stair cores only.";
            Debug.Log(status);
        }
        catch(Exception e) { status=e.Message; Debug.LogException(e); }
    }
    void Generate()
    {
        GameObject pending = null;
        int undoGroup=-1;
        try
        {
            ClampSettings(); var source=StairData(); var incoming=new List<Stair>();
            var plans=new List<Plan>(); var grids=new List<Grid>(); var levels=new List<List<Stair>>();
            for(int f=1;f<=topStairLanding;f++)
            {
                var cores=StairsAtFloor(source,f);
                Plan plan=f==1?Floor1Data():f==2?Floor2Data():UpperCorePlan(cores);
                plans.Add(plan); levels.Add(cores); grids.Add(MakeGrid(plan,cores,incoming));
                incoming=cores;
            }
            EnsureMaterials();
            Scene scene=SceneManager.GetActiveScene();
            GameObject old=FindBuilding();
            Vector3 position=old!=null?old.transform.position:Vector3.zero;
            Quaternion rotation=old!=null?old.transform.rotation:Quaternion.identity;
            Undo.IncrementCurrentGroup(); undoGroup=Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rebuild engineering building v4");
            pending=new GameObject(RootName+" (building)");
            pending.transform.position=position; pending.transform.rotation=rotation;
            pending.transform.localScale=Vector3.one*buildingScale;
            Transform upperGroup=Group("Upper stair cores",pending.transform);
            for(int i=0;i<plans.Count;i++)
            {
                Transform level=BuildLevel(i<2?pending.transform:upperGroup,"Floor "+(i+1),i*storeyHeight,plans[i],grids[i]);
                if(i+1<plans.Count)
                {
                    Transform stairGroup=Group("Stair flights",level);
                    foreach(Stair core in levels[i]) BuildStair(stairGroup,core);
                }
            }
            // Upper cores are visible to demonstrate the Floor 3 U return. Hide independently.
            SyncLabels(pending.transform);
            if(includeSite) BuildSite(Group("Site",pending.transform));
            if(addCamera) BuildCamera(pending.transform);
            // Only replace an earlier scene root after the new geometry has been built successfully.
            foreach(GameObject candidate in scene.GetRootGameObjects())
                if(candidate!=pending && (candidate.name==RootName || candidate.name=="Engineering Building - Floor 1"))
                    Undo.DestroyObjectImmediate(candidate);
            pending.name=RootName;
            Undo.RegisterCreatedObjectUndo(pending,"Rebuild engineering building v4");
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject=pending; SceneView.lastActiveSceneView?.FrameSelected();
            status="v4.1 generated. Inspect each floor separately using the buttons below. Save the scene with Ctrl+S.";
            pending=null;
        }
        catch(Exception e)
        {
            if(pending!=null) DestroyImmediate(pending);
            if(undoGroup>=0) Undo.RevertAllDownToGroup(undoGroup);
            status=e.Message; Debug.LogException(e);
        }
    }
    Transform BuildLevel(Transform root,string name,float y,Plan plan,Grid grid)
    {
        Transform level=Group(name,root); level.localPosition=Vector3.up*y;
        Transform floors=Group("Slab and room surfaces",level), walls=Group("Shared walls",level), labels=Group("Room labels",level);
        int nx=grid.Owner.GetLength(0), nz=grid.Owner.GetLength(1);
        var used=new bool[nx,nz];
        var biggest=new Dictionary<int,Rect>();
        for(int j=0;j<nz;j++) for(int i=0;i<nx;i++)
        {
            int id=grid.Owner[i,j]; if(used[i,j] || id==-1 || grid.Aperture[i,j])continue;
            int ix=i+1;while(ix<nx && !used[ix,j] && !grid.Aperture[ix,j] && grid.Owner[ix,j]==id)ix++;
            int jz=j+1;
            while(jz<nz)
            {
                bool match=true;for(int a=i;a<ix;a++)if(used[a,jz]||grid.Aperture[a,jz]||grid.Owner[a,jz]!=id){match=false;break;}
                if(!match)break;jz++;
            }
            for(int a=i;a<ix;a++)for(int b=j;b<jz;b++)used[a,b]=true;
            Rect r=Rect.MinMaxRect(grid.X[i],grid.Z[j],grid.X[ix],grid.Z[jz]);
            string label=id>0?plan.Rooms[id-1].Name:(id==-3?"Breezeway":"Hall");
            string cat=id>0?plan.Rooms[id-1].Category:(id==-3?"Path":"Corridor");
            Box(floors,label+" slab",new Vector3(r.center.x,-slabDepth/2,r.center.y),new Vector3(r.width,slabDepth,r.height),cat);
            if(id>0 && (!biggest.ContainsKey(id)||r.width*r.height>biggest[id].width*biggest[id].height))biggest[id]=r;
        }
        float height=storeyHeight-slabDepth;
        foreach(Wall w in grid.Walls)
        {
            var cuts=new List<float>{w.Start,w.End};
            var openings=grid.Doors.Where(d=>d.Horizontal==w.Horizontal && Mathf.Abs(d.Fixed-w.Fixed)<.002f && d.End>w.Start && d.Start<w.End).ToList();
            foreach(var d in openings){cuts.Add(Mathf.Clamp(d.Start,w.Start,w.End));cuts.Add(Mathf.Clamp(d.End,w.Start,w.End));}
            cuts=cuts.Distinct().OrderBy(a=>a).ToList();
            for(int k=0;k<cuts.Count-1;k++)
            {
                float a=cuts[k],b=cuts[k+1],mid=(a+b)/2;if(b-a<.002f)continue;
                bool door=openings.Any(d=>mid>d.Start-Epsilon && mid<d.End+Epsilon);
                float bottom=door?2.15f:0f;
                Vector3 pos=w.Horizontal?new Vector3(mid,(height+bottom)/2,w.Fixed):new Vector3(w.Fixed,(height+bottom)/2,mid);
                Vector3 size=w.Horizontal?new Vector3(b-a,height-bottom,wallDepth):new Vector3(wallDepth,height-bottom,b-a);
                Box(walls,door?"Door lintel":"Wall",pos,size,"Wall");
            }
        }
        if(showLabels)foreach(var pair in biggest)Label(labels,plan.Rooms[pair.Key-1].Name,pair.Value);
        return level;
    }
    void BuildStair(Transform parent,Stair s)
    {
        Transform t=Group(s.Name+(s.IsL?" - L south then right":" - U right return"),parent);
        int n=Mathf.CeilToInt(storeyHeight/0.4f);
        float half=storeyHeight/2;
        Rect e=s.Envelope;
        if(s.IsL)
        {
            float w=s.Lane;
            Vector3 start=new Vector3(e.xMax,0,e.yMin+.12f+w/2);
            Flight(t,"Lower south-facing flight",start,Vector3.left,s.LowerRun,w,half,n);
            Box(t,"Half-height landing",new Vector3(e.xMin+.12f+w/2,half-.1f,e.yMin+.12f+w/2),new Vector3(w,.2f,w),"Stair");
            Flight(t,"Upper flight after right turn",new Vector3(e.xMin+.12f+w/2,half,e.yMin+.12f+w),Vector3.forward,s.UpperRun,w,half,n);
            // Landing has guards only on its two closed sides; both flight interfaces stay open.
            Beam(t,new Vector3(e.xMin+.14f,half+1,e.yMin+.12f),new Vector3(e.xMin+.14f,half+1,e.yMin+.12f+w),.055f,"Rail");
            Beam(t,new Vector3(e.xMin+.12f,half+1,e.yMin+.14f),new Vector3(e.xMin+.12f+w,half+1,e.yMin+.14f),.055f,"Rail");
            Marker(t,"UP begins SOUTH (-X), right turn toward +Z",start);
        }
        else
        {
            // A 180-degree rotation preserves the RIGHT-hand return for the north upper cores.
            Transform flights=Group("Two flights and half landing",t);
            if(s.Reverse)
            {
                flights.localPosition=new Vector3(e.xMin+e.xMax,0,e.yMin+e.yMax);
                flights.localRotation=Quaternion.Euler(0,180,0);
            }
            float firstX=e.xMin+.12f+s.Lane/2;
            float secondX=e.xMax-.12f-s.Lane/2;
            float startZ=e.yMin+.12f+s.Apron;
            Flight(flights,"Lower flight",new Vector3(firstX,0,startZ),Vector3.forward,s.Run,s.Lane,half,n);
            float landingStart=startZ+s.Run;
            Box(flights,"Full-width half-height landing",new Vector3(e.center.x,half-.1f,landingStart+s.Landing/2),
                new Vector3(e.width-.24f,.2f,s.Landing),"Stair");
            Flight(flights,"Upper return flight",new Vector3(secondX,half,landingStart),Vector3.back,s.Run,s.Lane,half,n);
            Beam(flights,new Vector3(e.xMin+.15f,half+1,landingStart+s.Landing-.03f),new Vector3(e.xMax-.15f,half+1,landingStart+s.Landing-.03f),.055f,"Rail");
            Marker(flights,"Floor-level entry",new Vector3(firstX,0,startZ-.3f));
            Marker(flights,"Next-floor exit",new Vector3(secondX,storeyHeight,startZ-.3f));
        }
    }
    void Flight(Transform t,string name,Vector3 start,Vector3 direction,float length,float width,float climb,int steps)
    {
        Transform flight=Group(name,t);float run=length/steps,rise=climb/steps;
        Vector3 right=Vector3.Cross(Vector3.up,direction);
        for(int i=0;i<steps;i++)
        {
            float h=rise*(i+1);Vector3 pos=start+direction*((i+.5f)*run)+Vector3.up*(h/2);
            Vector3 size=Mathf.Abs(direction.x)>.5f?new Vector3(run,h,width):new Vector3(width,h,run);
            Box(flight,"Tread "+(i+1),pos,size,"Stair");
            Vector3 edge=start+direction*(i*run+.022f)+Vector3.up*(h+.003f);
            Box(flight,"Yellow nosing",edge,Mathf.Abs(direction.x)>.5f?new Vector3(.04f,.006f,width-.06f):new Vector3(width-.06f,.006f,.04f),"Nosing",false);
            if(i%3==0||i==steps-1)foreach(int side in new[]{-1,1})
            {
                Vector3 foot=start+direction*((i+.5f)*run)+Vector3.up*h+right*(side*(width/2-.045f));
                Beam(flight,foot,foot+Vector3.up, .045f,"Rail");
            }
        }
        foreach(int side in new[]{-1,1})
        {
            Vector3 offset=right*(side*(width/2-.045f));
            Beam(flight,start+offset+Vector3.up*(1+rise),start+direction*length+offset+Vector3.up*(climb+1),.055f,"Rail");
        }
    }
    void Beam(Transform parent,Vector3 a,Vector3 b,float thickness,string material)
    {
        Vector3 d=b-a;if(d.magnitude<.001f)return;
        GameObject beam=Box(parent,"Handrail",(a+b)/2,new Vector3(thickness,d.magnitude,thickness),material);
        beam.transform.localRotation=Quaternion.FromToRotation(Vector3.up,d.normalized);
    }
    void BuildSite(Transform parent)
    {
        // World north is +X, and the raised surface is level with the second-floor walking surface.
        float h=storeyHeight;
        Box(parent,"Raised north ground",new Vector3(106,h/2-.15f,16),new Vector3(12,h+.3f,40),"Terrain");
        Box(parent,"North entrance path",new Vector3(102.6f,h-.08f,16),new Vector3(5.2f,.16f,9),"Path");
        Box(parent,"North path",new Vector3(108,h-.08f,16),new Vector3(3,.16f,40),"Path");
        Box(parent,"Engineering patio",new Vector3(102.6f,h-.08f,4),new Vector3(5.2f,.16f,8),"Patio");
    }
    void BuildCamera(Transform root)
    {
        var c=Group("Overview camera",root);c.localPosition=new Vector3(53,78,16);c.localRotation=Quaternion.Euler(90,0,0);
        Camera cam=c.gameObject.AddComponent<Camera>();cam.orthographic=true;cam.orthographicSize=37f*buildingScale;cam.farClipPlane=300f*buildingScale;cam.depth=-10;
    }
    void Label(Transform parent,string text,Rect area)
    {
        Transform t=Group(text,parent);t.localPosition=new Vector3(area.center.x,.015f,area.center.y);t.localRotation=Quaternion.Euler(90,0,0);
        TextMesh label=t.gameObject.AddComponent<TextMesh>();label.text=text;label.anchor=TextAnchor.MiddleCenter;label.alignment=TextAlignment.Center;
        label.fontSize=48;label.characterSize=Mathf.Min(.045f,area.width/Mathf.Max(1,text.Length)*.18f);label.color=new Color(.07f,.07f,.07f);
        Font font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");if(font!=null){label.font=font;t.GetComponent<MeshRenderer>().sharedMaterial=font.material;}
    }
    static Transform Group(string name,Transform parent)
    { var go=new GameObject(name);go.transform.SetParent(parent,false);return go.transform; }
    static void Marker(Transform parent,string name,Vector3 position) { Group(name,parent).localPosition=position; }
    GameObject Box(Transform parent,string name,Vector3 pos,Vector3 size,string material,bool collider=true)
    {
        GameObject go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent,false);
        go.transform.localPosition=pos;go.transform.localScale=size;go.GetComponent<MeshRenderer>().sharedMaterial=materials[material];
        if(!collider)DestroyImmediate(go.GetComponent<Collider>());return go;
    }
    void EnsureMaterials()
    {
        if(!AssetDatabase.IsValidFolder("Assets/EngineeringBuildingGenerated"))AssetDatabase.CreateFolder("Assets","EngineeringBuildingGenerated");
        if(!AssetDatabase.IsValidFolder(MaterialFolder))AssetDatabase.CreateFolder("Assets/EngineeringBuildingGenerated","V4Materials");
        materials.Clear();
        MaterialFor("Lecture",new Color(.64f,.4f,.68f));MaterialFor("Imagination",new Color(1,.51f,.04f));
        MaterialFor("Realization",new Color(.93f,.89f,.1f));MaterialFor("Department",new Color(.5f,.85f,.34f));
        MaterialFor("Office",new Color(.35f,.83f,.84f));MaterialFor("Public",new Color(.95f,.57f,.6f));
        MaterialFor("Operations",new Color(.75f,.72f,.72f));MaterialFor("Corridor",new Color(.8f,.81f,.8f));
        MaterialFor("Wall",new Color(.85f,.86f,.86f));MaterialFor("Stair",new Color(.2f,.22f,.24f));
        MaterialFor("Nosing",new Color(.95f,.8f,.1f));MaterialFor("Rail",new Color(.65f,.06f,.045f));
        MaterialFor("Terrain",new Color(.24f,.4f,.21f));MaterialFor("Path",new Color(.47f,.58f,.6f));MaterialFor("Patio",new Color(.68f,.55f,.36f));
        AssetDatabase.SaveAssets();
    }
    void MaterialFor(string name,Color color)
    {
        string path=MaterialFolder+"/"+name+".mat";Material m=AssetDatabase.LoadAssetAtPath<Material>(path);
        if(m==null)
        {
            Shader shader=Shader.Find("Universal Render Pipeline/Lit")??Shader.Find("Standard");
            if(shader==null)throw new InvalidOperationException("A URP/Lit or Standard shader is required.");
            m=new Material(shader){name=name};m.color=color;if(m.HasProperty("_BaseColor"))m.SetColor("_BaseColor",color);
            AssetDatabase.CreateAsset(m,path);
        }
        materials[name]=m;
    }
    static GameObject FindBuilding()
    { return SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g=>g.name==RootName); }
    static Transform FindGroup(string name)
    { var b=FindBuilding();return b!=null?b.transform.Find(name):null; }
    void DrawGroup(string name)
    {
        Transform g=FindGroup(name);
        using(new EditorGUI.DisabledScope(g==null))
        {
            EditorGUILayout.BeginHorizontal();EditorGUILayout.LabelField(name,GUILayout.Width(110));
            bool active=g!=null&&g.gameObject.activeSelf;
            bool nextActive=EditorGUILayout.ToggleLeft("Active",active,GUILayout.Width(65));
            bool rendered=g!=null&&g.GetComponentsInChildren<Renderer>(true).Any(r=>r.enabled);
            bool nextRendered=EditorGUILayout.ToggleLeft("Render",rendered,GUILayout.Width(70));
            if(g!=null && nextActive!=active){Undo.RecordObject(g.gameObject,"Floor active");g.gameObject.SetActive(nextActive);MarkDirty();}
            if(g!=null && nextRendered!=rendered)RenderGroup(g,nextRendered);
            if(GUILayout.Button("Focus") && g!=null){Selection.activeGameObject=g.gameObject;SceneView.lastActiveSceneView?.FrameSelected();}
            EditorGUILayout.EndHorizontal();
        }
    }
    static void RenderGroup(Transform g,bool visible)
    {
        if(g==null)return;foreach(var r in g.GetComponentsInChildren<Renderer>(true)){Undo.RecordObject(r,"Floor rendering");r.enabled=visible;}MarkDirty();
    }
    void Preset(bool first,bool second)
    {
        foreach(var pair in new[]{new KeyValuePair<string,bool>("Floor 1",first),new KeyValuePair<string,bool>("Floor 2",second),new KeyValuePair<string,bool>("Upper stair cores",false),new KeyValuePair<string,bool>("Site",second)})
        {var g=FindGroup(pair.Key);if(g==null)continue;Undo.RecordObject(g.gameObject,"Show floors");g.gameObject.SetActive(pair.Value);RenderGroup(g,true);}
        MarkDirty();
    }
    static void SyncLabels(Transform root)
    {
        if(root==null)return;
        Transform first=root.Find("Floor 1"), second=root.Find("Floor 2");
        bool upperVisible=second!=null && second.gameObject.activeInHierarchy &&
            second.GetComponentsInChildren<Renderer>(true).Any(r=>r.enabled && r.GetComponent<TextMesh>()==null);
        if(first!=null)
        {
            Transform labels=first.Find("Room labels");
            if(labels!=null && labels.gameObject.activeSelf==upperVisible) labels.gameObject.SetActive(!upperVisible);
        }
    }
    static void Despawn(string name) { var g=FindGroup(name);if(g!=null){Undo.DestroyObjectImmediate(g.gameObject);MarkDirty();} }
    static void MarkDirty(){EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());SceneView.RepaintAll();}
}

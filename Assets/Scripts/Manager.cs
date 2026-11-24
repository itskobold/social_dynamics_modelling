using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
using System.Collections.Generic;
using Pathfinding;

public class Manager : MonoBehaviour
{
    //public Dictionary<int, int> allAgentIDs;  // <groupID, globalID>

    [Header("Debug")]
    public bool verbose;

    [Header("RNG")]
    public Unity.Mathematics.Random rand;
    public uint seed;

    [Header("Terrain")]
    public float2 terrainSize;
    public float dx;
    public float navDx;  // Grid sampling density of navigation mesh
    public TerrainGen terrain;

    // Street parameters (world units)
    public float minStreetWidth = 4f;
    public float maxStreetWidth = 8f;
    public float marginSize;

    // Building dimensions
    public float minBlockSpacing = 12f;
    public float maxBlockSpacing = 25f;
    public float minBuildingHeight = 3f;
    public float maxBuildingHeight = 12f;
    public float minBuildingSize = 10f;
    public float pavementWidth = 2f;

    // Attractors
    private GameObject attractorObj;
    public List<Attractor> doorAttractors;
    public List<Attractor> streetAttractors;
    public float streetEndAttraction = 0.5f;
    public int numAttractors = 0;

    // Spawners
    private GameObject spawnerObj;
    public List<SpawnerData> spawners;
    public Dictionary<int, Spawner> spawnersFree;
    public Dictionary<int, Spawner> spawnersBusy;
    public int carCounter = 0;
    public int carThresh;
    public int minCarThresh;
    public int maxCarThresh;
    public int numSpawners = 0;

    [Header("Models & Materials")]
    public GameObject playerPrefabM;
    public GameObject playerPrefabF;
    public List<GameObject> carPrefabs;
    public Material buildingLowMaterial;
    public Material buildingMidMaterial;
    public Material buildingHighMaterial;
    public Material buildingVHighMaterial;
    public Material pavementMaterial;
    public Material streetMaterial;
    public Material attractorMaterial;
    public Material spawnerMaterial;
    public Material spawnerRangeMaterial;

    [Header("Agent Groups")]
    public int numAgentGroups;
    public int numCarGroups;
    public int minAgentsPerGroup;
    public int maxAgentsPerGroup;
    public Dictionary<int, AgentGroup> agentGroupsFree;
    public Dictionary<int, AgentGroup> agentGroupsBusy;

    [Header("Agent Properties")]
    public float agentBodyRadius = 0.4f;
    public float agentHeight = 2f;
    public float agentSpeed = 1f;
    public float agentTurnSpeed = 120f;

    [Header("Car Properties")]
    public float carBodyRadius = 2.5f;
    public float carHeight = 2f;
    public float carSpeed = 3f;
    public float carTurnSpeed = 10f;

    void Start()
    {
        rand = new Unity.Mathematics.Random(seed);
        agentGroupsFree = new Dictionary<int, AgentGroup>();
        agentGroupsBusy = new Dictionary<int, AgentGroup>();

        LoadCarPrefabs();

        doorAttractors = new List<Attractor>();
        streetAttractors = new List<Attractor>();
        attractorObj = new GameObject();
        attractorObj.name = "Attractors";
        attractorObj.transform.SetParent(gameObject.transform);

        spawners = new List<SpawnerData>();
        spawnerObj = new GameObject();
        spawnerObj.name = "Spawners";
        spawnerObj.transform.SetParent(gameObject.transform);

        spawnersFree = new Dictionary<int, Spawner>();
        spawnersBusy = new Dictionary<int, Spawner>();

        //SetCarCounter();
        CreateTerrain();

        for (int i = 0; i < numAgentGroups + numCarGroups; i++)
        {
            CreateAgentGroup(i, i >= numAgentGroups);
        }
    }

    private void LoadCarPrefabs()
    {
        carPrefabs = new List<GameObject>();

        string rootPath = "Assets/Low Poly Soviet Car Pack/Prefabs";
        string[] prefabGUIDs = AssetDatabase.FindAssets("t:Prefab", new[] { rootPath });

        foreach (string guid in prefabGUIDs)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            carPrefabs.Add(prefab);
        }
    }

    public GameObject GetRandomCarPrefab()
    {
        return carPrefabs[rand.NextInt(carPrefabs.Count)];
    }

    void Update()
    {
        GridGraph grid = AstarPath.active.data.gridGraph;
        int penalty = 200;

        // Reset penalties each frame (or every few seconds)
        grid.GetNodes(node => node.Penalty = 0);

        // Find all agents and apply penalties around them
        foreach (var agent in FindObjectsByType<FollowerEntity>(FindObjectsSortMode.None))
        {
            var nearestNode = grid.GetNearest(agent.transform.position).node;
            if (nearestNode != null)
            {
                var bounds = new Bounds(agent.transform.position, Vector3.one);
                List<GraphNode> nodes = grid.GetNodesInRegion(bounds);

                foreach (var n in nodes)
                {
                    n.Penalty += (uint)penalty;
                }
            }
        }

        // --- Handle busy groups ---
        if (agentGroupsBusy.Count > 0)
        {
            var toFree = new List<int>();

            foreach (var kv in agentGroupsBusy)
            {
                AgentGroup aG = kv.Value;
                if (!aG.isActive)
                    toFree.Add(aG.groupID);
            }

            // Move inactive groups to free list
            foreach (var id in toFree)
            {
                var aG = agentGroupsBusy[id];
                agentGroupsFree[id] = aG;
                agentGroupsBusy.Remove(id);
            }
        }

        // --- Handle free groups ---
        while (agentGroupsFree.Count > 0)
        {
            var enumerator = agentGroupsFree.GetEnumerator();
            enumerator.MoveNext();
            var kv = enumerator.Current;

            AgentGroup aG = kv.Value;
            Spawner spawner = TryGetSpawner();

            if (spawner == null)
                break;

            spawner.SpawnAgentGroup(aG);
            aG.SetAllAgentDestinations(GetRandomAttractor(aG.isCar, spawner.data.localAttractorID));

            agentGroupsBusy[aG.groupID] = aG;
            agentGroupsFree.Remove(aG.groupID);
        }
    }

    private void SetCarCounter()
    {
        carCounter = 0;
        carThresh = rand.NextInt(minCarThresh, maxCarThresh);
    }

    private void TrySpawnCar()
    {
        Spawner spawner = TryGetSpawner();
        if (spawner == null) return;

        
    }

    private Spawner TryGetSpawner()
    {
        if (spawnersFree.Count == 0)
            return null; // No available spawners

        // Pick a random index
        int randomIndex = rand.NextInt(0, spawnersFree.Count);

        // Get the spawner at that index
        int i = 0;
        foreach (var kvp in spawnersFree)
        {
            if (i == randomIndex)
                return kvp.Value; // return the Spawner
            i++;
        }

        return null; // Shouldn’t happen, just fallback
    }

    public Attractor GetAttractorFromID(int id)
    {
        Attractor ret;
        if (id >= streetAttractors.Count)
            ret = doorAttractors[id - streetAttractors.Count];
        else
            ret = streetAttractors[id];
        return ret;
    }

    private Attractor GetRandomAttractor(bool streetOnly, int attractorIDtoIgnore)
    {
        int limit;
        if (streetOnly)
            limit = streetAttractors.Count;
        else
            limit = numAttractors;
        
        int id;
        while (true)
        {
            id = rand.NextInt(0, limit);
            if (id != attractorIDtoIgnore)
                break;
        } 

        return GetAttractorFromID(id);
    }

    private void CreateTerrain()
    {
        // Create a plane primitive
        GameObject terrainObj = new GameObject();
        terrainObj.name = "Terrain";
        terrain = terrainObj.AddComponent<TerrainGen>();
        terrain.Init(this, terrainSize, dx);
    }

    private void CreateAgentGroup(int groupID, bool isCar)
    {
        GameObject go = new GameObject();
        AgentGroup aG = go.AddComponent<AgentGroup>();

        int nA = (int)(rand.NextDouble() * maxAgentsPerGroup + minAgentsPerGroup);
        aG.Init(this, groupID, isCar, nA);
        agentGroupsFree[groupID] = aG;

        aG.gameObject.name = $"Agent Group #{groupID}";
        aG.gameObject.transform.parent = gameObject.transform;
        Dbg_TryPrint($"MANAGER: Created agent group #{groupID} with {nA} agents.");
    }

    public void CreateAttractor(AttractorData aD, bool isDoor)
    {
        // Register attractor data
        Attractor attractor = SpawnAttractor(aD);
        if (isDoor)
            doorAttractors.Add(attractor);
        else
            streetAttractors.Add(attractor);
    }

    private Attractor SpawnAttractor(AttractorData aD)
    {
        float3 pos = new float3(aD.pos.x, aD.scale.y / 2f, aD.pos.y);

        // Create cube
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(attractorObj.transform);
        cube.transform.position = new Vector3(pos.x, pos.y, pos.z);
        cube.transform.localScale = new Vector3(aD.scale.x, aD.scale.y, aD.scale.z);

        // Set rotation
        switch (aD.facing)
        {
            default:
            case Dir.NORTH:
                break;
            case Dir.SOUTH:
                cube.transform.eulerAngles = new Vector3(0, 180, 0);
                break;
            case Dir.WEST:
                cube.transform.eulerAngles = new Vector3(0, 270, 0);
                break;
            case Dir.EAST:
                cube.transform.eulerAngles = new Vector3(0, 90, 0);
                break;
        }

        cube.GetComponent<MeshRenderer>().material = attractorMaterial;
        Attractor attractor = cube.AddComponent<Attractor>();
        attractor.Init(aD);
        cube.name = $"Attractor ({pos.x:F1}, {pos.z:F1})";

        return attractor;
    }

    public void CreateAgentSpawner(SpawnerData sD)
    {
        // Register spawner data
        spawners.Add(sD);

        float3 pos = new float3(sD.pos.x, 1f, sD.pos.y);

        // Create sphere
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.SetParent(spawnerObj.transform);
        sphere.transform.position = new Vector3(pos.x, pos.y, pos.z);
        sphere.GetComponent<MeshRenderer>().material = spawnerMaterial;
        sphere.name = $"Spawner ({pos.x:F1}, {pos.z:F1})";

        // Create cube
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(sphere.transform);
        cube.transform.position = new Vector3(pos.x, 5f, pos.z);
        cube.transform.localScale = new Vector3(sD.size.x, 10f, sD.size.y);

        // Set material
        cube.GetComponent<MeshRenderer>().material = spawnerRangeMaterial;

        // Add component
        Spawner spawner = sphere.AddComponent<Spawner>();
        spawner.Init(this, sD);
        spawnersFree[sD.id] = spawner;
    }

    public void Dbg_TryPrint(string msg)
    {
        if (verbose)
            Debug.Log(msg);
    }
}

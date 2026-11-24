using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using Unity.Mathematics;
using System.Collections.Generic;
using Pathfinding;
using Pathfinding.RVO;

// MARK: Agent Data
[System.Serializable]
public struct AgentData
{
    // Grouping
    public int agentID;
    public int groupID;

    // Positioning
    public float2 position;  // X, Z
    public float yRotation;  // Around Y axis 

    public AgentData(int agentID, int groupID, float2 initPos, float initYRotation)
    {
        this.agentID = agentID;
        this.groupID = groupID;
        this.position = initPos;
        this.yRotation = initYRotation;
    }
}

// MARK: Agent
public class Agent : MonoBehaviour
{
    public AgentData agentData;
    private float stuckTime;
    public AgentGroup agentGroup;  // Associated group
    public GameObject destinationObj;
    public FollowerEntity followerEntity;
    public AIDestinationSetter destinationSetter;
    private Animator animator;
    public bool readyToDespawn;
    private Bounds bounds;

    public void Init(AgentGroup agentGroup, int agentID, int groupID)
    {
        this.agentData.agentID = agentID;
        this.agentData.groupID = groupID;
        this.readyToDespawn = false;
        this.animator = gameObject.GetComponent<Animator>();

        Unity.Mathematics.Random rand = agentGroup.manager.rand;
        this.agentGroup = agentGroup;

        this.followerEntity = gameObject.AddComponent<FollowerEntity>();
        this.followerEntity.radius = agentGroup.isCar ? agentGroup.manager.carBodyRadius : agentGroup.manager.agentBodyRadius;
        this.followerEntity.height = agentGroup.manager.agentHeight;
        this.followerEntity.stopDistance = 0;

        var settings = this.followerEntity.movementSettings;
        settings.follower.desiredWallDistance = 2f;
        settings.follower.slowdownTime = 0;
        settings.follower.allowRotatingOnSpot = !agentGroup.isCar;
        this.followerEntity.movementSettings = settings;

        this.followerEntity.maxSpeed = Util.NextNormal(ref agentGroup.manager.rand, agentGroup.groupSpeed, 0.01f);
        this.followerEntity.rotationSpeed = agentGroup.isCar ? agentGroup.manager.carTurnSpeed : agentGroup.manager.agentTurnSpeed;
        this.followerEntity.maxRotationSpeed = agentGroup.isCar ? agentGroup.manager.carTurnSpeed : agentGroup.manager.agentTurnSpeed;

        this.bounds = Util.CalculateWorldBounds(gameObject);
        gameObject.transform.position -= new Vector3(0, this.bounds.min.y, 0);

        if (agentGroup.isCar)
        {
            this.followerEntity.enableLocalAvoidance = false;
            this.followerEntity.enableGravity = false;
            Bounds localBounds = Util.CalculateLocalBounds(gameObject);
            NavMeshObstacle obstacle = gameObject.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = localBounds.center;
            obstacle.size = localBounds.size;
            obstacle.carving = true;
            gameObject.tag = "Car";
            Util.SetLayerRecursively(gameObject, "Car");
        }
        else
        {
            this.followerEntity.enableLocalAvoidance = true;
            this.followerEntity.rvoSettings.agentTimeHorizon = 2.5f;
            this.followerEntity.rvoSettings.obstacleTimeHorizon = 2.5f;
            this.followerEntity.rvoSettings.priority = 1f;
            gameObject.tag = "Agent";
        }

        this.destinationSetter = gameObject.AddComponent<AIDestinationSetter>();

        foreach (Transform child in gameObject.transform)
        {
            if (child.name.Contains("BodyMesh"))
            {
                child.GetComponent<Renderer>().material.SetColor("_Color", agentGroup.groupColor);
                break;
            }
        }

        // Set name
        gameObject.name = $"Agent {this.agentData.groupID}:{this.agentData.agentID}";
    }

    private void Update()
    {
        if (agentGroup.isCar)
        {
            bounds = Util.CalculateWorldBounds(gameObject);
            AstarPath.active.UpdateGraphs(bounds);
        }
        else
        {
            float speed = GetSpeed();

            // If moving slower than expected, increase "stuck" timer
            if (speed < 0.2f)
            {
                stuckTime += Time.deltaTime;
                followerEntity.rvoSettings.priority = Mathf.Clamp01(followerEntity.rvoSettings.priority - 0.001f * agentGroup.manager.rand.NextFloat());
                if (stuckTime > 1.5f)
                {
                    followerEntity.SearchPath(); // force recalc
                    stuckTime = 0;
                }
            }
            else
            {
                stuckTime = 0;
                followerEntity.rvoSettings.priority = 1f;
            }

            UpdateAnimationData();
        }

        UpdateAgentData();
        if (ArrivedAtDestination())
            readyToDespawn = true;
    }

    private void UpdateAgentData()
    {
        agentData.position.x = gameObject.transform.position.x;
        agentData.position.y = gameObject.transform.position.z;
        agentData.yRotation = gameObject.transform.eulerAngles.y;
    }

    private void UpdateAnimationData()
    {
        animator.SetFloat("Speed", GetSpeed());
    }

    private bool ArrivedAtDestination()
    {
        Transform t = destinationObj.transform;

        // --- 1. COMPUTE FRONT FACE CENTER ---
        float halfDepth = t.localScale.z * 0.5f;
        Vector3 frontCenter3D = t.position + t.forward * halfDepth;

        // Convert to float2 for XZ-plane logic
        float2 frontCenter = new float2(frontCenter3D.x, frontCenter3D.z);

        // Point relative to the front face center (XZ only)
        float2 faceToPoint = agentData.position - frontCenter;

        // --- 2. DISTANCE ALONG FORWARD DIRECTION (2D) ---
        float2 forward2D = math.normalize(new float2(t.forward.x, t.forward.z));
        float distanceAlongForward = math.dot(faceToPoint, forward2D);

        // Must be close to the face plane
        const float faceThreshold = 1.5f;
        if (math.abs(distanceAlongForward) > faceThreshold)
            return false;

        // --- 3. CHECK WITHIN WIDTH / HEIGHT BOUNDS OF THE SCALED CUBE ---
        // Half-width (x) and half-height (y) in world space based on scale.
        float halfWidth = t.localScale.x * 0.5f;   // X-axis extent
        float halfHeight = t.localScale.y * 0.5f;  // Y-axis extent

        // Compute right direction in XZ plane
        float2 right2D = math.normalize(new float2(t.right.x, t.right.z));

        // Project onto right direction to get lateral offset
        float lateral = math.dot(faceToPoint, right2D);

        // Check lateral bounds (using halfWidth)
        if (math.abs(lateral) > halfWidth)
            return false;

        return true;
    }

    private float GetSpeed()
    {
        Vector2 vecSpeed = new Vector2(Mathf.Abs(followerEntity.velocity.x), Mathf.Abs(followerEntity.velocity.z)) / agentGroup.manager.agentSpeed;
        return vecSpeed.sqrMagnitude;
    }
}

// MARK: Agent Group
public class AgentGroup : MonoBehaviour
{
    public Manager manager;
    public bool isActive;
    public bool isCar;
    public int groupID;
    public Color groupColor;
    public int targetAgents;
    public float groupSpeed;
    public List<Agent> agents;
    
    public int NumAgents() => agents.Count;

    public void Init(Manager manager, int groupID, bool isCar, int targetAgents)
    {
        this.manager = manager;
        this.groupID = groupID;
        this.groupColor = Util.GetRandomColor();
        this.targetAgents = isCar ? 1 : targetAgents;
        this.groupSpeed = Util.NextNormal(ref manager.rand, isCar ? manager.carSpeed : manager.agentSpeed, 0.05f);
        this.agents = new List<Agent>();
        this.isActive = false;
        this.isCar = isCar;
    }

    private void Update()
    {
        TryDestroyAgents();
    }

    public void CreateAgents(Spawner spawner)
    {
        int maxAgents = spawner.MaxSpawns();
        if (maxAgents > targetAgents)
            maxAgents = targetAgents;

        for (int i = 0; i < maxAgents; i++)
        {
            Agent agent = CreateAgentObject(i, spawner);
            agent.Init(this, i, groupID);
            agents.Add(agent);
        }
        isActive = true;
    }

    // Destroy agents that have reached their destinations
    public void TryDestroyAgents()
    {
        for (int i = agents.Count - 1; i >= 0; i--)
        {
            Agent agent = agents[i];
            if (agent.readyToDespawn)
            {
                Destroy(agent.gameObject);
                agents.RemoveAt(i);
            }
        }

        if (NumAgents() == 0)
            isActive = false;
    }

    private Agent CreateAgentObject(int agentID, Spawner spawner)
    {
        // Create object
        GameObject go;
        if (isCar)
            go = Instantiate(manager.GetRandomCarPrefab());
        else
            go = Instantiate(manager.rand.NextInt(2) == 1 ? manager.playerPrefabM : manager.playerPrefabF);

        Agent agent = go.AddComponent<Agent>();
        go.transform.parent = gameObject.transform;
        go.transform.position = new Vector3(spawner.spawnLocations[agentID].x, 0, spawner.spawnLocations[agentID].y);
        return agent;
    }

    public bool SetAgentDestination(Agent agent, Attractor attractor)
    {
        if (agents.Count == 0)
        {
            Debug.Log("AGENT_GROUP: Trying to set destination but not initialized!");
            return false;
        }

        agent.destinationObj = attractor.gameObject;
        agent.destinationSetter.target = attractor.gameObject.transform;
        return true;
    }

    public bool SetAllAgentDestinations(Attractor attractor)
    {
        foreach (Agent agent in agents)
        {
            if (!SetAgentDestination(agent, attractor))
                return false;
        }
        return true;
    }
}

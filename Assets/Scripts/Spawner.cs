using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;

[System.Serializable]
public struct SpawnerData
{
    public int id;
    public float2 size;
    public float2 pos;
    public float popularity;
    public Dir facing;
    public int localAttractorID;

    public SpawnerData(int id, float2 size, float2 pos, float popularity, Dir facing, int localAttractorID)
    {
        this.id = id;
        this.pos = pos;
        this.popularity = popularity;
        this.facing = facing;
        this.localAttractorID = localAttractorID;
        if (this.facing == Dir.NORTH || this.facing == Dir.SOUTH)
            this.size = size;
        else
        {
            float2 temp;
            temp.x = size.y;
            temp.y = size.x;
            this.size = temp;
        }
    }
}

public class Spawner : MonoBehaviour
{
    private Manager manager;
    public SpawnerData data;
    public bool isBusy;
    public List<float2> spawnLocations;

    public int MaxSpawns() => spawnLocations.Count;

    public void Init(Manager manager, SpawnerData data)
    {
        this.manager = manager;
        this.data = data;
        this.spawnLocations = GetSpawnLocations(data.size, data.pos, manager.agentBodyRadius);
    }

    public void SpawnAgentGroup(AgentGroup aG)
    {
        this.isBusy = true;
        aG.CreateAgents(this);
        manager.spawnersBusy[data.id] = this;
        manager.spawnersFree.Remove(data.id);
    }

    void Update()
    {
        if (isBusy && !Util.IsAgentInsideRegion(data.size, data.pos))
        {
            isBusy = false;
            manager.spawnersFree[data.id] = this;
            manager.spawnersBusy.Remove(data.id);
        }
    }

    public List<float2> GetSpawnLocations(float2 size, float2 pos, float radius)
    {
        var positions = new List<float2>();

        float minX = pos.x - size.x / 2f + radius;
        float maxX = pos.x + size.x / 2f - radius;
        float minZ = pos.y - size.y / 2f + radius;
        float maxZ = pos.y + size.y / 2f - radius;

        float rowSpacing = math.sqrt(3f) * radius;
        float colSpacing = 2f * radius;

        int row = 0;
        for (float z = minZ + radius; z <= maxZ - radius; z += rowSpacing, row++)
        {
            float xOffset = (row % 2 == 0) ? radius : 2f * radius;

            for (float x = minX + xOffset; x <= maxX - radius; x += colSpacing)
            {
                positions.Add(new float2(x, z));
            }
        }

        // Sort by distance to center
        positions.Sort((a, b) =>
        {
            float da = math.lengthsq(a - pos);
            float db = math.lengthsq(b - pos);
            return da.CompareTo(db);
        });

        return positions;
    }
}

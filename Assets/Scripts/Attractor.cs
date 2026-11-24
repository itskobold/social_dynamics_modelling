using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;

[System.Serializable]
public struct AttractorData
{
    public int id;
    public bool isDoor;
    public float3 scale;
    public float2 pos;
    public float popularity;
    public Dir facing;

    public AttractorData(int id, bool isDoor, float3 scale, float2 pos, float popularity, Dir facing)
    {
        this.id = id;
        this.isDoor = isDoor;
        this.scale = scale;
        this.pos = pos;
        this.popularity = popularity;
        this.facing = facing;
    }

    public bool IsPoint => scale.x == 0 && scale.y == 0 && scale.z == 0;
}

public class Attractor : MonoBehaviour
{
    public AttractorData data;

    public void Init(AttractorData aD)
    {
        this.data = aD;
    }
}

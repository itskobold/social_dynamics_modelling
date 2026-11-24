using UnityEngine;
using Unity.Mathematics;

public enum Dir
{
    WEST,
    NORTH,
    EAST,
    SOUTH
}

public enum Behaviour
{
    BOLDNESS,
    CURIOSITY,
    INTENT,
    AWARENESS,
    INFLUENCE,
    NUM_BEHAVIOUR_PARAMS
}

public class Util
{
    public static float NextNormal(ref Unity.Mathematics.Random rand, float mean, float variance)
    {
        float stdDev = math.sqrt(variance);

        // Generate two uniform random numbers in (0,1)
        float u1 = math.max((float)rand.NextDouble(), 1e-7f); // avoid log(0)
        float u2 = (float)rand.NextDouble();

        // Box-Muller transform
        float r = math.sqrt(-2f * math.log(u1));
        float theta = 2f * math.PI * u2;
        float z = r * math.cos(theta); // standard normal

        return mean + z * stdDev;
    }

    public static float Clamp(float value, float min, float max)
    {
        if (value < min)
            value = min;
        else if (value > max)
            value = max;
        return value;
    }

    public static float Clamp01(float value)
    {
        return Clamp(value, 0, 1);
    }

    public static bool IsAgentInsideRegion(float2 size, float2 center)
    {
        // Bounds of the region
        float2 halfExtents = new float2(size.x / 2f, size.y / 2f);
        float2 min = center - halfExtents;
        float2 max = center + halfExtents;

        // Find all agents
        GameObject[] agents = GameObject.FindGameObjectsWithTag("Agent");
        foreach (GameObject agent in agents)
        {
            Vector3 pos = agent.transform.position;

            // Check inside XZ rectangle
            if (pos.x >= min.x && pos.x <= max.x &&
                pos.z >= min.y && pos.z <= max.y)
            {
                return true; // At least one agent inside
            }
        }

        return false; // None inside
    }

    public static Color GetRandomColor(float minBrightness = 0.25f, float maxBrightness = 0.75f)
    {
        // Clamp brightness range to [0, 1]
        minBrightness = Mathf.Clamp01(minBrightness);
        maxBrightness = Mathf.Clamp01(maxBrightness);

        if (minBrightness > maxBrightness)
        {
            // Swap if out of order
            float temp = minBrightness;
            minBrightness = maxBrightness;
            maxBrightness = temp;
        }

        float h = UnityEngine.Random.Range(0f, 1f);                    // Random hue
        float v = UnityEngine.Random.Range(minBrightness, maxBrightness); // Controlled brightness

        return Color.HSVToRGB(h, 1f, v);
    }

    public static Bounds CalculateLocalBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Transform t = root.transform;
        Bounds combined = new Bounds(
            t.InverseTransformPoint(renderers[0].bounds.center),
            renderers[0].bounds.size
        );

        foreach (Renderer r in renderers)
        {
            Bounds rb = r.bounds;
            rb.center = t.InverseTransformPoint(rb.center);
            combined.Encapsulate(rb);
        }

        return combined;
    }

    public static Bounds CalculateWorldBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
            return new Bounds(root.transform.position, Vector3.zero);

        Bounds combined = renderers[0].bounds;

        foreach (Renderer r in renderers)
        {
            combined.Encapsulate(r.bounds);
        }

        return combined;
    }

    public static void SetLayerRecursively(GameObject obj, string layer)
    {
        if (obj == null) return;

        int id = LayerMask.NameToLayer(layer);
        obj.layer = id;

        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}

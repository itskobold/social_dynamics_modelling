using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using Unity.Mathematics;
using Unity.Collections;
using Pathfinding;
using System;

public struct BuildingData
{
    public float4 bounds;     // (xMin, zMin, xMax, zMax) in world units
    public float popularity;  // 0–1 popularity
    public float height;      // precomputed world height

    public BuildingData(float4 bounds, float popularity, float height)
    {
        this.bounds = bounds;
        this.popularity = popularity;
        this.height = height;
    }

    public float2 Center => new float2(
        (bounds.x + bounds.z) * 0.5f,
        (bounds.y + bounds.w) * 0.5f
    );
}

public enum Cell
{
    STREET,
    PAVEMENT,
    BUILDING
}

public struct CellData
{
    public Cell type;
    public bool inBounds;
    public bool isWalkable;

    public CellData(Cell cellType)
    {
        type = cellType;
        inBounds = true;
        isWalkable = false;
    }
}

public class TerrainGen : MonoBehaviour
{
    private Manager manager;

    // Terrain parameters
    public float2 terrainSize;
    public NativeArray<CellData> terrainGrid;
    Texture2D terrainGridTex;
    public int2 terrainGridSize;
    public float dx = 1f;

    // Navigation
    public NavMeshSurface navSurface;

    // Buildings and pavements (stored in native lists for jobs)
    public NativeList<BuildingData> buildings;
    public NativeList<float4> pavements;  // (xPos, zPos, width, depth) in world units

    // Temp attractor/spawner lists
    public NativeList<AttractorData> streetEndAttractors;
    public NativeList<AttractorData> doorAttractors;
    public NativeList<SpawnerData> streetEndSpawners;

    // Street parameters
    private float minStreetWidth;
    private float maxStreetWidth;
    private float minBlockSpacing;
    private float maxBlockSpacing;
    private float minBuildingHeight;
    private float maxBuildingHeight;

    // RNG
    private Unity.Mathematics.Random rand;

    public void Init(Manager manager, float2 terrainSize, float dx, uint seed = 1)
    {
        this.manager = manager;
        this.rand = new Unity.Mathematics.Random(seed);

        this.terrainSize = terrainSize;
        this.dx = dx;

        this.minStreetWidth = manager.minStreetWidth;
        this.maxStreetWidth = manager.maxStreetWidth;
        this.minBlockSpacing = manager.minBlockSpacing;
        this.maxBlockSpacing = manager.maxBlockSpacing;
        this.minBuildingHeight = manager.minBuildingHeight;
        this.maxBuildingHeight = manager.maxBuildingHeight;

        // native containers
        buildings = new NativeList<BuildingData>(Allocator.Persistent);
        pavements = new NativeList<float4>(Allocator.Persistent);
        streetEndAttractors = new NativeList<AttractorData>(Allocator.Persistent);
        doorAttractors = new NativeList<AttractorData>(Allocator.Persistent);
        streetEndSpawners = new NativeList<SpawnerData>(Allocator.Persistent);

        // Thread-safe: terrain grid generation
        terrainGridSize = GetTerrainGridSize();
        terrainGrid = new NativeArray<CellData>(terrainGridSize.x * terrainGridSize.y, Allocator.Persistent);
        int pavementWidthCells = manager.pavementWidth == 0 ? 0 : math.max(1, (int)math.ceil(manager.pavementWidth / dx));
        int marginCells = math.max(1, (int)math.ceil(manager.marginSize / dx));
        CreateTerrainGrid(marginCells, pavementWidthCells);

        // Thread-safe: placing buildings and attractors
        float3 worldOrigin = new float3(
            -dx * terrainGridSize.x * 0.5f,
            0f,
            -dx * terrainGridSize.y * 0.5f
        );
        int buildingCount = PlaceBuildings(worldOrigin, manager.minBuildingSize, pavementWidthCells, this.manager.marginSize);
        PlaceStreetEndAttractorsSpawners(worldOrigin, manager.streetEndAttraction, manager.marginSize - manager.pavementWidth);

        // Print outcomes
        if (manager.verbose)
            manager.Dbg_TryPrint($"Terrain generated.  Grid {terrainGridSize.x}x{terrainGridSize.y}.  Buildings: {buildingCount}.  Attractors: {manager.numAttractors}.  Spawners: {manager.numSpawners}.");

        // Render geometry on main thread
        RenderTerrainGeometry();

        // Create navigation surface
        BuildNavigationSurface(terrainGridSize.x, terrainGridSize.y, manager.navDx);

        // Set terrain shader properties
        terrainGridTex = CreateTerrainGridTexture();
        manager.streetMaterial.SetFloat("_viewMode", 0);
        manager.streetMaterial.SetTexture("_overlay", terrainGridTex);
        manager.streetMaterial.SetVector("_gridSize", new Vector2(terrainGridSize.x, terrainGridSize.y));
        manager.pavementMaterial.SetFloat("_viewMode", 0);
        manager.pavementMaterial.SetTexture("_overlay", terrainGridTex);
        manager.pavementMaterial.SetVector("_gridSize", new Vector2(terrainGridSize.x, terrainGridSize.y));
    }

    private void OnDestroy()
    {
        if (terrainGrid.IsCreated) terrainGrid.Dispose();
        if (buildings.IsCreated) buildings.Dispose();
        if (pavements.IsCreated) pavements.Dispose();
        if (streetEndAttractors.IsCreated) streetEndAttractors.Dispose();
        if (doorAttractors.IsCreated) doorAttractors.Dispose();
        if (streetEndSpawners.IsCreated) streetEndSpawners.Dispose();
    }

    // MARK: Job-safe
    private int2 GetTerrainGridSize()
    {
        int xCells = (int)math.ceil(terrainSize.x / dx);
        int yCells = (int)math.ceil(terrainSize.y / dx);
        return new int2(xCells, yCells);
    }

    private int Idx(int x, int y) => x * terrainGridSize.y + y;

    private void CreateTerrainGrid(int marginCells, int pavementWidthCells)
    {
        CellData cell;

        // Start everything as building
        for (int i = 0; i < terrainGrid.Length; i++) terrainGrid[i] = new CellData(Cell.BUILDING);

        int minStreetCells = math.max(1, (int)math.ceil(minStreetWidth / dx));
        int maxStreetCells = math.max(minStreetCells, (int)math.ceil(maxStreetWidth / dx));
        int minBlockCells = math.max(1, (int)math.ceil(minBlockSpacing / dx));
        int maxBlockCells = math.max(minBlockCells, (int)math.ceil(maxBlockSpacing / dx));

        // Vertical streets
        int x = marginCells;
        while (x < terrainGridSize.x - marginCells)
        {
            int streetWidth = rand.NextInt(minStreetCells, maxStreetCells + 1);

            for (int w = 0; w < streetWidth && (x + w) < terrainGridSize.x - marginCells - 1; w++)
            {
                int col = x + w;
                for (int yRow = 0; yRow < terrainGridSize.y; yRow++)
                {
                    cell = terrainGrid[Idx(col, yRow)];
                    cell.type = Cell.STREET;
                    cell.isWalkable = true;
                    if (yRow < marginCells - pavementWidthCells || yRow > terrainGridSize.y - marginCells + pavementWidthCells)
                        cell.inBounds = false;  // Out of bounds
                    terrainGrid[Idx(col, yRow)] = cell;
                }

                // Add pavement inside blocks
                for (int pw = 1; pw <= pavementWidthCells; pw++)
                {
                    if (col - pw >= 0) // left pavement
                    {
                        for (int yRow = 0; yRow < terrainGridSize.y; yRow++)
                        {
                            cell = terrainGrid[Idx(col - pw, yRow)];
                            if (cell.type == Cell.BUILDING)
                            {
                                cell.type = Cell.PAVEMENT;
                                if (yRow > 0)
                                    cell.isWalkable = true;
                                if (yRow < marginCells - pavementWidthCells || yRow > terrainGridSize.y - marginCells + pavementWidthCells)
                                    cell.inBounds = false;  // Out of bounds
                            }
                            terrainGrid[Idx(col - pw, yRow)] = cell;
                        }
                    }
                    if (col + pw < terrainGridSize.x) // right pavement
                    {
                        for (int yRow = 0; yRow < terrainGridSize.y; yRow++)
                        {
                            cell = terrainGrid[Idx(col + pw, yRow)];
                            if (cell.type == Cell.BUILDING)
                            {
                                cell.type = Cell.PAVEMENT;
                                if (yRow > 0)
                                    cell.isWalkable = true;
                                if (yRow < marginCells - pavementWidthCells || yRow > terrainGridSize.y - marginCells + pavementWidthCells)
                                    cell.inBounds = false;  // Out of bounds
                            }
                            terrainGrid[Idx(col + pw, yRow)] = cell;
                        }
                    }
                }
            }

            x += streetWidth;
            int blockWidth = rand.NextInt(minBlockCells, maxBlockCells + 1);
            x += blockWidth;
        }

        // Horizontal streets
        int y = marginCells;
        while (y < terrainGridSize.y - marginCells)
        {
            int streetWidth = rand.NextInt(minStreetCells, maxStreetCells + 1);

            for (int w = 0; w < streetWidth && (y + w) < terrainGridSize.y - marginCells - 1; w++)
            {
                int row = y + w;
                for (int xCol = 0; xCol < terrainGridSize.x; xCol++)
                {
                    cell = terrainGrid[Idx(xCol, row)];
                    cell.type = Cell.STREET;
                    cell.isWalkable = true;
                    if (xCol < marginCells - pavementWidthCells || xCol > terrainGridSize.x - marginCells + pavementWidthCells)
                        cell.inBounds = false;  // Out of bounds
                    terrainGrid[Idx(xCol, row)] = cell;
                }

                // Add pavement inside blocks
                for (int pw = 1; pw <= pavementWidthCells; pw++)
                {
                    if (row - pw >= 0) // top pavement
                    {
                        for (int xCol = 0; xCol < terrainGridSize.x; xCol++)
                        {
                            cell = terrainGrid[Idx(xCol, row - pw)];
                            if (cell.type == Cell.BUILDING)
                            {
                                cell.type = Cell.PAVEMENT;
                                if (xCol > 0)
                                    cell.isWalkable = true;
                                if (xCol < marginCells - pavementWidthCells || xCol > terrainGridSize.x - marginCells + pavementWidthCells)
                                    cell.inBounds = false;  // Out of bounds
                            }
                            terrainGrid[Idx(xCol, row - pw)] = cell;
                        }
                    }
                    if (row + pw < terrainGridSize.y) // bottom pavement
                    {
                        for (int xCol = 0; xCol < terrainGridSize.x; xCol++)
                        {
                            cell = terrainGrid[Idx(xCol, row + pw)];
                            if (cell.type == Cell.BUILDING)
                            {
                                cell.type = Cell.PAVEMENT;
                                if (xCol > 0)
                                    cell.isWalkable = true;
                                if (xCol < marginCells - pavementWidthCells || xCol > terrainGridSize.x - marginCells + pavementWidthCells)
                                    cell.inBounds = false;  // Out of bounds
                            }
                            terrainGrid[Idx(xCol, row + pw)] = cell;
                        }
                    }
                }
            }

            y += streetWidth;
            int blockHeight = rand.NextInt(minBlockCells, maxBlockCells + 1);
            y += blockHeight;
        }
    }

    private int PlaceBuildings(float3 worldOrigin, float minBuildingSize, int pavementWidthCells, float marginSize)
    {
        // NOTE: For jobs, this BFS/scan should eventually be rewritten as IJobParallelFor.
        // Here we just replace managed arrays with NativeArray.
        NativeArray<bool> visited = new NativeArray<bool>(
            terrainGridSize.x * terrainGridSize.y,
            Allocator.Temp
        );

        int buildingCount = 0;

        for (int gx = 0; gx < terrainGridSize.x; gx++)
        {
            for (int gy = 0; gy < terrainGridSize.y; gy++)
            {
                int idx = Idx(gx, gy);
                if (visited[idx]) continue;
                if (terrainGrid[idx].type != Cell.BUILDING)
                {
                    visited[idx] = true;
                    continue;
                }

                // Detect contiguous block
                int2 size = FindBuildingBlock(gx, gy, visited);

                // Create pavement data
                float xPos = worldOrigin.x + (gx + size.x * 0.5f) / dx;
                float zPos = worldOrigin.z + (gy + size.y * 0.5f) / dx;
                float width = (size.x + pavementWidthCells * 2) / dx;
                float depth = (size.y + pavementWidthCells * 2) / dx;

                float offset = pavementWidthCells / dx;
                if (gx <= 0)
                {
                    width -= offset;
                    xPos += offset / 2f;
                }
                if (gx + size.x >= terrainGridSize.x)
                {
                    width -= offset;
                    xPos -= offset / 2f;
                }
                if (gy <= 0)
                {
                    depth -= offset;
                    zPos += offset / 2f;
                }
                if (gy + size.y >= terrainGridSize.y)
                {
                    depth -= offset;
                    zPos -= offset / 2f;
                }

                pavements.Add(new float4(xPos, zPos, width, depth));

                // Place building
                buildingCount += CreateBuildingFromBlock(gx, gy, size, minBuildingSize, worldOrigin, marginSize);
            }
        }

        visited.Dispose();
        return buildingCount;
    }

    private int2 FindBuildingBlock(int startX, int startY, NativeArray<bool> visited)
    {
        // Step 1: find width along x
        int maxWidth = 0;
        for (int x = startX; x < terrainGridSize.x; x++)
        {
            int idx = Idx(x, startY);
            if (terrainGrid[idx].type == Cell.BUILDING && !visited[idx])
                maxWidth++;
            else
                break;
        }

        int width = maxWidth;
        int height = 1;

        // Step 2: extend down along z (y in grid) while maintaining width
        for (int y = startY + 1; y < terrainGridSize.y; y++)
        {
            int rowWidth = 0;
            for (int x = startX; x < startX + width; x++)
            {
                int idx = Idx(x, y);
                if (terrainGrid[idx].type == Cell.BUILDING && !visited[idx])
                    rowWidth++;
                else
                    break;
            }

            if (rowWidth == 0)
                break; // no more rows in block

            width = math.min(width, rowWidth);
            height++;
        }

        // Step 3: mark cells visited
        for (int x = startX; x < startX + width; x++)
        {
            for (int y = startY; y < startY + height; y++)
            {
                visited[Idx(x, y)] = true;
            }
        }

        return new int2(width, height); // width=x, height=z
    }

    private int CreateBuildingFromBlock(int gx, int gy, int2 size, float minBuildingSize, float3 worldOrigin, float marginSize)
    {
        int count = 0;
        int minCells = Mathf.CeilToInt(minBuildingSize / dx);

        // Determine max number of buildings along each axis
        int numX = math.max(1, size.x / minCells);
        int numZ = math.max(1, size.y / minCells);

        // Float widths/heights per building
        float buildingWidth = size.x / (float)numX;
        float buildingDepth = size.y / (float)numZ;

        for (int ix = 0; ix < numX; ix++)
        {
            for (int iz = 0; iz < numZ; iz++)
            {
                // Start cell coordinates
                float startX = gx + ix * buildingWidth;
                float startZ = gy + iz * buildingDepth;

                // End cell coordinates
                float endX = gx + (ix + 1) * buildingWidth;
                float endZ = gy + (iz + 1) * buildingDepth;

                // Convert to int cells
                int w = Mathf.Max(minCells, Mathf.CeilToInt(endX - startX));
                int h = Mathf.Max(minCells, Mathf.CeilToInt(endZ - startZ));

                // Make sure we don't overshoot the block
                if (startX + w > gx + size.x) w = (gx + size.x) - Mathf.CeilToInt(startX);
                if (startZ + h > gy + size.y) h = (gy + size.y) - Mathf.CeilToInt(startZ);

                if (w <= 0 || h <= 0) continue;

                // Create building
                BuildingData bd = CreateSingleBuilding(
                    Mathf.CeilToInt(startX),
                    Mathf.CeilToInt(startZ),
                    w, h,
                    worldOrigin,
                    marginSize
                );
                buildings.Add(bd);
                count++;
            }
        }

        return count;
    }

    private BuildingData CreateSingleBuilding(int gx, int gy, int widthCells, int depthCells, float3 worldOrigin, float marginSize)
    {
        // compute footprint in world units
        float worldWidth = widthCells * dx;
        float worldDepth = depthCells * dx;

        // choose a deterministic height now and store it for rendering
        float heightWorld = rand.NextFloat(minBuildingHeight, maxBuildingHeight);

        // compute center position (y=0 for footprint center)
        float centerX = (gx * dx) + worldWidth * 0.5f;
        float centerZ = (gy * dx) + worldDepth * 0.5f;
        float3 center = worldOrigin + new float3(centerX, 0f, centerZ);

        // bounds: xMin, zMin, xMax, zMax
        float xMin = center.x - worldWidth * 0.5f;
        float zMin = center.z - worldDepth * 0.5f;
        float xMax = center.x + worldWidth * 0.5f;
        float zMax = center.z + worldDepth * 0.5f;

        // door size offsets
        float xSz = (xMax - xMin) * 0.4f;
        float zSz = (zMax - zMin) * 0.4f;

        // popularity
        float popularity = rand.NextFloat(0f, 1f);

        float doorWidth = 0.125f;

        for (int i = 0; i < 4; i++)
        {
            if (!SideFacesStreet(gx, gy, widthCells, depthCells, terrainGridSize, i) || manager.rand.NextFloat() < 0.5f)
                continue;

            Dir dir;
            float3 scale;
            float2 pos;

            float doorLengthX = xMax - xMin - 2 * xSz; // usable horizontal length
            float doorLengthZ = zMax - zMin - 2 * zSz; // usable vertical length

            if (i == 0) // left
            {
                dir = Dir.WEST;
                scale = new float3(doorLengthZ, 2, doorWidth);
                pos = new float2(xMin, (zMin + zMax) * 0.5f);
            }
            else if (i == 1) // bottom
            {
                dir = Dir.SOUTH;
                scale = new float3(doorLengthX, 2, doorWidth);
                pos = new float2((xMin + xMax) * 0.5f, zMin);
            }
            else if (i == 2) // right
            {
                dir = Dir.EAST;
                scale = new float3(doorLengthZ, 2, doorWidth);
                pos = new float2(xMax, (zMin + zMax) * 0.5f);
            }
            else // top
            {
                dir = Dir.NORTH;
                scale = new float3(doorLengthX, 2, doorWidth);
                pos = new float2((xMin + xMax) * 0.5f, zMax);
            }

            doorAttractors.Add(new AttractorData(manager.numAttractors, true, scale, pos, popularity, dir));
            manager.numAttractors++;
        }

        return new BuildingData(new float4(xMin, zMin, xMax, zMax), popularity, heightWorld);
    }

    private bool SideFacesStreet(int gx, int gy, int width, int depth, int2 gridSize, int side)
    {
        CellData cell;
        bool ret = false;
        if (side == 0) // left
        {
            int x = gx - 1;
            if (x < 0 || x >= gridSize.x) return false; // too close to left edge
            for (int z = gy; z < gy + depth; z++)
            {
                cell = terrainGrid[Idx(x, z)];
                if (!cell.inBounds) return false;
                if (cell.type != Cell.BUILDING) ret = true;
            }
        }
        else if (side == 1) // bottom
        {
            int z = gy - 1;
            if (z < 0 || z >= gridSize.y) return false; // too close to bottom edge
            for (int x = gx; x < gx + width; x++)
            {
                cell = terrainGrid[Idx(x, z)];
                if (!cell.inBounds) return false;
                if (cell.type != Cell.BUILDING) ret = true;
            }
        }
        else if (side == 2) // right
        {
            int x = gx + width;
            if (x < 0 || x >= gridSize.x) return false; // too close to right edge
            for (int z = gy; z < gy + depth; z++)
            {
                cell = terrainGrid[Idx(x, z)];
                if (!cell.inBounds) return false;
                if (cell.type != Cell.BUILDING) ret = true;
            }
        }
        else if (side == 3) // top
        {
            int z = gy + depth;
            if (z < 0 || z >= gridSize.y) return false; // too close to top edge
            for (int x = gx; x < gx + width; x++)
            {
                cell = terrainGrid[Idx(x, z)];
                if (!cell.inBounds) return false;
                if (cell.type != Cell.BUILDING) ret = true;
            }
        }

        return ret;
    }

    private bool IsStreetAtWorldPosition(float worldX, float worldZ)
    {
        int gx = Mathf.CeilToInt((worldX + terrainSize.x / 2f) / dx);
        int gy = Mathf.CeilToInt((worldZ + terrainSize.y / 2f) / dx);

        // Clamp to grid
        gx = Mathf.Clamp(gx, 0, terrainGridSize.x - 1);
        gy = Mathf.Clamp(gy, 0, terrainGridSize.y - 1);

        return terrainGrid[Idx(gx, gy)].type != Cell.BUILDING;
    }

    private void PlaceStreetEndAttractorsSpawners(float3 worldOrigin, float attraction, float marginSize)
    {
        float thickness = 1f;

        // Convert margin from world units to grid cells
        int marginCells = Mathf.CeilToInt(marginSize / dx);
        int minGX = marginCells;
        int maxGX = terrainGridSize.x - marginCells;
        int minGY = marginCells;
        int maxGY = terrainGridSize.y - marginCells;

        // LEFT edge (x = minGX)
        int leftGX = minGX - 1;
        float leftSpawnerGX = leftGX / 2f;
        for (int gy = minGY; gy < maxGY;)
        {
            if (terrainGrid[Idx(leftGX, gy)].type != Cell.BUILDING)
            {
                int startGY = gy;
                while (gy < maxGY && terrainGrid[Idx(leftGX, gy)].type != Cell.BUILDING) gy++;

                float length = (gy - startGY) * dx;
                float3 scale = new float3(length, 5, thickness);

                float centerX = worldOrigin.x + minGX * dx - thickness * 0.5f;
                float centerSpawnerX = worldOrigin.x + leftSpawnerGX * dx;
                float centerZ = worldOrigin.z + (startGY + gy) * 0.5f * dx;
                float2 pos = new float2(centerX, centerZ);

                streetEndAttractors.Add(new AttractorData(manager.numAttractors, false, scale, pos, attraction, Dir.EAST));
                streetEndSpawners.Add(new SpawnerData(manager.numSpawners, new float2(length, marginSize - 1), new float2(centerSpawnerX, centerZ), attraction, Dir.EAST, manager.numAttractors));
                manager.numAttractors++;
                manager.numSpawners++;
            }
            else gy++;
        }

        // RIGHT edge (x = maxGX - 1)
        int rightGX = maxGX;
        float rightSpawnerGX = (terrainGridSize.x - rightGX) / 2f + rightGX;
        for (int gy = minGY; gy < maxGY;)
        {
            if (terrainGrid[Idx(rightGX, gy)].type != Cell.BUILDING)
            {
                int startGY = gy;
                while (gy < maxGY && terrainGrid[Idx(rightGX, gy)].type != Cell.BUILDING) gy++;

                float length = (gy - startGY) * dx;
                float3 scale = new float3(length, 5, thickness);

                float centerX = worldOrigin.x + rightGX * dx + thickness * 0.5f;
                float centerSpawnerX = worldOrigin.x + rightSpawnerGX * dx;
                float centerZ = worldOrigin.z + (startGY + gy) * 0.5f * dx;
                float2 pos = new float2(centerX, centerZ);

                streetEndAttractors.Add(new AttractorData(manager.numAttractors, false, scale, pos, attraction, Dir.WEST));
                streetEndSpawners.Add(new SpawnerData(manager.numSpawners, new float2(length, marginSize - 1), new float2(centerSpawnerX, centerZ), attraction, Dir.WEST, manager.numAttractors));
                manager.numAttractors++;
                manager.numSpawners++;
            }
            else gy++;
        }

        // BOTTOM edge (z = minGY)
        int bottomGY = minGY - 1;
        float btmSpawnerGY = bottomGY / 2f;
        for (int gx = minGX; gx < maxGX;)
        {
            if (terrainGrid[Idx(gx, bottomGY)].type != Cell.BUILDING)
            {
                int startGX = gx;
                while (gx < maxGX && terrainGrid[Idx(gx, bottomGY)].type != Cell.BUILDING) gx++;

                float length = (gx - startGX) * dx;
                float3 scale = new float3(length, 5, thickness);

                float centerX = worldOrigin.x + (startGX + gx) * 0.5f * dx;
                float centerZ = worldOrigin.z + minGY * dx - thickness * 0.5f;
                float centerSpawnerZ = worldOrigin.z + btmSpawnerGY * dx;
                float2 pos = new float2(centerX, centerZ);

                streetEndAttractors.Add(new AttractorData(manager.numAttractors, false, scale, pos, attraction, Dir.NORTH));
                streetEndSpawners.Add(new SpawnerData(manager.numSpawners, new float2(length, marginSize - 1), new float2(centerX, centerSpawnerZ), attraction, Dir.NORTH, manager.numAttractors));
                manager.numAttractors++;
                manager.numSpawners++;
            }
            else gx++;
        }

        // TOP edge (z = maxGY - 1)
        int topGY = maxGY;
        float topSpawnerGY = (terrainGridSize.y - topGY) / 2f + topGY;
        for (int gx = minGX; gx < maxGX;)
        {
            if (terrainGrid[Idx(gx, topGY)].type != Cell.BUILDING)
            {
                int startGX = gx;
                while (gx < maxGX && terrainGrid[Idx(gx, topGY)].type != Cell.BUILDING) gx++;

                float length = (gx - startGX) * dx;
                float3 scale = new float3(length, 5, thickness);

                float centerX = worldOrigin.x + (startGX + gx) * 0.5f * dx;
                float centerZ = worldOrigin.z + topGY * dx + thickness * 0.5f;
                float centerSpawnerZ = worldOrigin.z + topSpawnerGY * dx;
                float2 pos = new float2(centerX, centerZ);

                streetEndAttractors.Add(new AttractorData(manager.numAttractors, false, scale, pos, attraction, Dir.SOUTH));
                streetEndSpawners.Add(new SpawnerData(manager.numSpawners, new float2(length, marginSize - 1), new float2(centerX, centerSpawnerZ), attraction, Dir.SOUTH, manager.numAttractors));
                manager.numAttractors++;
                manager.numSpawners++;
            }
            else gx++;
        }
    }

    // MARK: Main thread
    private void RenderTerrainGeometry()
    {
        RenderStreetPlane();
        RenderBuildingObjects();
        RenderPavementObjects();
        RenderStreetEndAttractors();
        RenderDoorAttractors();
        RenderStreetEndSpawners();
    }

    private void RenderStreetPlane()
    {
        GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
        plane.name = "Ground";
        plane.GetComponent<MeshRenderer>().sharedMaterial = manager.streetMaterial;
        plane.transform.SetParent(gameObject.transform);
        plane.transform.localPosition = Vector3.zero;
        plane.transform.localScale = new Vector3(terrainSize.x / 10f, 1f, terrainSize.y / 10f);
    }

    public Texture2D CreateTerrainGridTexture()
    {
        int width = terrainGridSize.x;
        int height = terrainGridSize.y;

        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point; // keeps pixels sharp
        texture.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = x + y * width; // index in your NativeArray
                int value = (int)terrainGrid[index].type;
                bool oob = !terrainGrid[index].inBounds;

                Color color = (value == 2 || oob) ? Color.red : (value == 0) ? Color.green : Color.yellow;

                // Flip Y-axis when setting the pixel
                texture.SetPixel(height - 1 - y, width - 1 - x, color);
            }
        }
        texture.Apply();

        return texture;
    }

    public void RenderBuildingObjects()
    {
        if (!buildings.IsCreated) return;

        for (int i = 0; i < buildings.Length; i++)
        {
            BuildingData bd = buildings[i];
            float2 c = bd.Center;

            // Create cube
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.layer = LayerMask.NameToLayer("Terrain");
            cube.transform.SetParent(gameObject.transform);

            // Size (from stored bounds) and height (precomputed)
            float width = math.abs(bd.bounds.z - bd.bounds.x);
            float depth = math.abs(bd.bounds.w - bd.bounds.y);
            float height = bd.height;
            cube.transform.localScale = new Vector3(width, height, depth);

            // Position (y = half height so cube sits on ground)
            float xCenter = (bd.bounds.x + bd.bounds.z) * 0.5f;
            float zCenter = (bd.bounds.y + bd.bounds.w) * 0.5f;
            cube.transform.position = new Vector3(xCenter, height * 0.5f, zCenter);

            // Mark as obstacle
            NavMeshObstacle obstacle = cube.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = Vector3.zero;
            obstacle.carving = true;

            // Popularity → material selection
            Material mat;
            string popStr;
            if (bd.popularity < 0.25f)
            {
                mat = manager.buildingLowMaterial;
                popStr = "Low";
            }
            else if (bd.popularity < 0.5f)
            {
                mat = manager.buildingMidMaterial;
                popStr = "Mid";
            }
            else if (bd.popularity < 0.75f)
            {
                mat = manager.buildingHighMaterial;
                popStr = "High";
            }
            else
            {
                mat = manager.buildingVHighMaterial;
                popStr = "VHigh";
            }

            cube.GetComponent<MeshRenderer>().material = mat;
            cube.name = $"Building ({popStr}) {c.x:F1}, {c.y:F1}";
        }
    }

    private void RenderPavementObjects()
    {
        for (int i = 0; i < pavements.Length; i++)
        {
            float4 data = pavements[i];

            // Create cube
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(gameObject.transform);
            cube.transform.localScale = new Vector3(data.z, 0.1f, data.w);
            cube.transform.position = new Vector3(data.x, 0.05f, data.y);
            cube.GetComponent<MeshRenderer>().material = manager.pavementMaterial;
            cube.name = $"Pavement {cube.transform.position.x:F1}, {cube.transform.position.y:F1}";
        }
    }

    public void RenderStreetEndAttractors()
    {
        if (!streetEndAttractors.IsCreated) return;

        for (int i = 0; i < streetEndAttractors.Length; i++)
        {
            manager.CreateAttractor(streetEndAttractors[i], false);
        }
    }

    public void RenderDoorAttractors()
    {
        if (!doorAttractors.IsCreated) return;

        for (int i = 0; i < doorAttractors.Length; i++)
        {
            manager.CreateAttractor(doorAttractors[i], true);
        }
    }

    public void RenderStreetEndSpawners()
    {
        if (!streetEndSpawners.IsCreated) return;

        for (int i = 0; i < streetEndSpawners.Length; i++)
        {
            manager.CreateAgentSpawner(streetEndSpawners[i]);
        }
    }

    private void BuildNavigationSurface(int widthCells, int depthCells, float navDx)
    {
        AstarData data = AstarPath.active.data;
        GridGraph gg = data.AddGraph(typeof(GridGraph)) as GridGraph;
        gg.center = new Vector3(0, 0, 0);
        gg.collision.heightCheck = false;
        gg.collision.mask |= 1 << LayerMask.NameToLayer("Terrain");
        gg.SetDimensions((int)(widthCells / navDx), (int)(depthCells / navDx), navDx);
        AstarPath.active.Scan();

        // Car grid
        gg = data.AddGraph(typeof(GridGraph)) as GridGraph;
        gg.center = new Vector3(0, 0, 0);
        gg.collision.heightCheck = false;
        gg.collision.mask |= 1 << LayerMask.NameToLayer("Terrain");
        gg.SetDimensions((int)(widthCells / navDx), (int)(depthCells / navDx), navDx);
        AstarPath.active.Scan();
        //ApplyToGridGraph();
    }

    public void ApplyToGridGraph()
    {
        var astar = AstarPath.active;
        if (astar == null) { Debug.LogError("AstarPath.active is null"); return; }
        var gg = astar.data.gridGraph;
        if (gg == null) { Debug.LogError("No GridGraph found"); return; }

        Vector3 offset = new Vector3(-(manager.terrainSize.x / 2f), 0, -(manager.terrainSize.y / 2f));

        astar.AddWorkItem((IWorkItemContext ctx) =>
        {
            // iterate over each terrain cell and find nearest graph node
            for (int i = 0; i < terrainGrid.Length; i++)
            {
                // compute world position for this terrain cell (modify to match your terrain coordinate system)
                float z = i % manager.terrainSize.x;
                float x = i / manager.terrainSize.x;
                Vector3 worldPos = offset + new Vector3(x + 0.001f, 0, z + 0.001f);  // No fucking clue don't ask

                // get nearest node
                var nn = astar.GetNearest(worldPos);
                if (nn.node == null) continue;
                var gnode = nn.node as GridNode;
                if (gnode == null) continue;

                var cd = terrainGrid[i];
                if (!cd.isWalkable)
                    gnode.Walkable = false;

                if (cd.type == Cell.PAVEMENT)
                    gnode.Penalty = (uint)1;
            }
        });
        astar.FlushWorkItems();
    }
}

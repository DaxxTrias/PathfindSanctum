using System.Collections.Generic;
using System.Linq;
using ExileCore2;

namespace PathfindSanctum;

/// <summary>
/// Handles Dijkstra Pathfinding logic for Sanctum, calculating optimal routes based on room weights.
/// </summary>
public class PathFinder(
    Graphics graphics,
    PathfindSanctumSettings settings,
    SanctumStateTracker sanctumStateTracker,
    WeightCalculator weightCalculator
)
{
    private double[,] roomWeights;
    private readonly Dictionary<(int, int), string> debugTexts = [];

    private List<(int, int)> foundBestPath;

    #region Path Calculation
    public void CreateRoomWeightMap()
    {
        var roomsByLayer = sanctumStateTracker.roomsByLayer;
        if (roomsByLayer == null || roomsByLayer.Count == 0)
        {
            roomWeights = null;
            return;
        }

        roomWeights = new double[roomsByLayer.Count, roomsByLayer.Max(x => x.Count)];

        for (var layer = 0; layer < roomsByLayer.Count; layer++)
        {
            for (var room = 0; room < roomsByLayer[layer].Count; room++)
            {
                var sanctumRoom = roomsByLayer[layer][room];
                if (sanctumRoom == null)
                    continue;

                var stateTrackerRoom = sanctumStateTracker.GetRoom(layer, room);
                var (weight, debug) = weightCalculator.CalculateRoomWeight(stateTrackerRoom);
                roomWeights[layer, room] = weight;
                debugTexts[(layer, room)] = debug;
            }
        }
    }

    public List<(int, int)> FindBestPath()
    {
        if (roomWeights == null || roomWeights.Length == 0 || sanctumStateTracker.roomLayout == null)
        {
            foundBestPath = new List<(int, int)>();
            return foundBestPath;
        }

        var roomsByLayer = sanctumStateTracker.roomsByLayer;
        if (roomsByLayer == null || roomsByLayer.Count == 0)
        {
            foundBestPath = new List<(int, int)>();
            return foundBestPath;
        }

        (int, int)? startCandidate = null;
        if (
            sanctumStateTracker.PlayerLayerIndex >= 0
            && sanctumStateTracker.PlayerRoomIndex >= 0
            && sanctumStateTracker.PlayerLayerIndex < roomsByLayer.Count
            && roomsByLayer[sanctumStateTracker.PlayerLayerIndex] != null
            && sanctumStateTracker.PlayerRoomIndex < roomsByLayer[sanctumStateTracker.PlayerLayerIndex].Count
            && sanctumStateTracker.PlayerRoomIndex < roomWeights.GetLength(1)
        )
        {
            startCandidate = (sanctumStateTracker.PlayerLayerIndex, sanctumStateTracker.PlayerRoomIndex);
        }
        else
        {
            // Fallback: first available room from the top-most layer
            for (int layer = 0; layer < roomsByLayer.Count && startCandidate == null; layer++)
            {
                var list = roomsByLayer[layer];
                if (list != null && list.Count > 0 && 0 < roomWeights.GetLength(1))
                {
                    startCandidate = (layer, 0);
                }
            }
        }

        if (startCandidate == null)
        {
            foundBestPath = new List<(int, int)>();
            return foundBestPath;
        }

        var startNode = startCandidate.Value;

        var bestPath = new Dictionary<(int, int), List<(int, int)>>
        {
            { startNode, new List<(int, int)> { startNode } }
        };
        var maxCost = new Dictionary<(int, int), double>();

        // Initialize maxCost for all valid rooms
        for (int i = 0; i < roomWeights.GetLength(0); i++)
        {
            for (int j = 0; j < roomWeights.GetLength(1); j++)
            {
                maxCost[(i, j)] = double.MinValue;
            }
        }
        maxCost[startNode] = roomWeights[startNode.Item1, startNode.Item2];

        var queue = new SortedSet<(int, int)>(
            Comparer<(int, int)>.Create(
                (a, b) =>
                {
                    double costA = maxCost[a];
                    double costB = maxCost[b];
                    if (costA != costB)
                    {
                        // Reverse comparison to prioritize higher weights
                        return costB.CompareTo(costA);
                    }
                    return a.CompareTo(b);
                }
            )
        )
        {
            startNode
        };

        while (queue.Any())
        {
            var currentRoom = queue.First();
            queue.Remove(currentRoom);

            foreach (var neighbor in GetForwardNeighbors(currentRoom, sanctumStateTracker.roomLayout))
            {
                double neighborCost =
                    maxCost[currentRoom] + roomWeights[neighbor.Item1, neighbor.Item2];

                if (neighborCost > maxCost[neighbor])
                {
                    queue.Remove(neighbor);
                    maxCost[neighbor] = neighborCost;
                    queue.Add(neighbor);
                    bestPath[neighbor] = new List<(int, int)>(bestPath[currentRoom]) { neighbor };
                }
            }
        }

        var groupedPaths = bestPath.GroupBy(pair => pair.Value.Count());
        var maxCountGroup = groupedPaths.OrderByDescending(group => group.Key).FirstOrDefault();
        var path = maxCountGroup
            ?.OrderByDescending(pair => maxCost.GetValueOrDefault(pair.Key, double.MinValue))
            .FirstOrDefault()
            .Value;

        foundBestPath = path ?? new List<(int, int)>();
        return foundBestPath;
    }

    private static IEnumerable<(int, int)> GetForwardNeighbors(
        (int, int) currentRoom,
        byte[][][] connections
    )
    {
        int currentLayerIndex = currentRoom.Item1;
        int currentRoomIndex = currentRoom.Item2;
        int nextLayerIndex = currentLayerIndex + 1;

        if (connections == null)
        {
            yield break;
        }

        // Bounds check for current layer
        if (currentLayerIndex < 0 || currentLayerIndex >= connections.Length)
        {
            yield break;
        }

        byte[][] currentLayer = connections[currentLayerIndex];
        if (currentLayer == null)
        {
            yield break;
        }

        if (currentRoomIndex < 0 || currentRoomIndex >= currentLayer.Length)
        {
            yield break;
        }

        byte[] forwardTargets = currentLayer[currentRoomIndex];
        if (forwardTargets == null || forwardTargets.Length == 0)
        {
            yield break;
        }

        foreach (var nextIndex in forwardTargets)
        {
            yield return (nextLayerIndex, nextIndex);
        }
    }
    #endregion

    #region Visualization
    public void DrawDebugInfo()
    {
        if (!settings.DebugSettings.DebugEnable.Value)
            return;

        var roomsByLayer = sanctumStateTracker.roomsByLayer;

        for (var layer = 0; layer < roomsByLayer.Count; layer++)
        {
            for (var room = 0; room < roomsByLayer[layer].Count; room++)
            {
                var sanctumRoom = sanctumStateTracker.GetRoom(layer, room);
                if (sanctumRoom == null)
                    continue;

                var pos = sanctumRoom.Position;

                var debugText = debugTexts.TryGetValue((layer, room), out var text)
                    ? text
                    : string.Empty;
                var displayText = $"Weight: {roomWeights[layer, room]:F0}\n{debugText}";

                using (graphics.SetTextScale(settings.DebugSettings.DebugFontSizeMultiplier))
                {
                    graphics.DrawTextWithBackground(
                        displayText,
                        pos,
                        settings.StyleSettings.TextColor,
                        settings.StyleSettings.BackgroundColor
                    );
                }
            }
        }
    }

    public void DrawBestPath()
    {
        if (this.foundBestPath == null)
            return;

        var roomsByLayer = sanctumStateTracker.roomsByLayer;
        if (roomsByLayer == null)
            return;

        foreach (var room in this.foundBestPath)
        {
            if (
                room.Item1 == sanctumStateTracker.PlayerLayerIndex
                && room.Item2 == sanctumStateTracker.PlayerRoomIndex
            )
                continue;

            if (room.Item1 < 0 || room.Item1 >= roomsByLayer.Count)
                continue;
            var layerRooms = roomsByLayer[room.Item1];
            if (layerRooms == null || room.Item2 < 0 || room.Item2 >= layerRooms.Count)
                continue;

            var sanctumRoom = layerRooms[room.Item2];

            graphics.DrawFrame(
                sanctumRoom.GetClientRect(),
                settings.StyleSettings.BestPathColor,
                settings.StyleSettings.FrameThickness
            );
        }
    }
    #endregion

    #region Validation
    public string ValidateLayoutOrNull()
    {
        var roomsByLayer = sanctumStateTracker.roomsByLayer;
        var layout = sanctumStateTracker.roomLayout;
        if (roomsByLayer == null || layout == null)
            return "No Sanctum layout available.";

        // We expect that for each layer L, layout[L] has an entry per room in that layer.
        var layers = roomsByLayer.Count;
        if (layout.Length < layers)
            return $"Layout layer count {layout.Length} < rooms {layers}.";

        for (int layer = 0; layer < layers; layer++)
        {
            var layerRooms = roomsByLayer[layer];
            var layoutLayer = layout[layer];
            if (layerRooms == null)
                return $"Layer {layer} has null room list.";
            if (layoutLayer == null)
                return $"Layout for layer {layer} is null.";
            if (layoutLayer.Length < layerRooms.Count)
                return $"Layout rooms {layoutLayer.Length} < UI rooms {layerRooms.Count} at layer {layer}.";

            // Forward edges point to next layer
            if (layer + 1 < layers)
            {
                int nextLayerRooms = roomsByLayer[layer + 1]?.Count ?? 0;
                for (int room = 0; room < layerRooms.Count; room++)
                {
                    var edges = layoutLayer[room];
                    if (edges == null) continue;
                    foreach (var target in edges)
                    {
                        if (target >= nextLayerRooms)
                            return $"Invalid edge {layer}:{room}->{layer + 1}:{target} (next layer has {nextLayerRooms} rooms).";
                    }
                }
            }
        }
        return null;
    }
    #endregion
}

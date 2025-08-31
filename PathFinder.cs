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
            for (int layer = roomsByLayer.Count - 1; layer >= 0 && startCandidate == null; layer--)
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

        int numLayers = sanctumStateTracker.roomLayout.Length;

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

            foreach (var neighbor in GetNeighbors(currentRoom, sanctumStateTracker.roomLayout))
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

        if (sanctumStateTracker.PlayerLayerIndex != -1 && sanctumStateTracker.PlayerRoomIndex != -1)
        {
            path = bestPath.TryGetValue(
                (sanctumStateTracker.PlayerLayerIndex, sanctumStateTracker.PlayerRoomIndex),
                out var specificPath
            )
                ? specificPath
                : new List<(int, int)>();
        }

        foundBestPath = path ?? new List<(int, int)>();
        return foundBestPath;
    }

    private static IEnumerable<(int, int)> GetNeighbors(
        (int, int) currentRoom,
        byte[][][] connections
    )
    {
        int currentLayerIndex = currentRoom.Item1;
        int currentRoomIndex = currentRoom.Item2;
        int previousLayerIndex = currentLayerIndex - 1;

        if (connections == null || currentLayerIndex <= 0)
        {
            yield break; // brak sąsiadów
        }

        // ✅ zabezpieczenie granic
        if (previousLayerIndex < 0 || previousLayerIndex >= connections.Length)
        {
            yield break;
        }

        byte[][] previousLayer = connections[previousLayerIndex];
        if (previousLayer == null)
        {
            yield break;
        }

        for (int previousLayerRoomIndex = 0; previousLayerRoomIndex < previousLayer.Length; previousLayerRoomIndex++)
        {
            var previousLayerRoom = previousLayer[previousLayerRoomIndex];
            if (previousLayerRoom == null)
            {
                continue;
            }

            if (previousLayerRoom.Contains((byte)currentRoomIndex))
            {
                yield return (previousLayerIndex, previousLayerRoomIndex);
            }
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

            var sanctumRoom = layerRooms[room.Item1 == sanctumStateTracker.PlayerLayerIndex && room.Item2 == sanctumStateTracker.PlayerRoomIndex ? sanctumStateTracker.PlayerRoomIndex : room.Item2];

            graphics.DrawFrame(
                sanctumRoom.GetClientRect(),
                settings.StyleSettings.BestPathColor,
                settings.StyleSettings.FrameThickness
            );
        }
    }
    #endregion
}

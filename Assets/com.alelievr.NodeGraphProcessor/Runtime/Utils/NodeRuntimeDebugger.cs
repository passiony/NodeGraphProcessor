using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraphProcessor
{
    public enum NodeRunStatus
    {
        None,
        Running,
        Completed,
        Failed
    }

    /// <summary>
    /// Static hub to synchronize runtime execution state with the Graph Editor.
    /// </summary>
    public static class NodeRuntimeDebugger
    {
        private static Dictionary<int, Dictionary<string, NodeRunStatus>> nodeStatuses = new Dictionary<int, Dictionary<string, NodeRunStatus>>();

        public static event Action<BaseGraph, string, NodeRunStatus> onNodeStatusChanged;

        public static void UpdateNodeStatus(BaseGraph graph, string nodeGuid, NodeRunStatus status)
        {
            if (graph == null || string.IsNullOrEmpty(nodeGuid)) return;

            int graphId = graph.GetInstanceID();
            if (!nodeStatuses.ContainsKey(graphId))
                nodeStatuses[graphId] = new Dictionary<string, NodeRunStatus>();

            nodeStatuses[graphId][nodeGuid] = status;
            onNodeStatusChanged?.Invoke(graph, nodeGuid, status);
        }

        public static NodeRunStatus GetNodeStatus(BaseGraph graph, string nodeGuid)
        {
            if (graph == null) return NodeRunStatus.None;
            int graphId = graph.GetInstanceID();
            if (nodeStatuses.TryGetValue(graphId, out var statuses))
            {
                if (statuses.TryGetValue(nodeGuid, out var status))
                    return status;
            }
            return NodeRunStatus.None;
        }

        public static void Clear(BaseGraph graph)
        {
            if (graph == null) return;
            
            int graphId = graph.GetInstanceID();
            if (nodeStatuses.TryGetValue(graphId, out var statuses))
            {
                // Notify everyone to clear status before removing the dictionary
                foreach (var nodeGuid in statuses.Keys)
                {
                    onNodeStatusChanged?.Invoke(graph, nodeGuid, NodeRunStatus.None);
                }
                nodeStatuses.Remove(graphId);
            }
        }
    }
}

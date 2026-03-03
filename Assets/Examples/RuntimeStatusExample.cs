using UnityEngine;
using GraphProcessor;
using System.Collections;
using System.Linq;

/// <summary>
/// Example script to demonstrate how to update node status from runtime.
/// Add this to a GameObject and assign your Graph asset.
/// </summary>
public class RuntimeStatusExample : GraphBehaviour
{
    private Coroutine simulation;

    void Start()
    {
        if (Application.isPlaying && graph != null)
        {
            simulation = StartCoroutine(SimulateTaskFlow());
        }
    }

    IEnumerator SimulateTaskFlow()
    {
        Debug.Log("Starting Task Flow Simulation...");
        
        // Reset all nodes
        foreach (var node in graph.nodes)
        {
            NodeRuntimeDebugger.UpdateNodeStatus(graph, node.GUID, NodeRunStatus.None);
        }

        // Simple sequential simulation
        var nodes = graph.nodes.OrderBy(n => n.computeOrder).ToList();
        
        foreach (var node in nodes)
        {
            // Mark as running
            Debug.Log($"Running Node: {node.GetType().Name} ({node.GUID})");
            NodeRuntimeDebugger.UpdateNodeStatus(graph, node.GUID, NodeRunStatus.Running);
            
            yield return new WaitForSeconds(3.0f);
            
            // Mark as completed
            NodeRuntimeDebugger.UpdateNodeStatus(graph, node.GUID, NodeRunStatus.Completed);
        }

        Debug.Log("Task Flow Simulation Finished.");
    }

    void OnDisable()
    {
        if (graph != null)
            NodeRuntimeDebugger.Clear(graph);
    }
}

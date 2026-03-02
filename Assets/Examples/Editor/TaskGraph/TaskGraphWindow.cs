using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using GraphProcessor;

public class TaskGraphWindow : BaseGraphWindow
{
    BaseGraph tmpGraph;
    CustomToolbarView toolbarView;

    [MenuItem("Window/06 Task Graph")]
    public static BaseGraphWindow OpenWithTmpGraph()
    {
        var graphWindow = CreateWindow<TaskGraphWindow>();

        // When the graph is opened from the window, we don't save the graph to disk
        graphWindow.tmpGraph = ScriptableObject.CreateInstance<BaseGraph>();
        graphWindow.tmpGraph.hideFlags = HideFlags.HideAndDontSave;
        graphWindow.InitializeGraph(graphWindow.tmpGraph);

        graphWindow.Show();

        return graphWindow;
    }

    protected override void OnDestroy()
    {
        graphView?.Dispose();
        DestroyImmediate(tmpGraph);
    }

    protected override void InitializeWindow(BaseGraph graph)
    {
        titleContent = new GUIContent("All Graph");

        if (graphView == null)
        {
            graphView = new TaskGraphView(this);
            toolbarView = new CustomToolbarView(graphView);
            graphView.Add(toolbarView);
        }

        rootView.Add(graphView);
    }

    protected override void InitializeGraphView(BaseGraphView view)
    {
        // graphView.OpenPinned< ExposedParameterView >();
        // toolbarView.UpdateButtonStatus();
    }
}
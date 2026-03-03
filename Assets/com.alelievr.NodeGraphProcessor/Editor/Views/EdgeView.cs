using UnityEditor.Experimental.GraphView;
using UnityEngine.UIElements;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace GraphProcessor
{
	public class EdgeView : Edge
	{
		public bool					isConnected = false;

		public SerializableEdge		serializedEdge { get { return userData as SerializableEdge; } }

		readonly string				edgeStyle = "GraphProcessorStyles/EdgeView";
		static StyleSheet			cachedEdgeStyle;

		protected BaseGraphView		owner => ((input ?? output) as PortView).owner.owner;

		// Animation - Multiple dots for a richer effect
		List<VisualElement> 		flowDots = new List<VisualElement>();
		const int 					k_DotCount = 3; 
		IVisualElementScheduledItem animationItem;

		public EdgeView() : base()
		{
			if (cachedEdgeStyle == null)
				cachedEdgeStyle = Resources.Load<StyleSheet>(edgeStyle);
			styleSheets.Add(cachedEdgeStyle);

			RegisterCallback<MouseDownEvent>(OnMouseDown);

			// Setup multiple flow dots
			for (int i = 0; i < k_DotCount; i++)
			{
				var dot = new VisualElement();
				dot.AddToClassList("flow-dot");
				dot.style.visibility = Visibility.Hidden;
				dot.pickingMode = PickingMode.Ignore; // Ensure dots don't block mouse
				Add(dot);
				flowDots.Add(dot);
			}

			animationItem = schedule.Execute(UpdateAnimation).Every(16);
		}

		void UpdateAnimation()
		{
			if (output == null || input == null) return;

			var outputNodeView = output.node as BaseNodeView;
			if (outputNodeView == null || outputNodeView.nodeTarget == null) return;

			var status = NodeRuntimeDebugger.GetNodeStatus(owner.graph, outputNodeView.nodeTarget.GUID);
			
			if (status == NodeRunStatus.Running)
			{
				float baseT = (float)(EditorApplication.timeSinceStartup % 1.0); // 1 second loop
				
				// Edge control points
				Vector2 start = edgeControl.from;
				Vector2 end = edgeControl.to;
				float ctrlDist = Mathf.Max(Mathf.Abs(end.x - start.x) / 2, 30);
				Vector2 p1 = start + new Vector2(ctrlDist, 0);
				Vector2 p2 = end - new Vector2(ctrlDist, 0);

				for (int i = 0; i < k_DotCount; i++)
				{
					var dot = flowDots[i];
					dot.style.visibility = Visibility.Visible;

					// Offset each dot by 0.33 of the loop
					float t = (baseT + (i * 1.0f / k_DotCount)) % 1.0f;
					
					// Cubic Bezier formula
					float u = 1 - t;
					Vector2 pos = u*u*u*start + 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t*end;

					dot.transform.position = pos - new Vector2(4, 4);
					
					// Fade in/out at ends for smoother look
					float opacity = 1.0f;
					if (t < 0.1f) opacity = t * 10f;
					else if (t > 0.9f) opacity = (1f - t) * 10f;
					dot.style.opacity = opacity;
				}
			}
			else
			{
				foreach (var dot in flowDots)
					dot.style.visibility = Visibility.Hidden;
			}
		}

        public override void OnPortChanged(bool isInput)
		{
			base.OnPortChanged(isInput);
			UpdateEdgeSize();
		}

		public void UpdateEdgeSize()
		{
			if (input == null && output == null)
				return;

			PortData inputPortData = (input as PortView)?.portData;
			PortData outputPortData = (output as PortView)?.portData;

			for (int i = 1; i < 20; i++)
				RemoveFromClassList($"edge_{i}");
			int maxPortSize = Mathf.Max(inputPortData?.sizeInPixel ?? 0, outputPortData?.sizeInPixel ?? 0);
			if (maxPortSize > 0)
				AddToClassList($"edge_{Mathf.Max(1, maxPortSize - 6)}");
		}

		protected override void OnCustomStyleResolved(ICustomStyle styles)
		{
			base.OnCustomStyleResolved(styles);

			UpdateEdgeControl();
		}

		void OnMouseDown(MouseDownEvent e)
		{
			if (e.clickCount == 2)
			{
				// Empirical offset:
				var position = e.mousePosition;
                position += new Vector2(-10f, -28);
                Vector2 mousePos = owner.ChangeCoordinatesTo(owner.contentViewContainer, position);

				owner.AddRelayNode(input as PortView, output as PortView, mousePos);
			}
		}
	}
}

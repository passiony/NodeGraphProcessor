using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Experimental.GraphView;
using UnityEngine.UIElements;
using UnityEditor;
using System.Reflection;
using System;
using System.Collections;
using System.Linq;
using UnityEditor.UIElements;
using System.Text.RegularExpressions;
using Sirenix.OdinInspector;
using Status = UnityEngine.UIElements.DropdownMenuAction.Status;
using NodeView = UnityEditor.Experimental.GraphView.Node;

namespace GraphProcessor
{
	[BoxGroup]
	[HideReferenceObjectPicker]
	[NodeCustomEditor(typeof(BaseNode))]
	public class BaseNodeView : NodeView
	{
		public BaseNode							nodeTarget;

		[HideInInspector]public List< PortView >					inputPortViews = new List< PortView >();
		[HideInInspector]public List< PortView >					outputPortViews = new List< PortView >();

		public BaseGraphView					owner { private set; get; }

		protected Dictionary< string, List< PortView > > portsPerFieldName = new Dictionary< string, List< PortView > >();

		[HideInInspector]public VisualElement 					controlsContainer;
		protected VisualElement					rightTitleContainer;
		private VisualElement 					inputContainerElement;

		TextField								titleTextField;

		public event Action< PortView >			onPortConnected;
		public event Action< PortView >			onPortDisconnected;

		protected virtual bool					hasSettings { get; set; }

		[HideInInspector]public bool								initializing = false; //Used for applying SetPosition on locked node at init.
		private bool								controlsLoaded = false;

        readonly string							baseNodeStyle = "GraphProcessorStyles/BaseNodeView";
		static StyleSheet						cachedBaseNodeStyle;
		static Dictionary<string, StyleSheet>	layoutStyleCache = new Dictionary<string, StyleSheet>();

		VisualElement							settings;
		NodeSettingsView						settingsContainer;
		Button									settingButton;
		bool									settingsExpanded = false;

		struct InspectorFieldInfo
		{
			public FieldInfo field;
			public string displayName;
			public bool hasInputAttribute;
			public bool hasOutputAttribute;
			public bool isSerializeField;
			public bool isNotSerialized;
			public bool showAsDrawer;
			public bool isVisibleIf;
			public VisibleIf visibleIf;
			public ShowInInspector showInInspector;
		}

		static Dictionary<Type, List<InspectorFieldInfo>> inspectorFieldsCache = new Dictionary<Type, List<InspectorFieldInfo>>();
		static Dictionary<Type, List<FieldInfo>> settingFieldsCache = new Dictionary<Type, List<FieldInfo>>();

		// Performance optimization: Cache PropertyFields to avoid tree queries
		private List<PropertyField> 			cachedPropertyFields = new List<PropertyField>();

		[System.NonSerialized]
		List< IconBadge >						badges = new List< IconBadge >();

		#region  Initialization
		
		public void Initialize(BaseGraphView owner, BaseNode node, bool skipRefresh = false)
		{
			nodeTarget = node;
			this.owner = owner;

			if (node == null) return;

			if (!node.deletable)
				capabilities &= ~Capabilities.Deletable;
			if (node.isRenamable)
				capabilities |= Capabilities.Renamable;

			node.onMessageAdded += AddMessageView;
			node.onMessageRemoved += RemoveMessageView;
			node.onPortsUpdated += a => schedule.Execute(_ => UpdatePortsForField(a)).ExecuteLater(0);
			NodeRuntimeDebugger.onNodeStatusChanged += HandleNodeStatusChanged;

			if (cachedBaseNodeStyle == null)
				cachedBaseNodeStyle = Resources.Load<StyleSheet>(baseNodeStyle);
            if (cachedBaseNodeStyle != null)
				styleSheets.Add(cachedBaseNodeStyle);

            if (!string.IsNullOrEmpty(node.layoutStyle))
			{
				if (!layoutStyleCache.TryGetValue(node.layoutStyle, out var layoutStyle))
				{
					layoutStyle = Resources.Load<StyleSheet>(node.layoutStyle);
					layoutStyleCache[node.layoutStyle] = layoutStyle;
				}

				if (layoutStyle != null)
					styleSheets.Add(layoutStyle);
			}

			InitializeView();
			InitializePorts();

			if (nodeTarget.expanded)
			{
				if (GetType().GetMethod(nameof(Enable), new Type[] { }).DeclaringType != typeof(BaseNodeView))
					ExceptionToLog.Call(() => Enable());
				else
					ExceptionToLog.Call(() => Enable(false));
			}

			RefreshExpandedState();

			if (!skipRefresh)
				this.RefreshPorts();

			RegisterCallback<DetachFromPanelEvent>(e => ExceptionToLog.Call(Disable));
			OnGeometryChanged(null);
		}

		void InitializePorts()
		{
			if (owner == null || nodeTarget == null) return;
			var listener = owner.connectorListener;

			foreach (var inputPort in nodeTarget.inputPorts)
			{
				AddPort(inputPort.fieldInfo, Direction.Input, listener, inputPort.portData);
			}

			foreach (var outputPort in nodeTarget.outputPorts)
			{
				AddPort(outputPort.fieldInfo, Direction.Output, listener, outputPort.portData);
			}
		}

		void InitializeView()
		{
            controlsContainer = new VisualElement{ name = "controls" };
			controlsContainer.AddToClassList("NodeControls");
			mainContainer.Add(controlsContainer);

			rightTitleContainer = new VisualElement{ name = "RightTitleContainer" };
			titleContainer.Add(rightTitleContainer);

			if (nodeTarget.showControlsOnHover)
			{
				bool mouseOverControls = false;
				controlsContainer.style.display = DisplayStyle.None;
				RegisterCallback<MouseOverEvent>(e => {
					if (controlsContainer != null) controlsContainer.style.display = DisplayStyle.Flex;
					mouseOverControls = true;
				});
				RegisterCallback<MouseOutEvent>(e => {
					var rect = GetPosition();
					var graphMousePosition = owner.contentViewContainer.WorldToLocal(e.mousePosition);
					if (rect.Contains(graphMousePosition) || !nodeTarget.showControlsOnHover)
						return;
					mouseOverControls = false;
					schedule.Execute(_ => {
						if (!mouseOverControls && controlsContainer != null)
							controlsContainer.style.display = DisplayStyle.None;
					}).ExecuteLater(500);
				});
			}

			Undo.undoRedoPerformed += UpdateFieldValues;

			initializing = true;

			UpdateTitle();
            SetPosition(nodeTarget.position);
			SetNodeColor(nodeTarget.color);
            
			AddInputContainer();

			if ((capabilities & Capabilities.Renamable) != 0)
				SetupRenamableTitle();
		}

		void SetupRenamableTitle()
		{
			var titleLabel = this.Q("title-label") as Label;
			if (titleLabel == null) return;

			titleTextField = new TextField{ isDelayed = true };
			titleTextField.style.display = DisplayStyle.None;
			titleLabel.parent.Insert(0, titleTextField);

			titleLabel.RegisterCallback<MouseDownEvent>(e => {
				if (e.clickCount == 2 && e.button == (int)MouseButton.LeftMouse)
					OpenTitleEditor();
			});

			titleTextField.RegisterValueChangedCallback(e => CloseAndSaveTitleEditor(e.newValue));

			titleTextField.RegisterCallback<MouseDownEvent>(e => {
				if (e.clickCount == 2 && e.button == (int)MouseButton.LeftMouse)
					CloseAndSaveTitleEditor(titleTextField.value);
			});

			titleTextField.RegisterCallback<FocusOutEvent>(e => CloseAndSaveTitleEditor(titleTextField.value));

			void OpenTitleEditor()
			{
				titleTextField.style.display = DisplayStyle.Flex;
				titleLabel.style.display = DisplayStyle.None;
				titleTextField.focusable = true;

				titleTextField.SetValueWithoutNotify(title);
				titleTextField.Focus();
				titleTextField.SelectAll();
			}

			void CloseAndSaveTitleEditor(string newTitle)
			{
				if (owner == null || nodeTarget == null) return;
				owner.RegisterCompleteObjectUndo("Renamed node " + newTitle);
				nodeTarget.SetCustomName(newTitle);

				titleTextField.style.display = DisplayStyle.None;
				titleLabel.style.display = DisplayStyle.Flex;
				titleTextField.focusable = false;

				UpdateTitle();
			}
		}

		void UpdateTitle()
		{
			if (nodeTarget == null) return;
			title = (nodeTarget.GetCustomName() == null) ? nodeTarget.GetType().Name : nodeTarget.GetCustomName();
		}

		void OnGeometryChanged(GeometryChangedEvent evt)
		{
		}

		VisualElement selectionBorder, nodeBorder;
		internal void EnableSyncSelectionBorderHeight()
		{
			if (selectionBorder == null || nodeBorder == null)
			{
				selectionBorder = this.Q("selection-border");
				nodeBorder = this.Q("node-border");

				if (selectionBorder != null && nodeBorder != null)
				{
					nodeBorder.RegisterCallback<GeometryChangedEvent>(evt => {
						if (selectionBorder != null && nodeBorder != null)
							selectionBorder.style.height = nodeBorder.localBound.height;
					});
					selectionBorder.style.height = nodeBorder.localBound.height;
				}
			}
		}
		
		public void OpenSettings() {}
		public void CloseSettings() {}

		#endregion

		#region API

		public List< PortView > GetPortViewsFromFieldName(string fieldName)
		{
			List< PortView >	ret;
			portsPerFieldName.TryGetValue(fieldName, out ret);
			return ret;
		}

		public PortView GetFirstPortViewFromFieldName(string fieldName)
		{
			return GetPortViewsFromFieldName(fieldName)?.First();
		}

		public PortView GetPortViewFromFieldName(string fieldName, string identifier)
		{
			return GetPortViewsFromFieldName(fieldName)?.FirstOrDefault(pv => {
				return (pv.portData.identifier == identifier) || (String.IsNullOrEmpty(pv.portData.identifier) && String.IsNullOrEmpty(identifier));
			});
		}


		public PortView AddPort(FieldInfo fieldInfo, Direction direction, BaseEdgeConnectorListener listener, PortData portData)
		{
			PortView p = CreatePortView(direction, fieldInfo, portData, listener);
			if (p == null) return null;

			if (p.direction == Direction.Input)
			{
				inputPortViews.Add(p);
				inputContainer.Add(p);
			}
			else
			{
				outputPortViews.Add(p);
				outputContainer.Add(p);
			}

			p.Initialize(this, portData?.displayName);

			if (!portsPerFieldName.TryGetValue(p.fieldName, out var ports))
			{
				ports = new List< PortView >();
				portsPerFieldName[p.fieldName] = ports;
			}
			ports.Add(p);

			return p;
		}

        protected virtual PortView CreatePortView(Direction direction, FieldInfo fieldInfo, PortData portData, BaseEdgeConnectorListener listener)
        	=> PortView.CreatePortView(direction, fieldInfo, portData, listener);

        public void InsertPort(PortView portView, int index)
		{
			if (portView == null) return;
			if (portView.direction == Direction.Input)
				inputContainer.Insert(index, portView);
			else
				outputContainer.Insert(index, portView);
		}

		public void RemovePort(PortView p)
		{
			if (p == null) return;
			var edgesCopy = p.GetEdges().ToList();
			foreach (var e in edgesCopy)
				owner.Disconnect(e, refreshPorts: false);

			if (p.direction == Direction.Input)
			{
				if (inputPortViews.Remove(p))
					p.RemoveFromHierarchy();
			}
			else
			{
				if (outputPortViews.Remove(p))
					p.RemoveFromHierarchy();
			}

			if (portsPerFieldName.TryGetValue(p.fieldName, out var ports))
				ports.Remove(p);
		}
		
		public static Rect GetNodeRect(Node node, float left = int.MaxValue, float top = int.MaxValue)
		{
			return new Rect(
				new Vector2(left != int.MaxValue ? left : node.style.left.value.value, top != int.MaxValue ? top : node.style.top.value.value),
				new Vector2(node.style.width.value.value, node.style.height.value.value)
			);
		}

		public void AlignFlowSelectedNodes()
		{
			if (owner == null) return;
			var initialSelection = owner.selection.OfType<BaseNodeView>().ToList();
			if (initialSelection.Count == 0) return;

			owner.RegisterCompleteObjectUndo("Align Flow Tree");

			List<BaseNodeView> GetChildren(BaseNodeView node)
			{
				return node.outputPortViews
					.SelectMany(p => p.GetEdges())
					.Select(e => e.input.node as BaseNodeView)
					.Where(n => n != null)
					.Distinct()
					.OrderBy(n => n.GetPosition().y) 
					.ToList();
			}

			HashSet<BaseNodeView> involvedNodes = new HashSet<BaseNodeView>();
			void CollectDownstream(BaseNodeView node)
			{
				if (involvedNodes.Contains(node)) return;
				involvedNodes.Add(node);
				foreach (var child in GetChildren(node)) CollectDownstream(child);
			}
			foreach (var selected in initialSelection) CollectDownstream(selected);

			var roots = involvedNodes.Where(n => 
				!n.inputPortViews.Any(p => p.GetEdges().Any(e => involvedNodes.Contains(e.output.node as BaseNodeView)))
			).OrderBy(n => n.GetPosition().y).ToList();

			float verticalSpacing = 30f;
			float horizontalSpacing = 320f; 

			Dictionary<BaseNodeView, float> subtreeHeights = new Dictionary<BaseNodeView, float>();
			float GetSubtreeHeight(BaseNodeView node, HashSet<BaseNodeView> visited)
			{
				if (visited.Contains(node)) return subtreeHeights.ContainsKey(node) ? subtreeHeights[node] : 0;
				visited.Add(node);

				var children = GetChildren(node).Where(involvedNodes.Contains).ToList();
				float nodeHeight = node.GetPosition().height;

				if (children.Count == 0)
				{
					subtreeHeights[node] = nodeHeight;
					return nodeHeight;
				}

				float childrenTotalHeight = 0;
				foreach (var child in children)
					childrenTotalHeight += GetSubtreeHeight(child, visited) + verticalSpacing;
				
				childrenTotalHeight -= verticalSpacing; 

				float finalHeight = Mathf.Max(nodeHeight, childrenTotalHeight);
				subtreeHeights[node] = finalHeight;
				return finalHeight;
			}

			HashSet<BaseNodeView> h1 = new HashSet<BaseNodeView>();
			foreach (var root in roots) GetSubtreeHeight(root, h1);

			HashSet<BaseNodeView> h2 = new HashSet<BaseNodeView>();
			void ApplyLayout(BaseNodeView node, float x, float yCenter, HashSet<BaseNodeView> visited)
			{
				if (visited.Contains(node)) return;
				visited.Add(node);

				Rect rect = node.GetPosition();
				node.SetPosition(new Rect(x, yCenter - rect.height / 2f, rect.width, rect.height));

				var children = GetChildren(node).Where(involvedNodes.Contains).ToList();
				if (children.Count == 0) return;

				float totalSpaceForChildren = 0;
				foreach (var child in children) totalSpaceForChildren += subtreeHeights[child] + verticalSpacing;
				totalSpaceForChildren -= verticalSpacing;

				float currentChildTopY = yCenter - totalSpaceForChildren / 2f;

				foreach (var child in children)
				{
					float childSubtreeHeight = subtreeHeights[child];
					float childCenterY = currentChildTopY + childSubtreeHeight / 2f;
					ApplyLayout(child, x + horizontalSpacing, childCenterY, visited);
					currentChildTopY += childSubtreeHeight + verticalSpacing;
				}
			}

			foreach (var root in roots)
			{
				ApplyLayout(root, root.GetPosition().x, root.GetPosition().center.y, h2);
			}
		}

		public void OpenNodeViewScript()
		{
			var script = NodeProvider.GetNodeViewScript(GetType());
			if (script != null)
				AssetDatabase.OpenAsset(script.GetInstanceID(), 0, 0);
		}

		public void OpenNodeScript()
		{
			if (nodeTarget == null) return;
			var script = NodeProvider.GetNodeScript(nodeTarget.GetType());
			if (script != null)
				AssetDatabase.OpenAsset(script.GetInstanceID(), 0, 0);
		}

		public void ToggleDebug() {}
		public void UpdateDebugView() {}

		public void AddMessageView(string message, Texture icon, Color color)
			=> AddBadge(new NodeBadgeView(message, icon, color));

		public void AddMessageView(string message, NodeMessageType messageType)
		{
			IconBadge	badge = null;
			switch (messageType)
			{
				case NodeMessageType.Warning:
					badge = new NodeBadgeView(message, EditorGUIUtility.IconContent("Collab.Warning").image, Color.yellow);
					break ;
				case NodeMessageType.Error:	
					badge = IconBadge.CreateError(message);
					break ;
				case NodeMessageType.Info:
					badge = IconBadge.CreateComment(message);
					break ;
				default:
				case NodeMessageType.None:
					badge = new NodeBadgeView(message, null, Color.grey);
					break ;
			}
			
			AddBadge(badge);
		}

		void AddBadge(IconBadge badge)
		{
			if (badge == null) return;
			Add(badge);
			badges.Add(badge);
			badge.AttachTo(topContainer, SpriteAlignment.TopRight);
		}

		void RemoveBadge(Func<IconBadge, bool> callback)
		{
			badges.RemoveAll(b => {
				if (callback(b))
				{
					b.Detach();
					b.RemoveFromHierarchy();
					return true;
				}
				return false;
			});
		}

		public void RemoveMessageViewContains(string message) => RemoveBadge(b => b.badgeText.Contains(message));
		public void RemoveMessageView(string message) => RemoveBadge(b => b.badgeText == message);

		public void Highlight() => AddToClassList("Highlight");
		public void UnHighlight() => RemoveFromClassList("Highlight");

		void HandleNodeStatusChanged(BaseGraph graph, string nodeGuid, NodeRunStatus status)
		{
			if (nodeTarget == null || graph != owner.graph || nodeTarget.GUID != nodeGuid)
				return;
			
			UpdateStatus(status);
		}

		private IVisualElementScheduledItem pulseSchedule;
		private float pulseTimer = 0;

		public virtual void UpdateStatus(NodeRunStatus status)
		{
			if (pulseSchedule != null)
			{
				pulseSchedule.Pause();
				RemoveFromClassList("running");
				pulseTimer = 0;
			}

			switch (status)
			{
				case NodeRunStatus.Running:
					if (pulseSchedule == null)
					{
						// Check every 100ms for more precise timing
						pulseSchedule = schedule.Execute(() => {
							pulseTimer += 0.1f;
							bool isBright = this.ClassListContains("running");

							if (isBright && pulseTimer >= 1.5f) // Stay bright for 1.5 seconds
							{
								RemoveFromClassList("running");
								pulseTimer = 0;
							}
							else if (!isBright && pulseTimer >= 0.5f) // Stay dim for only 0.5 seconds
							{
								AddToClassList("running");
								pulseTimer = 0;
							}
						}).Every(100);
					}
					else
					{
						pulseSchedule.Resume();
					}
					AddToClassList("running");
					pulseTimer = 0;
					break;
				default:
					RemoveFromClassList("running");
					break;
			}
		}

		#endregion

		#region Callbacks & Overrides

		public virtual void Enable(bool fromInspector = false) => DrawDefaultInspector(fromInspector);
		public virtual void Enable() => DrawDefaultInspector(false);

		public virtual void Disable()
		{
			Undo.undoRedoPerformed -= UpdateFieldValues;
			NodeRuntimeDebugger.onNodeStatusChanged -= HandleNodeStatusChanged;

			if (nodeTarget != null)
			{
				nodeTarget.onMessageAdded -= AddMessageView;
				nodeTarget.onMessageRemoved -= RemoveMessageView;
			}

			fieldControlsMap.Clear();
			visibleConditions.Clear();
			hideElementIfConnected.Clear();
			cachedPropertyFields.Clear();
		}

		Dictionary<string, List<(object value, VisualElement target)>> visibleConditions = new Dictionary<string, List<(object value, VisualElement target)>>();
		Dictionary<string, VisualElement>  hideElementIfConnected = new Dictionary<string, VisualElement>();
		Dictionary<FieldInfo, List<VisualElement>> fieldControlsMap = new Dictionary<FieldInfo, List<VisualElement>>();

		protected void AddInputContainer()
		{
			inputContainerElement = new VisualElement {name = "input-container"};
			if (mainContainer != null && mainContainer.parent != null)
			{
				mainContainer.parent.Add(inputContainerElement);
				inputContainerElement.SendToBack();
				inputContainerElement.pickingMode = PickingMode.Ignore;
			}
		}

		protected virtual void DrawDefaultInspector(bool fromInspector = false)
		{
			if (controlsLoaded && !fromInspector) return;
			if (nodeTarget == null) return;

			Type nodeType = nodeTarget.GetType();

			if (!inspectorFieldsCache.TryGetValue(nodeType, out var fieldsInfo))
			{
				fieldsInfo = new List<InspectorFieldInfo>();
				var fields = nodeType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
					.Where(f => f.DeclaringType != typeof(BaseNode));

				foreach (var field in nodeTarget.OverrideFieldOrder(fields).Reverse())
				{
					var info = new InspectorFieldInfo();
					info.field = field;
					info.displayName = ObjectNames.NicifyVariableName(field.Name);
					var inspectorNameAttribute = field.GetCustomAttribute<InspectorNameAttribute>();
					if (inspectorNameAttribute != null)
						info.displayName = inspectorNameAttribute.displayName;

					info.hasInputAttribute = field.GetCustomAttribute(typeof(InputAttribute)) != null;
					info.hasOutputAttribute = field.GetCustomAttribute(typeof(OutputAttribute)) != null;
					info.isSerializeField = field.GetCustomAttribute(typeof(SerializeField)) != null;
					info.isNotSerialized = field.IsNotSerialized;
					info.showAsDrawer = field.GetCustomAttribute(typeof(ShowAsDrawer)) != null;
					info.isVisibleIf = field.GetCustomAttribute(typeof(VisibleIf)) != null;
					info.visibleIf = field.GetCustomAttribute(typeof(VisibleIf)) as VisibleIf;
					info.showInInspector = field.GetCustomAttribute<ShowInInspector>();

					fieldsInfo.Add(info);
				}
				inspectorFieldsCache[nodeType] = fieldsInfo;
			}

			foreach (var info in fieldsInfo)
			{
				var field = info.field;
				if (field.GetCustomAttribute(typeof(SettingAttribute)) != null) continue;

				if ((!field.IsPublic && !info.isSerializeField) || info.isNotSerialized)
				{
					AddEmptyField(field, fromInspector);
					continue;
				}

				bool hasInputOrOutputAttribute = info.hasInputAttribute || info.hasOutputAttribute;
				bool showAsDrawer = !fromInspector && info.showAsDrawer;
				if (!info.isSerializeField && hasInputOrOutputAttribute && !showAsDrawer)
				{
					AddEmptyField(field, fromInspector);
					continue;
				}

				if (field.GetCustomAttribute(typeof(System.NonSerializedAttribute)) != null || field.GetCustomAttribute(typeof(HideInInspector)) != null)
				{
					AddEmptyField(field, fromInspector);
					continue;
				}

				if (!info.isSerializeField && info.showInInspector != null && !info.showInInspector.showInNode && !fromInspector)
				{
					AddEmptyField(field, fromInspector);
					continue;
				}

				var showInputDrawer = info.hasInputAttribute && info.isSerializeField;
				showInputDrawer |= info.hasInputAttribute && info.showAsDrawer;
				showInputDrawer &= !fromInspector;
				showInputDrawer &= !typeof(IList).IsAssignableFrom(field.FieldType);

				var elem = AddControlField(field, info.displayName, showInputDrawer);
				if (info.hasInputAttribute && elem != null)
				{
					hideElementIfConnected[field.Name] = elem;
					if (portsPerFieldName.TryGetValue(field.Name, out var pvs))
						if (pvs.Any(pv => pv.GetEdges().Count > 0))
							elem.style.display = DisplayStyle.None;
				}

				if (info.isVisibleIf && info.visibleIf != null && elem != null)
				{
					var visibleCondition = info.visibleIf;
					var conditionField = nodeTarget.GetType().GetField(visibleCondition.fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (conditionField != null)
					{
						if (!visibleConditions.TryGetValue(visibleCondition.fieldName, out var list))
							list = visibleConditions[visibleCondition.fieldName] = new List<(object value, VisualElement target)>();
						list.Add((visibleCondition.value, elem));
						UpdateFieldVisibility(visibleCondition.fieldName, conditionField.GetValue(nodeTarget));
					}
				}
			}

			if (!fromInspector) controlsLoaded = true;
		}

		protected virtual void SetNodeColor(Color color)
		{
			if (titleContainer == null) return;
			titleContainer.style.borderBottomColor = new StyleColor(color);
			titleContainer.style.borderBottomWidth = new StyleFloat(color.a > 0 ? 5f : 0f);
		}
		
		private void AddEmptyField(FieldInfo field, bool fromInspector)
		{
			if (field == null || field.GetCustomAttribute(typeof(InputAttribute)) == null || fromInspector)
				return;

			if (field.GetCustomAttribute<VerticalAttribute>() != null)
				return;
			
			var box = new VisualElement {name = field.Name};
			box.AddToClassList("port-input-element");
			box.AddToClassList("empty");
			if (inputContainerElement != null)
				inputContainerElement.Add(box);
		}

		void UpdateFieldVisibility(string fieldName, object newValue)
		{
			if (newValue == null) return;
			if (visibleConditions.TryGetValue(fieldName, out var list))
			{
				foreach (var elem in list)
				{
					if (newValue.Equals(elem.value))
						elem.target.style.display = DisplayStyle.Flex;
					else
						elem.target.style.display = DisplayStyle.None;
				}
			}
		}

		void UpdateOtherFieldValueSpecific<T>(FieldInfo field, object newValue)
		{
			if (field == null || !fieldControlsMap.TryGetValue(field, out var list)) return;
			foreach (var inputField in list)
			{
				var notify = inputField as INotifyValueChanged<T>;
				if (notify != null)
					notify.SetValueWithoutNotify((T)newValue);
			}
		}

		static MethodInfo specificUpdateOtherFieldValue = typeof(BaseNodeView).GetMethod(nameof(UpdateOtherFieldValueSpecific), BindingFlags.NonPublic | BindingFlags.Instance);
		void UpdateOtherFieldValue(FieldInfo info, object newValue)
		{
			if (info == null) return;
			var fieldType = info.FieldType.IsSubclassOf(typeof(UnityEngine.Object)) ? typeof(UnityEngine.Object) : info.FieldType;
			var genericUpdate = specificUpdateOtherFieldValue.MakeGenericMethod(fieldType);
			genericUpdate.Invoke(this, new object[]{info, newValue});
		}

		object GetInputFieldValueSpecific<T>(FieldInfo field)
		{
			if (fieldControlsMap.TryGetValue(field, out var list))
			{
				foreach (var inputField in list)
				{
					if (inputField is INotifyValueChanged<T> notify)
						return notify.value;
				}
			}
			return null;
		}

		static MethodInfo specificGetValue = typeof(BaseNodeView).GetMethod(nameof(GetInputFieldValueSpecific), BindingFlags.NonPublic | BindingFlags.Instance);
		object GetInputFieldValue(FieldInfo info)
		{
			if (info == null) return null;
			var fieldType = info.FieldType.IsSubclassOf(typeof(UnityEngine.Object)) ? typeof(UnityEngine.Object) : info.FieldType;
			var genericUpdate = specificGetValue.MakeGenericMethod(fieldType);
			return genericUpdate.Invoke(this, new object[]{info});
		}

		protected VisualElement AddControlField(string fieldName, string label = null, bool showInputDrawer = false, Action valueChangedCallback = null)
		{
			var field = nodeTarget.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			return field != null ? AddControlField(field, label, showInputDrawer, valueChangedCallback) : null;
		}

		internal void SyncSerializedPropertyPathes()
		{
			if (owner == null || owner.graph == null || owner.serializedGraph == null || nodeTarget == null)
				return;

			int nodeIndex = owner.graph.nodes.FindIndex(n => n == nodeTarget);
			if (nodeIndex == -1) return;

			string prefix = "nodes.Array.data[" + nodeIndex + "].";
			foreach (var propertyField in cachedPropertyFields)
			{
				if (propertyField == null || string.IsNullOrEmpty(propertyField.bindingPath))
					continue;

				propertyField.Unbind();
				int lastDot = propertyField.bindingPath.LastIndexOf('.');
				if (lastDot != -1)
				{
					string fieldName = propertyField.bindingPath.Substring(lastDot + 1);
					propertyField.bindingPath = prefix + fieldName;
				}
				propertyField.Bind(owner.serializedGraph);
			}
		}

		protected SerializedProperty FindSerializedProperty(string fieldName)
		{
			if (owner == null || owner.graph == null || owner.serializedGraph == null) return null;
			int i = owner.graph.nodes.FindIndex(n => n == nodeTarget);
			if (i == -1) return null;
			var nodesProp = owner.serializedGraph.FindProperty("nodes");
			if (nodesProp == null || i >= nodesProp.arraySize) return null;
			return nodesProp.GetArrayElementAtIndex(i).FindPropertyRelative(fieldName);
		}

		protected VisualElement AddControlField(FieldInfo field, string label = null, bool showInputDrawer = false, Action valueChangedCallback = null)
		{
			if (field == null || owner == null || owner.serializedGraph == null) return null;

			var prop = FindSerializedProperty(field.Name);
			if (prop == null) return null;

			var element = new PropertyField(prop, showInputDrawer ? "" : label);
			element.Bind(owner.serializedGraph);

#if UNITY_2020_3
			if (showInputDrawer || String.IsNullOrEmpty(label))
				element.AddToClassList("DrawerField_2020_3");
#endif

			if (typeof(IList).IsAssignableFrom(field.FieldType))
				EnableSyncSelectionBorderHeight();

			element.RegisterValueChangeCallback(e => {
				if (nodeTarget != null) UpdateFieldVisibility(field.Name, field.GetValue(nodeTarget));
				valueChangedCallback?.Invoke();
				NotifyNodeChanged();
			});

			if (!owner.graph.IsLinkedToScene())
			{
				var objectField = element.Q<ObjectField>();
				if (objectField != null) objectField.allowSceneObjects = false;
			}

			if (!fieldControlsMap.TryGetValue(field, out var inputFieldList))
				inputFieldList = fieldControlsMap[field] = new List<VisualElement>();
			inputFieldList.Add(element);

			if (showInputDrawer)
			{
				var box = new VisualElement {name = field.Name};
				box.AddToClassList("port-input-element");
				box.Add(element);
				if (inputContainerElement != null) inputContainerElement.Add(box);
			}
			else
			{
				if (controlsContainer != null) controlsContainer.Add(element);
			}
			element.name = field.Name;
			cachedPropertyFields.Add(element);

			return element;
		}

		void UpdateFieldValues()
		{
			if (nodeTarget == null) return;
			foreach (var kp in fieldControlsMap)
				UpdateOtherFieldValue(kp.Key, kp.Key.GetValue(nodeTarget));
		}
		
		protected void AddSettingField(FieldInfo field)
		{
			if (field == null || owner == null || owner.serializedGraph == null) return;
			var prop = FindSerializedProperty(field.Name);
			if (prop == null) return;

			var element = new PropertyField(prop);
			element.Bind(owner.serializedGraph);
			element.name = field.Name;
			cachedPropertyFields.Add(element);
		}

		internal void OnPortConnected(PortView port)
		{
			if (port == null) return;
			if(port.direction == Direction.Input && inputContainerElement?.Q(port.fieldName) != null)
				inputContainerElement.Q(port.fieldName).AddToClassList("empty");
			
			if (hideElementIfConnected.TryGetValue(port.fieldName, out var elem))
				elem.style.display = DisplayStyle.None;

			onPortConnected?.Invoke(port);
		}

		internal void OnPortDisconnected(PortView port)
		{
			if (port == null) return;
			if (port.direction == Direction.Input && inputContainerElement?.Q(port.fieldName) != null)
			{
				inputContainerElement.Q(port.fieldName).RemoveFromClassList("empty");
				if (nodeTarget != null && nodeTarget.nodeFields.TryGetValue(port.fieldName, out var fieldInfo))
				{
					var valueBeforeConnection = GetInputFieldValue(fieldInfo.info);
					if (valueBeforeConnection != null)
						fieldInfo.info.SetValue(nodeTarget, valueBeforeConnection);
				}
			}
			
			if (hideElementIfConnected.TryGetValue(port.fieldName, out var elem))
				elem.style.display = DisplayStyle.Flex;

			onPortDisconnected?.Invoke(port);
		}

		public virtual void OnRemoved() {}
		public virtual void OnCreated() {}

		public override void SetPosition(Rect newPos)
		{
            if (nodeTarget == null) return;
			if (initializing || !nodeTarget.isLocked)
            {
                base.SetPosition(newPos);
				if (!initializing && owner != null && !owner.isGrouping && !owner.isReloading)
					owner.RegisterCompleteObjectUndo("Moved graph node");
                nodeTarget.position = newPos;
                initializing = false;
            }
		}

		public override bool	expanded
		{
			get { return base.expanded; }
			set
			{
				base.expanded = value;
				if (nodeTarget != null) nodeTarget.expanded = value;
				if (value && !controlsLoaded) Enable();
			}
		}

        public void ChangeLockStatus() { if (nodeTarget != null) nodeTarget.nodeLock ^= true; }

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
		{
			evt.menu.AppendAction("Align Flow", (e) => AlignFlowSelectedNodes());
			evt.menu.AppendSeparator();

			if (owner != null)
			{
				var groupView = owner.groupViews.FirstOrDefault(gv => gv.ContainsElement(this));
				if (groupView != null)
				{
					evt.menu.AppendAction("Remove from Group", (e) => {
						owner.RegisterCompleteObjectUndo("Remove from Group");
						groupView.RemoveElement(this);
					}, DropdownMenuAction.AlwaysEnabled);
				}
			}

			evt.menu.AppendAction("Open Node Script", (e) => OpenNodeScript(), OpenNodeScriptStatus);
			evt.menu.AppendAction("Open Node View Script", (e) => OpenNodeViewScript(), OpenNodeViewScriptStatus);
            if (nodeTarget != null && nodeTarget.unlockable)
                evt.menu.AppendAction((nodeTarget.isLocked ? "Unlock" : "Lock"), (e) => ChangeLockStatus(), LockStatus);
        }

        Status LockStatus(DropdownMenuAction action) => Status.Normal;

        Status OpenNodeScriptStatus(DropdownMenuAction action)
		{
			if (nodeTarget != null && NodeProvider.GetNodeScript(nodeTarget.GetType()) != null)
				return Status.Normal;
			return Status.Disabled;
		}

		Status OpenNodeViewScriptStatus(DropdownMenuAction action)
		{
			if (NodeProvider.GetNodeViewScript(GetType()) != null)
				return Status.Normal;
			return Status.Disabled;
		}

		IEnumerable< PortView > SyncPortCounts(IEnumerable< NodePort > ports, IEnumerable< PortView > portViews)
		{
			if (owner == null || nodeTarget == null) return portViews;
			var listener = owner.connectorListener;
			var portViewList = portViews.ToList();

			foreach (var pv in portViews.ToList())
			{
				if (!ports.Any(p => p.portData.identifier == pv.portData.identifier))
				{
					RemovePort(pv);
					portViewList.Remove(pv);
				}
			}

			foreach (var p in ports)
			{
				if (!portViews.Any(pv => p.portData.identifier == pv.portData.identifier))
				{
					Direction portDirection = nodeTarget.IsFieldInput(p.fieldName) ? Direction.Input : Direction.Output;
					var pv = AddPort(p.fieldInfo, portDirection, listener, p.portData);
					if (pv != null) portViewList.Add(pv);
				}
			}

			return portViewList;
		}

		void SyncPortOrder(IEnumerable< NodePort > ports, IEnumerable< PortView > portViews)
		{
			var portViewList = portViews.ToList();
			var portsList = ports.ToList();

			for (int i = 0; i < portsList.Count; i++)
			{
				var id = portsList[i].portData.identifier;
				var pv = portViewList.FirstOrDefault(p => p.portData.identifier == id);
				if (pv != null) InsertPort(pv, i);
			}
		}

		public virtual new bool RefreshPorts()
		{
			if (nodeTarget == null) return false;
			UpdatePortViewWithPorts(nodeTarget.inputPorts, inputPortViews);
			UpdatePortViewWithPorts(nodeTarget.outputPorts, outputPortViews);

			void UpdatePortViewWithPorts(NodePortContainer ports, List< PortView > portViews)
			{
				if (ports.Count == 0 && portViews.Count == 0) return;

				if (portViews.Count == 0)
					SyncPortCounts(ports, new PortView[]{});
				else if (ports.Count == 0)
					SyncPortCounts(new NodePort[]{}, portViews);
				else if (portViews.Count != ports.Count)
					SyncPortCounts(ports, portViews);
				else
				{
					var p = ports.GroupBy(n => n.fieldName);
					var pv = portViews.GroupBy(v => v.fieldName);
					p.Zip(pv, (portPerFieldName, portViewPerFieldName) => {
						IEnumerable< PortView > portViewsList = portViewPerFieldName;
						if (portPerFieldName.Count() != portViewPerFieldName.Count())
							portViewsList = SyncPortCounts(portPerFieldName, portViewPerFieldName);
						SyncPortOrder(portPerFieldName, portViewsList);
						return "";
					}).ToList();
				}

				for (int i = 0; i < portViews.Count; i++)
					if (i < ports.Count) portViews[i].UpdatePortView(ports[i].portData);
			}

			return base.RefreshPorts();
		}

		public void ForceUpdatePorts()
		{
			if (nodeTarget != null) nodeTarget.UpdateAllPorts();
			RefreshPorts();
		}

		void UpdatePortsForField(string fieldName) => RefreshPorts();

		protected virtual VisualElement CreateSettingsView() => new Label("Settings") {name = "header"};

		public void NotifyNodeChanged() { if (owner != null && owner.graph != null && nodeTarget != null) owner.graph.NotifyNodeChanged(nodeTarget); }

		#endregion
    }
}
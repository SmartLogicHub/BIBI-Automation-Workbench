import React, { createContext, memo, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import ELK from 'elkjs/lib/elk.bundled.js';
import {
  Background,
  BackgroundVariant,
  Handle,
  MarkerType,
  MiniMap,
  Panel,
  Position,
  ReactFlow,
  ReactFlowProvider,
  applyEdgeChanges,
  applyNodeChanges,
  useReactFlow
} from '@xyflow/react';
import {
  BadgeCheck,
  BookOpen,
  Check,
  ChevronLeft,
  ChevronRight,
  CircleDot,
  ClipboardCheck,
  Coins,
  Copy,
  Eye,
  Link2,
  ListChecks,
  LoaderCircle,
  Maximize,
  Minus,
  PanelLeftClose,
  PanelLeftOpen,
  PlayCircle,
  Plus,
  Radio,
  Redo2,
  Search,
  Share2,
  Shuffle,
  Sparkles,
  ThumbsUp,
  Timer,
  Trash2,
  Undo2,
  UserPlus,
  WandSparkles,
  X,
  Zap
} from 'lucide-react';
import '@xyflow/react/dist/style.css';
import './styles.css';
import {
  advanceClickConnection,
  deleteGraphElements,
  duplicateGraphNode,
  edgeLabelForSourceHandle,
  findAvailableNodePosition
} from './layout.js';
import { createNodeParameters } from './nodeDefaults.js';

const elk = new ELK();
const roots = new Map();
const NODE_WIDTH = 224;
const NODE_HEIGHT = 108;
const HandleInteractionContext = createContext({
  connectionSource: null,
  onHandleClick: () => {},
  isValidTarget: () => false
});

const clone = (value) => JSON.parse(JSON.stringify(value));

function iconForNodeType(nodeType = '') {
  if (nodeType === 'control.wait') return Timer;
  if (nodeType === 'control.random') return Shuffle;
  if (nodeType === 'control.log_result') return ClipboardCheck;
  if (nodeType.includes('random_watch') || nodeType === 'daily.watch_share') return PlayCircle;
  if (nodeType.includes('random_like') || nodeType.endsWith('.like')) return ThumbsUp;
  if (nodeType.includes('share')) return Share2;
  if (nodeType.includes('coin') || nodeType.includes('charge')) return Coins;
  if (nodeType.includes('follow')) return UserPlus;
  if (nodeType.includes('live')) return Radio;
  if (nodeType.includes('manga')) return BookOpen;
  if (nodeType.includes('vip')) return BadgeCheck;
  if (nodeType === 'task.daily') return ListChecks;
  return Zap;
}

function accentForGroup(group = '') {
  if (group.includes('控制')) return '#f2b36d';
  if (group.includes('随机')) return '#43bdd6';
  if (group.includes('直播')) return '#9e8ee8';
  if (group.includes('任务')) return '#7bc8a1';
  return '#8eb2ee';
}

function normalizeStep(step, index) {
  return {
    id: step.id || crypto.randomUUID().replaceAll('-', ''),
    nodeType: step.nodeType || '',
    displayName: step.displayName || '未命名动作',
    group: step.group || '',
    enabled: step.enabled !== false,
    sortOrder: Number(step.sortOrder) || index + 1,
    permission: step.permission || '',
    failureStrategy: step.failureStrategy || 'skip',
    retryCount: Number(step.retryCount) || 0,
    intervalSeconds: Number(step.intervalSeconds) || 0,
    maxRuntimeSeconds: Number(step.maxRuntimeSeconds) || 0,
    x: Number(step.x) || 0,
    y: Number(step.y) || 0,
    parameters: { ...(step.parameters || {}) }
  };
}

function toFlowNodes(steps = [], definitions = [], runningStates = {}) {
  const byType = new Map(definitions.map((item) => [item.nodeType, item]));
  return steps.map((rawStep, index) => {
    const step = normalizeStep(rawStep, index);
    const definition = byType.get(step.nodeType) || {
      nodeType: step.nodeType,
      displayName: step.displayName,
      group: step.group,
      description: ''
    };
    return {
      id: step.id,
      type: 'maintenanceNode',
      position: { x: step.x, y: step.y },
      data: {
        step,
        definition,
        runningState: runningStates[step.id] || ''
      }
    };
  });
}

function toFlowEdges(edges = []) {
  return edges.map((edge) => ({
    id: edge.id || crypto.randomUUID().replaceAll('-', ''),
    source: edge.sourceStepId,
    target: edge.targetStepId,
    sourceHandle: edge.sourceHandle && edge.sourceHandle !== 'default' ? edge.sourceHandle : null,
    targetHandle: edge.targetHandle && edge.targetHandle !== 'default' ? edge.targetHandle : null,
    label: edge.label === 'default'
      ? ''
      : edge.label || edgeLabelForSourceHandle(edge.sourceHandle),
    type: 'default',
    markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16, color: '#8c79e7' },
    style: { stroke: '#8c79e7', strokeWidth: 1.8 }
  }));
}

function serializeGraph(nodes, edges, viewport) {
  return {
    steps: nodes.map((node, index) => ({
      ...node.data.step,
      id: node.id,
      sortOrder: index + 1,
      x: Math.round(node.position.x),
      y: Math.round(node.position.y),
      parameters: { ...(node.data.step.parameters || {}) }
    })),
    edges: edges.map((edge) => ({
      id: edge.id,
      sourceStepId: edge.source,
      targetStepId: edge.target,
      sourceHandle: edge.sourceHandle || 'default',
      targetHandle: edge.targetHandle || 'default',
      label: edgeLabelForSourceHandle(edge.sourceHandle)
    })),
    viewportX: viewport.x,
    viewportY: viewport.y,
    viewportZoom: viewport.zoom
  };
}

const MaintenanceNode = memo(({ data, selected }) => {
  const { step, definition, runningState } = data;
  const { connectionSource, onHandleClick, isValidTarget } = useContext(HandleInteractionContext);
  const Icon = iconForNodeType(step.nodeType);
  const isRandom = step.nodeType === 'control.random';
  const isConnectionSource = connectionSource?.nodeId === step.id;
  const isConnectionTarget = connectionSource && isValidTarget(step.id);
  const statusText = runningState === 'running'
    ? '运行中'
    : runningState === 'success'
      ? '已完成'
      : runningState === 'failed'
        ? '未完成'
        : '';

  return (
    <article
      className={`workflow-node ${selected ? 'is-selected' : ''} ${step.enabled ? '' : 'is-disabled'} ${runningState ? `is-${runningState}` : ''} ${isConnectionSource ? 'is-connection-source' : ''} ${isConnectionTarget ? 'is-connection-target' : ''}`}
      style={{ '--node-accent': accentForGroup(step.group) }}
    >
      <Handle
        className="workflow-handle workflow-handle--input"
        type="target"
        position={Position.Left}
        id="default"
        aria-label={`连接到${step.displayName}`}
        onClick={(event) => {
          event.stopPropagation();
          onHandleClick({ nodeId: step.id, handleId: 'default', handleType: 'target' });
        }}
      />
      <div className="workflow-node__topline" />
      <div className="workflow-node__body">
        <span className="workflow-node__icon"><Icon size={17} strokeWidth={2} /></span>
        <div className="workflow-node__copy">
          <strong>{step.displayName || definition.displayName}</strong>
          <span>{definition.description || step.group}</span>
        </div>
        {statusText && (
          <span className={`workflow-node__status is-${runningState}`}>
            {runningState === 'running' ? <LoaderCircle size={13} /> : <Check size={13} />}
            {statusText}
          </span>
        )}
      </div>
      <footer className="workflow-node__footer">
        <span>{step.group || '养号动作'}</span>
        {!step.enabled && <span>已停用</span>}
      </footer>
      {isRandom ? (
        <>
          <span className="workflow-branch-label workflow-branch-label--matched">命中</span>
          <Handle
            className="workflow-handle workflow-handle--branch"
            type="source"
            position={Position.Right}
            id="matched"
            style={{ top: 35 }}
            aria-label={`${step.displayName}命中出口`}
            onClick={(event) => {
              event.stopPropagation();
              onHandleClick({ nodeId: step.id, handleId: 'matched', handleType: 'source' });
            }}
          />
          <span className="workflow-branch-label workflow-branch-label--otherwise">未命中</span>
          <Handle
            className="workflow-handle workflow-handle--branch"
            type="source"
            position={Position.Right}
            id="otherwise"
            style={{ top: 73 }}
            aria-label={`${step.displayName}未命中出口`}
            onClick={(event) => {
              event.stopPropagation();
              onHandleClick({ nodeId: step.id, handleId: 'otherwise', handleType: 'source' });
            }}
          />
        </>
      ) : (
        <Handle
          className="workflow-handle workflow-handle--output"
          type="source"
          position={Position.Right}
          id="default"
          aria-label={`${step.displayName}出口`}
          onClick={(event) => {
            event.stopPropagation();
            onHandleClick({ nodeId: step.id, handleId: 'default', handleType: 'source' });
          }}
        />
      )}
    </article>
  );
});

MaintenanceNode.displayName = 'MaintenanceNode';
const nodeTypes = { maintenanceNode: MaintenanceNode };

function WorkflowEditor({ options, dotnetRef }) {
  const definitions = options.nodes || [];
  const initialNodes = useMemo(
    () => toFlowNodes(options.steps, definitions, options.runningStates),
    // The editor is remounted when the workflow key changes.
    []
  );
  const initialEdges = useMemo(() => toFlowEdges(options.edges), []);
  const [nodes, setNodes] = useState(initialNodes);
  const [edges, setEdges] = useState(initialEdges);
  const [query, setQuery] = useState('');
  const [selectedGroup, setSelectedGroup] = useState('全部');
  const [selection, setSelection] = useState({ nodes: [], edges: [] });
  const [clickConnectionSource, setClickConnectionSource] = useState(null);
  const [libraryOpen, setLibraryOpen] = useState(() => window.innerWidth >= 1120);
  const [isArranging, setIsArranging] = useState(false);
  const [viewport, setViewportState] = useState({
    x: Number(options.viewportX) || 0,
    y: Number(options.viewportY) || 0,
    zoom: Math.min(2, Math.max(0.25, Number(options.viewportZoom) || 1))
  });
  const [isMobile, setIsMobile] = useState(() => window.innerWidth < 768);
  const nodesRef = useRef(nodes);
  const edgesRef = useRef(edges);
  const viewportRef = useRef(viewport);
  const historyRef = useRef([{ nodes: clone(initialNodes), edges: clone(initialEdges) }]);
  const historyIndexRef = useRef(0);
  const connectionDragRef = useRef(false);
  const externalVersionRef = useRef(options.externalVersion || 0);
  const lastEmittedRef = useRef(JSON.stringify(serializeGraph(initialNodes, initialEdges, viewport)));
  const {
    fitView,
    screenToFlowPosition,
    setViewport,
    zoomIn,
    zoomOut
  } = useReactFlow();

  useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  useEffect(() => { edgesRef.current = edges; }, [edges]);
  useEffect(() => { viewportRef.current = viewport; }, [viewport]);

  useEffect(() => {
    if (
      initialNodes.length > 0
      && Math.abs(Number(options.viewportX) || 0) < 1
      && Math.abs(Number(options.viewportY) || 0) < 1
      && Math.abs((Number(options.viewportZoom) || 1) - 1) < 0.01
    ) {
      const timer = window.setTimeout(() => fitView({ padding: 0.12, duration: 180 }), 80);
      return () => window.clearTimeout(timer);
    }
    return undefined;
  }, []);

  useEffect(() => {
    const nextStates = options.runningStates || {};
    setNodes((current) => current.map((node) => ({
      ...node,
      data: { ...node.data, runningState: nextStates[node.id] || '' }
    })));
  }, [options.runningStates]);

  useEffect(() => {
    const onResize = () => {
      const mobile = window.innerWidth < 768;
      setIsMobile(mobile);
      if (window.innerWidth < 1120) setLibraryOpen(false);
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  useEffect(() => {
    const nextVersion = options.externalVersion || 0;
    if (nextVersion === externalVersionRef.current) return;
    externalVersionRef.current = nextVersion;
    const nextNodes = toFlowNodes(options.steps, definitions, options.runningStates);
    const nextEdges = toFlowEdges(options.edges);
    nodesRef.current = nextNodes;
    edgesRef.current = nextEdges;
    setNodes(nextNodes);
    setEdges(nextEdges);
    setSelection({ nodes: [], edges: [] });
    setClickConnectionSource(null);
    historyRef.current = [{ nodes: clone(nextNodes), edges: clone(nextEdges) }];
    historyIndexRef.current = 0;
  }, [options.externalVersion, options.steps, options.edges, options.runningStates, definitions]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const state = serializeGraph(nodes, edges, viewportRef.current);
      const serialized = JSON.stringify(state);
      if (serialized === lastEmittedRef.current) return;
      lastEmittedRef.current = serialized;
      dotnetRef?.invokeMethodAsync('HandleEditorChanged', state);
    }, 420);
    return () => window.clearTimeout(timer);
  }, [nodes, edges, viewport, dotnetRef]);

  const notify = useCallback((message) => {
    dotnetRef?.invokeMethodAsync('HandleEditorMessage', message);
  }, [dotnetRef]);

  const pushHistory = useCallback((nextNodes, nextEdges) => {
    const currentHistory = historyRef.current.slice(0, historyIndexRef.current + 1);
    currentHistory.push({ nodes: clone(nextNodes), edges: clone(nextEdges) });
    if (currentHistory.length > 60) currentHistory.shift();
    historyRef.current = currentHistory;
    historyIndexRef.current = currentHistory.length - 1;
  }, []);

  const restoreHistory = useCallback((index) => {
    const snapshot = historyRef.current[index];
    if (!snapshot) return;
    const restoredNodes = clone(snapshot.nodes);
    const restoredEdges = clone(snapshot.edges);
    historyIndexRef.current = index;
    nodesRef.current = restoredNodes;
    edgesRef.current = restoredEdges;
    setNodes(restoredNodes);
    setEdges(restoredEdges);
  }, []);

  const undo = useCallback(() => {
    if (historyIndexRef.current <= 0) return;
    restoreHistory(historyIndexRef.current - 1);
  }, [restoreHistory]);

  const redo = useCallback(() => {
    if (historyIndexRef.current >= historyRef.current.length - 1) return;
    restoreHistory(historyIndexRef.current + 1);
  }, [restoreHistory]);

  const onNodesChange = useCallback((changes) => {
    setNodes((current) => {
      const next = applyNodeChanges(changes, current);
      nodesRef.current = next;
      return next;
    });
  }, []);

  const onEdgesChange = useCallback((changes) => {
    setEdges((current) => {
      const next = applyEdgeChanges(changes, current);
      edgesRef.current = next;
      return next;
    });
  }, []);

  const onDelete = useCallback(({ nodes: deletedNodes, edges: deletedEdges }) => {
    const next = deleteGraphElements(
      nodesRef.current,
      edgesRef.current,
      deletedNodes,
      deletedEdges
    );
    nodesRef.current = next.nodes;
    edgesRef.current = next.edges;
    setNodes(next.nodes);
    setEdges(next.edges);
    setSelection({ nodes: [], edges: [] });
    setClickConnectionSource((current) => (
      current && deletedNodes.some((node) => node.id === current.nodeId) ? null : current
    ));
    dotnetRef?.invokeMethodAsync('HandleNodeSelected', '');
    pushHistory(next.nodes, next.edges);
  }, [dotnetRef, pushHistory]);

  const deleteSelection = useCallback(() => {
    if (selection.nodes.length === 0 && selection.edges.length === 0) return;
    const hasConnectedNode = selection.nodes.some((node) => (
      edgesRef.current.some((edge) => edge.source === node.id || edge.target === node.id)
    ));
    if (hasConnectedNode && !window.confirm('删除节点会同时删除与它相连的线，确定继续吗？')) return;
    onDelete(selection);
  }, [onDelete, selection]);

  const duplicateSelection = useCallback(() => {
    if (selection.nodes.length !== 1 || selection.edges.length > 0) return;
    const source = selection.nodes[0];
    const nextId = crypto.randomUUID().replaceAll('-', '');
    const duplicate = duplicateGraphNode(source, nextId);
    const nextNodes = [
      ...nodesRef.current.map((node) => ({ ...node, selected: false })),
      duplicate
    ];
    nodesRef.current = nextNodes;
    setNodes(nextNodes);
    setSelection({ nodes: [duplicate], edges: [] });
    pushHistory(nextNodes, edgesRef.current);
    dotnetRef?.invokeMethodAsync('HandleNodeSelected', nextId);
  }, [dotnetRef, pushHistory, selection]);

  const createsCycle = useCallback((source, target) => {
    const outgoing = new Map();
    for (const edge of edgesRef.current) {
      if (!outgoing.has(edge.source)) outgoing.set(edge.source, []);
      outgoing.get(edge.source).push(edge.target);
    }
    const stack = [target];
    const visited = new Set();
    while (stack.length) {
      const current = stack.pop();
      if (current === source) return true;
      if (visited.has(current)) continue;
      visited.add(current);
      for (const next of outgoing.get(current) || []) stack.push(next);
    }
    return false;
  }, []);

  const connectionError = useCallback((connection) => {
    if (!connection.source || !connection.target) return '请选择两个有效节点。';
    if (connection.source === connection.target) return '节点不能连接到自身。';
    if (createsCycle(connection.source, connection.target)) return '养号流程不能形成循环。';
    if (edgesRef.current.some((edge) => edge.source === connection.source && edge.target === connection.target)) {
      return '这两个节点已经连接。';
    }

    const sourceNode = nodesRef.current.find((node) => node.id === connection.source);
    if (!sourceNode) return '没有找到起点节点。';
    const outgoing = edgesRef.current.filter((edge) => edge.source === connection.source);
    if (sourceNode?.data.step.nodeType === 'control.random') {
      if (outgoing.length >= 2) return '随机判断节点的两个分支都已连接。';
      if (outgoing.some((edge) => (edge.sourceHandle || 'default') === (connection.sourceHandle || 'default'))) {
        return '这个分支已经连接了后续动作。';
      }
      return '';
    }
    return outgoing.length === 0 ? '' : '当前动作已经有后续节点，请先删除原连线。';
  }, [createsCycle]);

  const isValidConnection = useCallback(
    (connection) => connectionError(connection) === '',
    [connectionError]
  );

  const onConnect = useCallback((connection) => {
    const error = connectionError(connection);
    if (error) {
      notify(error);
      return false;
    }
    const nextEdge = {
      id: crypto.randomUUID().replaceAll('-', ''),
      source: connection.source,
      target: connection.target,
      sourceHandle: connection.sourceHandle,
      targetHandle: connection.targetHandle,
      label: edgeLabelForSourceHandle(connection.sourceHandle),
      type: 'default',
      markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16, color: '#8c79e7' },
      style: { stroke: '#8c79e7', strokeWidth: 1.8 }
    };
    const nextEdges = [...edgesRef.current, nextEdge];
    edgesRef.current = nextEdges;
    setEdges(nextEdges);
    pushHistory(nodesRef.current, nextEdges);
    setClickConnectionSource(null);
    return true;
  }, [connectionError, notify, pushHistory]);

  const onHandleClick = useCallback((click) => {
    if (connectionDragRef.current || isMobile) return;
    const result = advanceClickConnection(clickConnectionSource, click);
    if (result.kind === 'missing-source') {
      notify('请先点击上游节点右侧的圆点。');
      return;
    }
    if (result.kind === 'completed') {
      onConnect(result.connection);
      setClickConnectionSource(null);
      return;
    }
    setClickConnectionSource(result.source || null);
  }, [clickConnectionSource, isMobile, notify, onConnect]);

  const handleInteraction = useMemo(() => ({
    connectionSource: clickConnectionSource,
    onHandleClick,
    isValidTarget: (targetId) => Boolean(
      clickConnectionSource
      && isValidConnection({
        source: clickConnectionSource.nodeId,
        sourceHandle: clickConnectionSource.handleId,
        target: targetId,
        targetHandle: 'default'
      })
    )
  }), [clickConnectionSource, isValidConnection, onHandleClick]);

  const createNode = useCallback((definition, position) => {
    const step = normalizeStep({
      id: crypto.randomUUID().replaceAll('-', ''),
      nodeType: definition.nodeType,
      displayName: definition.displayName,
      group: definition.group,
      enabled: definition.enabledByDefault !== false,
      failureStrategy: 'skip',
      intervalSeconds: definition.nodeType === 'control.wait' ? 30 : 0,
      parameters: createNodeParameters(definition)
    }, nodesRef.current.length);
    const nextNode = {
      id: step.id,
      type: 'maintenanceNode',
      position,
      data: { step, definition, runningState: '' }
    };
    const nextNodes = [...nodesRef.current, nextNode];
    nodesRef.current = nextNodes;
    setNodes(nextNodes);
    pushHistory(nextNodes, edgesRef.current);
    dotnetRef?.invokeMethodAsync('HandleNodeSelected', step.id);
  }, [dotnetRef, pushHistory]);

  const addNodeNearCenter = useCallback((definition) => {
    const desiredPosition = screenToFlowPosition({
      x: window.innerWidth * 0.58,
      y: window.innerHeight * 0.48
    });
    const position = findAvailableNodePosition(
      nodesRef.current,
      desiredPosition,
      NODE_WIDTH,
      NODE_HEIGHT
    );
    createNode(definition, position);
  }, [createNode, screenToFlowPosition]);

  const onDrop = useCallback((event) => {
    event.preventDefault();
    const nodeType = event.dataTransfer.getData('application/bibi-node');
    const definition = definitions.find((item) => item.nodeType === nodeType);
    if (!definition) return;
    createNode(definition, screenToFlowPosition({ x: event.clientX, y: event.clientY }));
  }, [createNode, definitions, screenToFlowPosition]);

  const onDragOver = useCallback((event) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  }, []);

  const arrange = useCallback(async () => {
    if (nodesRef.current.length === 0 || isArranging) return;
    setIsArranging(true);
    try {
      const graph = {
        id: 'root',
        layoutOptions: {
          'elk.algorithm': 'layered',
          'elk.direction': 'RIGHT',
          'elk.spacing.nodeNode': '54',
          'elk.layered.spacing.nodeNodeBetweenLayers': '92',
          'elk.layered.nodePlacement.strategy': 'NETWORK_SIMPLEX'
        },
        children: nodesRef.current.map((node) => ({
          id: node.id,
          width: NODE_WIDTH,
          height: NODE_HEIGHT
        })),
        edges: edgesRef.current.map((edge) => ({
          id: edge.id,
          sources: [edge.source],
          targets: [edge.target]
        }))
      };
      const layout = await elk.layout(graph);
      const positions = new Map((layout.children || []).map((item) => [item.id, item]));
      const nextNodes = nodesRef.current.map((node) => {
        const position = positions.get(node.id);
        return position ? { ...node, position: { x: position.x || 0, y: position.y || 0 } } : node;
      });
      nodesRef.current = nextNodes;
      setNodes(nextNodes);
      pushHistory(nextNodes, edgesRef.current);
      window.setTimeout(() => fitView({ padding: 0.12, duration: 180 }), 40);
    } catch {
      notify('自动整理未完成，请稍后重试。');
    } finally {
      setIsArranging(false);
    }
  }, [fitView, isArranging, notify, pushHistory]);

  const groups = useMemo(
    () => ['全部', ...Array.from(new Set(definitions.map((item) => item.group).filter(Boolean)))],
    [definitions]
  );
  const filteredDefinitions = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    return definitions.filter((item) => {
      const matchesGroup = selectedGroup === '全部' || item.group === selectedGroup;
      const matchesQuery = !normalizedQuery
        || item.displayName.toLowerCase().includes(normalizedQuery)
        || item.description.toLowerCase().includes(normalizedQuery);
      return matchesGroup && matchesQuery;
    });
  }, [definitions, query, selectedGroup]);

  return (
    <div className={`workflow-react-shell ${libraryOpen ? 'has-library' : 'library-collapsed'} ${isMobile ? 'is-mobile' : ''}`}>
      {!isMobile && (
        <aside className="workflow-library" aria-label="养号节点库">
          <div className="workflow-library__heading">
            {libraryOpen && (
              <div>
                <span>养号动作</span>
                <strong>节点库</strong>
              </div>
            )}
            <button
              className="workflow-icon-button"
              type="button"
              onClick={() => setLibraryOpen((value) => !value)}
              title={libraryOpen ? '收起节点库' : '展开节点库'}
              aria-label={libraryOpen ? '收起节点库' : '展开节点库'}
            >
              {libraryOpen ? <PanelLeftClose size={18} /> : <PanelLeftOpen size={18} />}
            </button>
          </div>
          {libraryOpen && (
            <>
              <label className="workflow-library__search">
                <Search size={16} />
                <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索动作" />
              </label>
              <select
                className="workflow-library__filter"
                value={selectedGroup}
                onChange={(event) => setSelectedGroup(event.target.value)}
                aria-label="节点分类"
              >
                {groups.map((group) => <option value={group} key={group}>{group}</option>)}
              </select>
              <div className="workflow-library__list">
                {filteredDefinitions.map((definition) => {
                  const Icon = iconForNodeType(definition.nodeType);
                  return (
                    <div
                      className="workflow-library-item"
                      key={definition.nodeType}
                      draggable
                      onDragStart={(event) => {
                        event.dataTransfer.setData('application/bibi-node', definition.nodeType);
                        event.dataTransfer.effectAllowed = 'move';
                      }}
                      onDoubleClick={() => addNodeNearCenter(definition)}
                    >
                      <span className="workflow-library-item__icon" style={{ '--node-accent': accentForGroup(definition.group) }}>
                        <Icon size={17} />
                      </span>
                      <span className="workflow-library-item__copy">
                        <strong>{definition.displayName}</strong>
                        <small>{definition.description}</small>
                      </span>
                      <button
                        type="button"
                        className="workflow-library-item__add"
                        onClick={() => addNodeNearCenter(definition)}
                        title="添加到画布"
                        aria-label={`添加${definition.displayName}`}
                      >
                        <Plus size={16} />
                      </button>
                    </div>
                  );
                })}
              </div>
            </>
          )}
        </aside>
      )}

      <main className="workflow-flow-area">
        {isMobile && <div className="workflow-mobile-note">移动端仅支持查看流程，编辑请使用更宽的窗口。</div>}
        <HandleInteractionContext.Provider value={handleInteraction}>
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onDelete={onDelete}
            onConnect={onConnect}
            onConnectStart={() => {
              connectionDragRef.current = true;
              setClickConnectionSource(null);
            }}
            onConnectEnd={() => {
              window.setTimeout(() => { connectionDragRef.current = false; }, 0);
            }}
            isValidConnection={isValidConnection}
            onDrop={onDrop}
            onDragOver={onDragOver}
            onSelectionChange={(nextSelection) => setSelection(nextSelection)}
            onNodeClick={(_, node) => dotnetRef?.invokeMethodAsync('HandleNodeSelected', node.id)}
            onEdgeClick={() => dotnetRef?.invokeMethodAsync('HandleNodeSelected', '')}
            onPaneClick={() => {
              setClickConnectionSource(null);
              setSelection({ nodes: [], edges: [] });
              dotnetRef?.invokeMethodAsync('HandleNodeSelected', '');
            }}
            onNodeDragStop={() => pushHistory(nodesRef.current, edgesRef.current)}
            onMove={(_, nextViewport) => setViewportState(nextViewport)}
            onMoveEnd={(_, nextViewport) => {
              viewportRef.current = nextViewport;
              setViewportState(nextViewport);
            }}
            defaultViewport={viewport}
            minZoom={0.25}
            maxZoom={2}
            fitViewOptions={{ padding: 0.12, duration: 180 }}
            nodesDraggable={!isMobile}
            nodesConnectable={!isMobile}
            elementsSelectable={!isMobile}
            deleteKeyCode={isMobile ? null : ['Backspace', 'Delete']}
            selectionOnDrag={!isMobile}
            panOnScroll
            zoomOnDoubleClick={false}
            connectionLineStyle={{ stroke: '#8c79e7', strokeWidth: 1.8 }}
            proOptions={{ hideAttribution: true }}
          >
          <Background variant={BackgroundVariant.Dots} gap={18} size={1.2} color="#cfd8d3" />
          {nodes.length === 0 && !isMobile && (
            <div className="workflow-empty-state">
              <CircleDot size={24} />
              <strong>从左侧添加第一个养号动作</strong>
            </div>
          )}
          {!isMobile && (
            <Panel position="bottom-left" className="workflow-canvas-tools">
              <button type="button" onClick={undo} title="撤销" aria-label="撤销"><Undo2 size={17} /></button>
              <button type="button" onClick={redo} title="重做" aria-label="重做"><Redo2 size={17} /></button>
              <span />
              <button type="button" onClick={() => zoomOut({ duration: 120 })} title="缩小" aria-label="缩小"><Minus size={17} /></button>
              <output>{Math.round(viewport.zoom * 100)}%</output>
              <button type="button" onClick={() => zoomIn({ duration: 120 })} title="放大" aria-label="放大"><Plus size={17} /></button>
              <button type="button" onClick={() => fitView({ padding: 0.12, duration: 180 })} title="适配画布" aria-label="适配画布"><Maximize size={17} /></button>
              <button type="button" onClick={arrange} title="一键整理" aria-label="一键整理">
                {isArranging ? <LoaderCircle className="is-spinning" size={17} /> : <WandSparkles size={17} />}
              </button>
            </Panel>
          )}
          {!isMobile && (clickConnectionSource || selection.nodes.length > 0 || selection.edges.length > 0) && (
            <Panel position="top-center" className="workflow-selection-tools">
              {clickConnectionSource ? (
                <>
                  <Link2 size={16} />
                  <span>点击目标节点左侧圆点完成连线</span>
                  <button
                    type="button"
                    onClick={() => setClickConnectionSource(null)}
                    title="取消连线"
                    aria-label="取消连线"
                  >
                    <X size={16} />
                  </button>
                </>
              ) : (
                <>
                  <span>
                    {selection.nodes.length === 1
                      ? selection.nodes[0].data.step.displayName
                      : selection.edges.length === 1
                        ? '已选择连线'
                        : `已选择 ${selection.nodes.length + selection.edges.length} 项`}
                  </span>
                  {selection.nodes.length === 1 && selection.edges.length === 0 && (
                    <button type="button" onClick={duplicateSelection} title="复制节点" aria-label="复制节点">
                      <Copy size={16} />
                    </button>
                  )}
                  <button
                    className="is-danger"
                    type="button"
                    onClick={deleteSelection}
                    title="删除所选"
                    aria-label="删除所选"
                  >
                    <Trash2 size={16} />
                  </button>
                </>
              )}
            </Panel>
          )}
          <MiniMap
            className="workflow-minimap"
            pannable={!isMobile}
            zoomable={!isMobile}
            nodeColor={(node) => accentForGroup(node.data.step.group)}
            maskColor="rgba(244, 247, 244, 0.72)"
          />
          </ReactFlow>
        </HandleInteractionContext.Provider>
      </main>
    </div>
  );
}

function EditorRoot(props) {
  return (
    <ReactFlowProvider>
      <WorkflowEditor {...props} />
    </ReactFlowProvider>
  );
}

function mount(elementId, options, dotnetRef) {
  const element = document.getElementById(elementId);
  if (!element) return;
  let entry = roots.get(elementId);
  if (!entry) {
    entry = { root: createRoot(element) };
    roots.set(elementId, entry);
  }
  entry.options = options;
  entry.dotnetRef = dotnetRef;
  entry.root.render(<EditorRoot key={options.workflowKey || 'new'} options={options} dotnetRef={dotnetRef} />);
}

function update(elementId, options) {
  const entry = roots.get(elementId);
  if (!entry) return;
  entry.options = options;
  entry.root.render(<EditorRoot key={options.workflowKey || 'new'} options={options} dotnetRef={entry.dotnetRef} />);
}

function unmount(elementId) {
  const entry = roots.get(elementId);
  if (!entry) return;
  entry.root.unmount();
  roots.delete(elementId);
}

window.bibiWorkflowEditor = { mount, update, unmount };

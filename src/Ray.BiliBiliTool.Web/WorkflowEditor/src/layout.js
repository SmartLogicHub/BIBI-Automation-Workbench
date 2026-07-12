function overlaps(existing, candidate, width, height, gap) {
  return candidate.x < existing.x + width + gap
    && candidate.x + width + gap > existing.x
    && candidate.y < existing.y + height + gap
    && candidate.y + height + gap > existing.y;
}

export function findAvailableNodePosition(nodes, desired, width, height, gap = 24) {
  const positions = nodes.map((node) => node.position || { x: 0, y: 0 });
  const isAvailable = (candidate) => positions.every(
    (existing) => !overlaps(existing, candidate, width, height, gap)
  );

  if (isAvailable(desired)) return desired;

  const stepX = width + gap;
  const stepY = height + gap;
  for (let ring = 1; ring <= 50; ring += 1) {
    const offsets = [
      [ring, 0], [0, ring], [-ring, 0], [0, -ring],
      [ring, ring], [-ring, ring], [-ring, -ring], [ring, -ring]
    ];
    for (const [column, row] of offsets) {
      const candidate = {
        x: desired.x + column * stepX,
        y: desired.y + row * stepY
      };
      if (isAvailable(candidate)) return candidate;
    }
  }

  return {
    x: desired.x + positions.length * stepX,
    y: desired.y
  };
}

export function deleteGraphElements(nodes, edges, deletedNodes = [], deletedEdges = []) {
  const deletedNodeIds = new Set(deletedNodes.map((node) => node.id));
  const deletedEdgeIds = new Set(deletedEdges.map((edge) => edge.id));

  return {
    nodes: nodes.filter((node) => !deletedNodeIds.has(node.id)),
    edges: edges.filter((edge) => (
      !deletedEdgeIds.has(edge.id)
      && !deletedNodeIds.has(edge.source)
      && !deletedNodeIds.has(edge.target)
    ))
  };
}

export function edgeLabelForSourceHandle(handleId) {
  if (handleId === 'matched') return '命中';
  if (handleId === 'otherwise') return '未命中';
  return '';
}

export function advanceClickConnection(currentSource, click) {
  if (!click?.nodeId || !click?.handleType) return { kind: 'ignored', source: currentSource };

  if (click.handleType === 'source') {
    const nextSource = {
      nodeId: click.nodeId,
      handleId: click.handleId || 'default'
    };
    if (
      currentSource?.nodeId === nextSource.nodeId
      && currentSource?.handleId === nextSource.handleId
    ) {
      return { kind: 'cancelled', source: null };
    }
    return { kind: currentSource ? 'restarted' : 'started', source: nextSource };
  }

  if (!currentSource) return { kind: 'missing-source', source: null };

  return {
    kind: 'completed',
    source: null,
    connection: {
      source: currentSource.nodeId,
      sourceHandle: currentSource.handleId || 'default',
      target: click.nodeId,
      targetHandle: click.handleId || 'default'
    }
  };
}

export function duplicateGraphNode(node, nextId) {
  const duplicated = JSON.parse(JSON.stringify(node));
  duplicated.id = nextId;
  duplicated.position = {
    x: Number(node.position?.x || 0) + 32,
    y: Number(node.position?.y || 0) + 32
  };
  duplicated.selected = true;
  duplicated.data.step.id = nextId;
  duplicated.data.runningState = '';
  return duplicated;
}

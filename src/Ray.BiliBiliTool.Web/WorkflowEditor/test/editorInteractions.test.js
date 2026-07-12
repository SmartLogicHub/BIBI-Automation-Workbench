import test from 'node:test';
import assert from 'node:assert/strict';

import {
  advanceClickConnection,
  duplicateGraphNode,
  edgeLabelForSourceHandle
} from '../src/layout.js';

test('clicking an output and then an input creates a direct connection', () => {
  const started = advanceClickConnection(null, {
    nodeId: 'watch',
    handleId: 'default',
    handleType: 'source'
  });

  assert.equal(started.kind, 'started');
  assert.deepEqual(started.source, { nodeId: 'watch', handleId: 'default' });

  const completed = advanceClickConnection(started.source, {
    nodeId: 'like',
    handleId: 'default',
    handleType: 'target'
  });

  assert.equal(completed.kind, 'completed');
  assert.deepEqual(completed.connection, {
    source: 'watch',
    sourceHandle: 'default',
    target: 'like',
    targetHandle: 'default'
  });
});

test('clicking the selected output again cancels click connection mode', () => {
  const result = advanceClickConnection(
    { nodeId: 'watch', handleId: 'default' },
    { nodeId: 'watch', handleId: 'default', handleType: 'source' }
  );

  assert.equal(result.kind, 'cancelled');
});

test('ordinary connections never expose the internal default label', () => {
  assert.equal(edgeLabelForSourceHandle('default'), '');
  assert.equal(edgeLabelForSourceHandle(null), '');
  assert.equal(edgeLabelForSourceHandle('matched'), '命中');
  assert.equal(edgeLabelForSourceHandle('otherwise'), '未命中');
});

test('duplicating a node creates a new selected node without copying edges', () => {
  const source = {
    id: 'watch',
    type: 'maintenanceNode',
    position: { x: 100, y: 80 },
    selected: true,
    data: {
      step: { id: 'watch', displayName: '随机观看', parameters: { countMin: '1' } },
      definition: { nodeType: 'action.random_watch' },
      runningState: 'success'
    }
  };

  const duplicate = duplicateGraphNode(source, 'watch-copy');

  assert.equal(duplicate.id, 'watch-copy');
  assert.equal(duplicate.data.step.id, 'watch-copy');
  assert.deepEqual(duplicate.position, { x: 132, y: 112 });
  assert.equal(duplicate.selected, true);
  assert.equal(duplicate.data.runningState, '');
  assert.notEqual(duplicate.data.step.parameters, source.data.step.parameters);
});

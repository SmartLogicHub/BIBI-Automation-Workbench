import test from 'node:test';
import assert from 'node:assert/strict';

import { deleteGraphElements, findAvailableNodePosition } from '../src/layout.js';

test('quick add chooses the nearest non-overlapping canvas position', () => {
  const desired = { x: 100, y: 100 };
  const existing = [
    { position: { x: 100, y: 100 } },
    { position: { x: 356, y: 100 } }
  ];

  const result = findAvailableNodePosition(existing, desired, 224, 108, 24);

  assert.deepEqual(result, { x: 100, y: 232 });
});

test('node deletion is committed as one graph snapshot', () => {
  const nodes = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
  const edges = [
    { id: 'ab', source: 'a', target: 'b' },
    { id: 'bc', source: 'b', target: 'c' }
  ];

  const result = deleteGraphElements(nodes, edges, [{ id: 'c' }], []);

  assert.deepEqual(result.nodes.map((node) => node.id), ['a', 'b']);
  assert.deepEqual(result.edges.map((edge) => edge.id), ['ab']);
});

test('edge-only deletion keeps every node', () => {
  const nodes = [{ id: 'a' }, { id: 'b' }];
  const edges = [{ id: 'ab', source: 'a', target: 'b' }];

  const result = deleteGraphElements(nodes, edges, [], [{ id: 'ab' }]);

  assert.deepEqual(result.nodes, nodes);
  assert.deepEqual(result.edges, []);
});

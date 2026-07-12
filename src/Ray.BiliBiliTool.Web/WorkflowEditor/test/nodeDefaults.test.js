import test from 'node:test';
import assert from 'node:assert/strict';
import { createNodeParameters } from '../src/nodeDefaults.js';

test('composite tasks do not expose repeat controls', () => {
  assert.deepEqual(createNodeParameters({ nodeType: 'task.daily', group: '任务组合' }), {
    executionProbability: '100'
  });
});

test('risky random actions start with conservative daily limits', () => {
  assert.deepEqual(
    createNodeParameters({ nodeType: 'social.random_follow', group: '随机互动' }),
    {
      executionProbability: '100',
      countMin: '1',
      countMax: '1',
      waitMinSeconds: '5',
      waitMaxSeconds: '15',
      dailyLimit: '2'
    }
  );
  assert.equal(
    createNodeParameters({ nodeType: 'daily.random_watch', group: '随机互动' }).dailyLimit,
    '10'
  );
});

test('control nodes only receive their own settings', () => {
  assert.deepEqual(createNodeParameters({ nodeType: 'control.wait', group: '控制节点' }), {});
  assert.deepEqual(createNodeParameters({ nodeType: 'control.random', group: '控制节点' }), {
    branchProbability: '50'
  });
});

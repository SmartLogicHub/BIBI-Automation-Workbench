const DAILY_LIMITS = new Map([
  ['daily.random_watch', 10],
  ['daily.watch_share', 5],
  ['daily.random_like', 3],
  ['daily.random_share', 3],
  ['daily.donate_coin', 2],
  ['social.random_follow', 2]
]);

export function createNodeParameters(definition) {
  const nodeType = definition?.nodeType || '';
  if (nodeType === 'control.random') {
    return { branchProbability: '50' };
  }
  if (nodeType.startsWith('control.')) {
    return {};
  }

  const base = { executionProbability: '100' };
  const isRepeatable = definition?.group === '随机互动' || definition?.group === '直播互动';
  if (!isRepeatable) {
    return base;
  }

  return {
    ...base,
    countMin: '1',
    countMax: '1',
    waitMinSeconds: '5',
    waitMaxSeconds: '15',
    dailyLimit: String(DAILY_LIMITS.get(nodeType) ?? 5)
  };
}

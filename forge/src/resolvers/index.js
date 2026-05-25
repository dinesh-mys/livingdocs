import Resolver from '@forge/resolver';
import { fetch } from '@forge/api';
import { storage } from '@forge/kvs';

const resolver = new Resolver();

resolver.define('getConfig', async ({ context }) => {
  const pageId = context.extension.content.id;
  return await storage.get(`page-config-${pageId}`) ?? null;
});

resolver.define('saveConfig', async ({ payload, context }) => {
  const pageId = context.extension.content.id;
  await storage.set(`page-config-${pageId}`, {
    repoUrl: payload.repoUrl,
    filePath: payload.filePath,
    savedAt: new Date().toISOString()
  });
  return { ok: true };
});

resolver.define('analyzeFile', async ({ payload, context }) => {
  const { repoUrl, filePath } = payload;
  const pageId = context.extension.content.id;

  try {
    const match = repoUrl.match(/github\.com\/([^/]+)\/([^/]+)/);
    if (!match) return { error: 'Invalid GitHub URL — use https://github.com/owner/repo' };

    const [, owner, repo] = match;
    const cleanRepo = repo.replace(/\.git$/, '');
    const headers = { 'Accept': 'application/vnd.github+json' };

    // 1. Staleness — last 50 commits on this file
    const commitsRes = await fetch(
      `https://api.github.com/repos/${owner}/${cleanRepo}/commits?path=${encodeURIComponent(filePath)}&per_page=50`,
      { headers }
    );
    if (!commitsRes.ok) return { error: `GitHub API error: ${commitsRes.status}` };

    const commits = await commitsRes.json();
    if (!commits.length) return { error: 'No commits found for this file path.' };

    const lastChange = new Date(commits[0].commit.author.date);
    const daysSinceChange = Math.floor((Date.now() - lastChange) / 86400000);
    const stalenessScore = Math.min(100, Math.floor((daysSinceChange / 90) * 100));

    const staleness = {
      daysSinceChange,
      stalenessScore,
      isStale: stalenessScore >= 40,
      commitSha: commits[0].sha.slice(0, 7),
      commitMessage: commits[0].commit.message.split('\n')[0],
    };

    // 2. Departure risk — dominant author analysis
    const authorCounts = {};
    commits.forEach(c => {
      const author = c.commit.author.name;
      authorCounts[author] = (authorCounts[author] || 0) + 1;
    });
    const total = commits.length;
    const [topAuthor, topCount] = Object.entries(authorCounts).sort((a, b) => b[1] - a[1])[0];
    const percentage = Math.round((topCount / total) * 100);
    const departureRisk = (percentage >= 60 && total >= 5)
      ? { topAuthor, percentage, commitCount: topCount }
      : null;

    // 3. Doc comments — fetch file content and scan for comment patterns
    let hasDocComments = false;
    const contentRes = await fetch(
      `https://api.github.com/repos/${owner}/${cleanRepo}/contents/${encodeURIComponent(filePath)}`,
      { headers }
    );
    if (contentRes.ok) {
      const contentData = await contentRes.json();
      const decoded = Buffer.from(contentData.content.replace(/\n/g, ''), 'base64').toString('utf8');
      hasDocComments = /\/\/\/|\/\*\*|"""|'''/.test(decoded);
    }

    // 4. Last synced timestamp from storage
    const lastSynced = await storage.get(`last-synced-${pageId}`) ?? null;

    return { staleness, departureRisk, hasDocComments, lastSynced };

  } catch (err) {
    return { error: err.message };
  }
});

export const handler = resolver.getDefinitions();

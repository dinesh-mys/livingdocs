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

resolver.define('getStaleness', async ({ payload }) => {
  const { repoUrl, filePath } = payload;
  try {
    const match = repoUrl.match(/github\.com\/([^/]+)\/([^/]+)/);
    if (!match) return { error: 'Invalid GitHub URL — use https://github.com/owner/repo' };

    const [, owner, repo] = match;
    const cleanRepo = repo.replace(/\.git$/, '');

    const res = await fetch(
      `https://api.github.com/repos/${owner}/${cleanRepo}/commits?path=${encodeURIComponent(filePath)}&per_page=1`,
      { headers: { 'Accept': 'application/vnd.github+json' } }
    );

    if (!res.ok) return { error: `GitHub API error: ${res.status}` };

    const commits = await res.json();
    if (!commits.length) return { error: 'No commits found for this file path' };

    const lastCodeChange = new Date(commits[0].commit.author.date);
    const daysSinceChange = Math.floor((Date.now() - lastCodeChange) / 86400000);
    const stalenessScore = Math.min(100, Math.floor((daysSinceChange / 90) * 100));

    return {
      lastCodeChange: lastCodeChange.toISOString(),
      daysSinceChange,
      stalenessScore,
      isStale: stalenessScore >= 40,
      commitSha: commits[0].sha.slice(0, 7),
      commitMessage: commits[0].commit.message.split('\n')[0],
      fileUrl: `https://github.com/${owner}/${cleanRepo}/blob/main/${filePath}`,
    };
  } catch (err) {
    return { error: err.message };
  }
});

export const handler = resolver.getDefinitions();

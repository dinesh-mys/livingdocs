import React, { useState, useEffect } from 'react';
import ForgeReconciler, { Text, Button, Stack, Textfield } from '@forge/react';
import { invoke } from '@forge/bridge';

function App() {
  const [config, setConfig] = useState(null);
  const [loading, setLoading] = useState(true);
  const [repoUrl, setRepoUrl] = useState('');
  const [filePath, setFilePath] = useState('');
  const [staleness, setStaleness] = useState(null);
  const [saving, setSaving] = useState(false);
  const [checking, setChecking] = useState(false);

  useEffect(() => {
    invoke('getConfig')
      .then(cfg => { setConfig(cfg); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  const save = async () => {
    if (!repoUrl || !filePath) return;
    setSaving(true);
    await invoke('saveConfig', { repoUrl, filePath });
    const cfg = await invoke('getConfig');
    setConfig(cfg);
    setSaving(false);
  };

  const check = async () => {
    setChecking(true);
    const result = await invoke('getStaleness', { repoUrl: config.repoUrl, filePath: config.filePath });
    setStaleness(result);
    setChecking(false);
  };

  if (loading) return <Text>Loading LivingDocs...</Text>;

  if (!config) {
    return (
      <Stack space="space.100">
        <Text weight="bold">🌿 LivingDocs — Link this page to a source file</Text>
        <Textfield label="GitHub Repo URL" placeholder="https://github.com/owner/repo" value={repoUrl} onChange={e => setRepoUrl(e.target.value)} />
        <Textfield label="File Path" placeholder="src/Tax.cs" value={filePath} onChange={e => setFilePath(e.target.value)} />
        <Button appearance="primary" isLoading={saving} onClick={save}>Save & Track</Button>
      </Stack>
    );
  }

  return (
    <Stack space="space.100">
      <Text weight="bold">🌿 LivingDocs</Text>
      <Text>Tracking: {config.filePath}</Text>
      {!staleness && <Button appearance="primary" isLoading={checking} onClick={check}>Check Staleness</Button>}
      {staleness && staleness.error && <Text>⚠️ {staleness.error}</Text>}
      {staleness && !staleness.error && (
        <Stack space="space.050">
          <Text>Staleness: {staleness.stalenessScore}% — {staleness.isStale ? '⚠️ Stale' : '✅ Fresh'}</Text>
          <Text>Last code change: {staleness.daysSinceChange} days ago</Text>
          <Text>Commit: {staleness.commitSha} — {staleness.commitMessage}</Text>
          {staleness.isStale && <Text>💡 Run write_docs or sync_confluence in Claude to refresh.</Text>}
        </Stack>
      )}
      <Button spacing="compact" onClick={() => { setConfig(null); setStaleness(null); }}>Reconfigure</Button>
    </Stack>
  );
}

ForgeReconciler.render(<App />);

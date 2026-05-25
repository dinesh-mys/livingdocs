import React, { useState, useEffect } from 'react';
import ForgeReconciler, {
  Text, Button, Stack, Textfield, Badge, Box, Inline, Heading
} from '@forge/react';
import { invoke } from '@forge/bridge';

const StalenessLabel = ({ score }) => {
  if (score < 40) return <Badge appearance="success">Fresh — {score}%</Badge>;
  if (score < 70) return <Badge appearance="inprogress">Stale — {score}%</Badge>;
  return <Badge appearance="removed">Critical — {score}%</Badge>;
};

function App() {
  const [config, setConfig]   = useState(null);
  const [loading, setLoading] = useState(true);
  const [repoUrl, setRepoUrl] = useState('');
  const [filePath, setFilePath] = useState('');
  const [data, setData]       = useState(null);
  const [saving, setSaving]   = useState(false);
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

  const analyze = async () => {
    setChecking(true);
    const result = await invoke('analyzeFile', {
      repoUrl: config.repoUrl,
      filePath: config.filePath
    });
    setData(result);
    setChecking(false);
  };

  if (loading) return <Text>Loading LivingDocs...</Text>;

  if (!config) {
    return (
      <Stack space="space.150">
        <Heading as="h4">🌿 LivingDocs — Link this page to a source file</Heading>
        <Textfield
          label="GitHub Repo URL"
          placeholder="https://github.com/owner/repo"
          value={repoUrl}
          onChange={e => setRepoUrl(e.target.value)}
        />
        <Textfield
          label="File Path"
          placeholder="src/Auth.cs"
          value={filePath}
          onChange={e => setFilePath(e.target.value)}
        />
        <Button appearance="primary" isLoading={saving} onClick={save}>
          Save &amp; Track
        </Button>
      </Stack>
    );
  }

  return (
    <Stack space="space.150">
      <Inline spread="space-between" alignBlock="center">
        <Heading as="h4">🌿 LivingDocs</Heading>
        <Text color="color.text.subtlest">{config.filePath}</Text>
      </Inline>

      {!data && (
        <Button appearance="primary" isLoading={checking} onClick={analyze}>
          Analyse File
        </Button>
      )}

      {data && data.error && (
        <Text color="color.text.danger">⚠️ {data.error}</Text>
      )}

      {data && !data.error && (
        <Stack space="space.125">

          {/* Staleness */}
          <Inline space="space.100" alignBlock="center">
            <Text weight="bold">Staleness</Text>
            <StalenessLabel score={data.staleness.stalenessScore} />
          </Inline>
          <Text color="color.text.subtlest">
            Last changed {data.staleness.daysSinceChange}d ago · {data.staleness.commitSha} {data.staleness.commitMessage}
          </Text>

          {/* Doc comments */}
          <Inline space="space.100" alignBlock="center">
            <Text weight="bold">Documentation</Text>
            {data.hasDocComments
              ? <Badge appearance="success">Comments found</Badge>
              : <Badge appearance="removed">No doc comments</Badge>
            }
          </Inline>

          {/* Departure risk */}
          <Inline space="space.100" alignBlock="center">
            <Text weight="bold">Departure Risk</Text>
            {data.departureRisk
              ? <Badge appearance="removed">⚠️ {data.departureRisk.topAuthor} owns {data.departureRisk.percentage}%</Badge>
              : <Badge appearance="success">Distributed</Badge>
            }
          </Inline>

          {/* Last synced */}
          <Text color="color.text.subtlest">
            {data.lastSynced
              ? `Last synced by LivingDocs: ${new Date(data.lastSynced).toLocaleDateString()}`
              : 'Not yet synced by LivingDocs'}
          </Text>

          {/* Action hint */}
          {(data.staleness.isStale || !data.hasDocComments || data.departureRisk) && (
            <Box padding="space.100" backgroundColor="color.background.warning">
              <Text>
                💡 Run <Text weight="bold">sync_confluence</Text> in Claude Desktop or VS Code to refresh this page.
              </Text>
            </Box>
          )}

          <Inline space="space.100">
            <Button spacing="compact" isLoading={checking} onClick={analyze}>Refresh</Button>
            <Button spacing="compact" appearance="subtle" onClick={() => { setConfig(null); setData(null); }}>
              Reconfigure
            </Button>
          </Inline>

        </Stack>
      )}
    </Stack>
  );
}

ForgeReconciler.render(<App />);

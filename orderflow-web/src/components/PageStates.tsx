import { Alert, AlertTitle, Box, Button, CircularProgress, Stack, Typography } from '@mui/material';
import { toApiError } from '../api/errors';

export function LoadingState({ label = 'Loading' }: { label?: string }) {
  return (
    <Stack role="status" spacing={2} sx={{ py: 6, alignItems: 'center' }}>
      <CircularProgress aria-hidden />
      <Typography color="text.secondary">{label}…</Typography>
    </Stack>
  );
}

export function EmptyState({ title, hint }: { title: string; hint?: string }) {
  return (
    <Box sx={{ py: 6, textAlign: 'center' }}>
      <Typography variant="h6" component="p">{title}</Typography>
      {hint && <Typography color="text.secondary">{hint}</Typography>}
    </Box>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const problem = toApiError(error);
  return (
    <Alert severity="error" action={onRetry && <Button color="inherit" size="small" onClick={onRetry}>Try again</Button>}>
      <AlertTitle>{problem.message}</AlertTitle>
      {problem.correlationId && (
        <Typography variant="body2" data-testid="correlation-id">
          Reference for support: <code>{problem.correlationId}</code>
        </Typography>
      )}
    </Alert>
  );
}

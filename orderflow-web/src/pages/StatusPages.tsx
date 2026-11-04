import { Button, Stack, Typography } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';

function StatusPage({ title, message }: { title: string; message: string }) {
  return (
    <Stack spacing={2} sx={{ py: 6, textAlign: 'center', alignItems: 'center' }}>
      <Typography variant="h4" component="h1">{title}</Typography>
      <Typography color="text.secondary">{message}</Typography>
      <Button component={RouterLink} to="/">Back to products</Button>
    </Stack>
  );
}

export const NotFoundPage = () => <StatusPage title="Page not found" message="This page does not exist or has moved." />;
export const ForbiddenPage = () => <StatusPage title="No access" message="Your account does not have permission to view this page." />;

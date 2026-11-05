import { Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { AuditEntry, Paged } from '../../types/api';

export function AuditLogPage() {
  const query = useQuery({ queryKey: ['audit-logs'], queryFn: async () => (await api.get<Paged<AuditEntry>>('/admin/audit-logs')).data });
  if (query.isPending) return <LoadingState label="Loading the audit log" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />;
  if (query.data.items.length === 0) return <EmptyState title="Nothing has been audited yet" />;
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Audit log</Typography>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>When (UTC)</TableCell>
              <TableCell>Action</TableCell>
              <TableCell>Target</TableCell>
              <TableCell>Reason</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {query.data.items.map((entry) => (
              <TableRow key={entry.id}>
                <TableCell>{entry.occurredAtUtc}</TableCell>
                <TableCell>{entry.action}</TableCell>
                <TableCell>{entry.targetType} {entry.targetId}</TableCell>
                <TableCell>{entry.reason}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </Stack>
  );
}

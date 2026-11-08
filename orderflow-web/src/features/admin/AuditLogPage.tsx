import { Stack, TextField, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../../api/client';
import { DataTable } from '../../components/DataTable';
import { formatDate } from '../../components/format';
import { Pager, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { AuditEntry, Paged } from '../../types/api';

export function AuditLogPage() {
  const { page, get, update } = useListParams();
  const correlationId = get('correlationId');
  const [draft, setDraft] = useState(correlationId);
  const query = useQuery({
    queryKey: ['audit-logs', correlationId, page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<AuditEntry>>('/admin/audit-logs', { params: { correlationId: correlationId || undefined, page } })).data
  });
  const apply = (event: FormEvent) => { event.preventDefault(); update({ correlationId: draft.trim() || null }); };
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Audit log</Typography>
      <form onSubmit={apply}><TextField size="small" label="Correlation ID" value={draft} onChange={(event) => setDraft(event.target.value)} sx={{ minWidth: 340 }} /></form>
      {query.isPending ? <LoadingState label="Loading the audit log" />
        : query.isError ? <ErrorState error={query.error} onRetry={() => query.refetch()} />
        : query.data.items.length === 0 ? <EmptyState title="Nothing has been audited yet" />
        : (
          <>
            <DataTable
              rowKey={(entry) => entry.id}
              rows={query.data.items}
              columns={[
                { header: 'When', render: (entry) => formatDate(entry.occurredAtUtc) },
                { header: 'Action', render: (entry) => entry.action },
                { header: 'Target', render: (entry) => `${entry.targetType} ${entry.targetId ?? ''}` },
                { header: 'Reason', render: (entry) => entry.reason },
                { header: 'Correlation ID', render: (entry) => <code>{entry.correlationId}</code> }
              ]}
            />
            <Pager page={query.data.page} totalPages={query.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
    </Stack>
  );
}

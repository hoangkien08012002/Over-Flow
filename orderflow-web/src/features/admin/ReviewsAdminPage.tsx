import { Button, Link, Stack, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { Pager, StatusFilter, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { Paged, Review } from '../../types/api';

const schema = z.object({ reason: z.string().trim().min(1, 'Moderation needs a reason.').max(500) });

export function ReviewsAdminPage() {
  const { page, get, update } = useListParams();
  const status = get('status');
  const [moderating, setModerating] = useState<Review | null>(null);
  const reviews = useQuery({
    queryKey: ['admin-reviews', status, page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Review>>('/admin/reviews', { params: { status: status || undefined, page } })).data
  });
  const moderate = useAction(({ review, reason }: { review: Review; reason: string }) =>
    api.post(`/admin/reviews/${review.id}/moderate`, { status: review.status === 'Published' ? 'Hidden' : 'Published', reason, version: review.version }), [['admin-reviews'], ['reviews']]);
  const target = moderating?.status === 'Published' ? 'Hide' : 'Publish';

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Reviews</Typography>
      <StatusFilter value={status} options={['Published', 'Hidden']} onChange={(value) => update({ status: value || null })} />
      {reviews.isPending ? <LoadingState label="Loading reviews" />
        : reviews.isError ? <ErrorState error={reviews.error} onRetry={() => reviews.refetch()} />
        : reviews.data.items.length === 0 ? <EmptyState title="No reviews found" />
        : (
          <>
            <DataTable
              rowKey={(review) => review.id}
              rows={reviews.data.items}
              columns={[
                { header: 'Product', render: (review) => <Link component={RouterLink} to={`/products/${review.productId}`}>View</Link> },
                { header: 'Rating', align: 'right', render: (review) => review.rating },
                { header: 'Comment', render: (review) => review.comment },
                { header: 'Status', render: (review) => <StatusChip status={review.status} /> },
                { header: 'Moderation reason', render: (review) => review.moderationReason },
                { header: '', render: (review) => <Button size="small" onClick={() => setModerating(review)}>{review.status === 'Published' ? 'Hide' : 'Publish'}</Button> }
              ]}
            />
            <Pager page={reviews.data.page} totalPages={reviews.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
      <FormDialog
        open={moderating !== null} title={`${target} this review`} submitLabel={target} schema={schema}
        fields={[{ name: 'reason', label: 'Reason', multiline: true, helperText: "The customer's text is kept unchanged." }]} defaults={{ reason: '' }}
        onSubmit={({ reason }) => moderate.mutateAsync({ review: moderating!, reason })} onClose={() => setModerating(null)}
      />
    </Stack>
  );
}

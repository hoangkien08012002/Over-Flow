import { Button, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Category } from '../../types/api';

const schema = z.object({
  name: z.string().trim().min(1, 'Enter a name.').max(150),
  slug: z.string().trim().min(1, 'Enter a slug.').max(180),
  description: z.string().trim().max(1000).optional(),
  isActive: z.boolean()
});

export function CategoriesAdminPage() {
  const [editing, setEditing] = useState<Category | 'new' | null>(null);
  const categories = useQuery({ queryKey: ['categories', 'all'], staleTime: 0, queryFn: async () => (await api.get<Category[]>('/categories', { params: { includeInactive: true } })).data });
  const save = useAction((values: z.infer<typeof schema>) => {
    const category = editing === 'new' ? null : editing;
    return category ? api.put(`/categories/${category.id}`, { ...values, version: category.version }) : api.post('/categories', values);
  }, [['categories']]);
  const activate = useAction((category: Category) => api.put(`/categories/${category.id}/activation`, { isActive: !category.isActive, version: category.version }), [['categories']]);

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Categories</Typography>
      <Button variant="contained" sx={{ alignSelf: 'flex-start' }} onClick={() => setEditing('new')}>New category</Button>
      {activate.error && <ErrorState error={activate.error} />}
      {categories.isPending ? <LoadingState label="Loading categories" />
        : categories.isError ? <ErrorState error={categories.error} onRetry={() => categories.refetch()} />
        : categories.data.length === 0 ? <EmptyState title="No categories yet" />
        : (
          <DataTable
            rowKey={(category) => category.id}
            rows={categories.data}
            columns={[
              { header: 'Name', render: (category) => category.name },
              { header: 'Slug', render: (category) => category.slug },
              { header: 'Status', render: (category) => (category.isActive ? 'Active' : 'Inactive') },
              { header: '', render: (category) => (
                <Stack direction="row" spacing={1}>
                  <Button size="small" aria-label={`Edit ${category.name}`} onClick={() => setEditing(category)}>Edit</Button>
                  <Button size="small" aria-label={`${category.isActive ? 'Deactivate' : 'Activate'} ${category.name}`} onClick={() => activate.mutate(category)}>{category.isActive ? 'Deactivate' : 'Activate'}</Button>
                </Stack>
              ) }
            ]}
          />
        )}
      <FormDialog
        open={editing !== null}
        title={editing === 'new' ? 'New category' : 'Edit category'}
        schema={schema}
        defaults={editing && editing !== 'new' ? { ...editing, description: editing.description ?? '' } : { name: '', slug: '', description: '', isActive: true }}
        fields={[
          { name: 'name', label: 'Name' },
          { name: 'slug', label: 'Slug', helperText: 'Unique, used in links.' },
          { name: 'description', label: 'Description', multiline: true },
          { name: 'isActive', label: 'Active', type: 'checkbox' }
        ]}
        onSubmit={(values) => save.mutateAsync(values)}
        onClose={() => setEditing(null)}
      />
    </Stack>
  );
}

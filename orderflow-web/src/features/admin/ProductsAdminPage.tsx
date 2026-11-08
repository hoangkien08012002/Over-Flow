import { Button, Stack, TextField, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { formatMoney } from '../../components/format';
import { Pager, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Category, Paged, Product } from '../../types/api';

const schema = z.object({
  categoryId: z.string().min(1, 'Choose a category.'),
  sku: z.string().trim().min(1, 'Enter a SKU.').max(64),
  name: z.string().trim().min(1, 'Enter a name.').max(200),
  description: z.string().trim().max(4000).optional(),
  price: z.coerce.number({ message: 'Enter a price.' }).min(0, 'A price cannot be negative.').multipleOf(0.01, 'Use at most two decimal places.'),
  currencyCode: z.string().trim().regex(/^[A-Z]{3}$/, 'Use a three-letter code such as VND.'),
  isActive: z.boolean()
});

const blank = { categoryId: '', sku: '', name: '', description: '', price: '', currencyCode: 'VND', isActive: true };

export function ProductsAdminPage() {
  const { params, page, get, update } = useListParams();
  const [search, setSearch] = useState(get('search'));
  const [editing, setEditing] = useState<Product | 'new' | null>(null);
  const categories = useQuery({ queryKey: ['categories', 'all'], queryFn: async () => (await api.get<Category[]>('/categories', { params: { includeInactive: true } })).data });
  const products = useQuery({
    queryKey: ['products', 'admin', params.toString()],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Product>>('/products', { params: { includeInactive: true, search: get('search') || undefined, page } })).data
  });
  const save = useAction((values: z.infer<typeof schema>) => {
    const product = editing === 'new' ? null : editing;
    return product ? api.put(`/products/${product.id}`, { ...values, version: product.version }) : api.post('/products', values);
  }, [['products']]);
  const activate = useAction((product: Product) => api.put(`/products/${product.id}/activation`, { isActive: !product.isActive, version: product.version }), [['products']]);
  const applySearch = (event: FormEvent) => { event.preventDefault(); update({ search: search.trim() || null }); };

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Products</Typography>
      <Stack direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap' }}>
        <form onSubmit={applySearch}><TextField size="small" label="Search" value={search} onChange={(event) => setSearch(event.target.value)} /></form>
        <Button variant="contained" onClick={() => setEditing('new')}>New product</Button>
      </Stack>
      {activate.error && <ErrorState error={activate.error} />}
      {products.isPending ? <LoadingState label="Loading products" />
        : products.isError ? <ErrorState error={products.error} onRetry={() => products.refetch()} />
        : products.data.items.length === 0 ? <EmptyState title="No products found" />
        : (
          <>
            <DataTable
              rowKey={(product) => product.id}
              rows={products.data.items}
              columns={[
                { header: 'Name', render: (product) => product.name },
                { header: 'SKU', render: (product) => product.sku },
                { header: 'Category', render: (product) => product.categoryName },
                { header: 'Price', align: 'right', render: (product) => formatMoney(product.price, product.currencyCode) },
                { header: 'Status', render: (product) => (product.isActive ? 'Active' : 'Inactive') },
                { header: '', render: (product) => (
                  <Stack direction="row" spacing={1}>
                    <Button size="small" aria-label={`Edit ${product.name}`} onClick={() => setEditing(product)}>Edit</Button>
                    <Button size="small" aria-label={`${product.isActive ? 'Deactivate' : 'Activate'} ${product.name}`} onClick={() => activate.mutate(product)}>{product.isActive ? 'Deactivate' : 'Activate'}</Button>
                  </Stack>
                ) }
              ]}
            />
            <Pager page={products.data.page} totalPages={products.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
      <FormDialog
        open={editing !== null}
        title={editing === 'new' ? 'New product' : 'Edit product'}
        schema={schema}
        defaults={editing && editing !== 'new' ? { ...editing, description: editing.description ?? '' } : blank}
        fields={[
          { name: 'categoryId', label: 'Category', type: 'select', options: (categories.data ?? []).filter((category) => category.isActive).map((category) => ({ value: category.id, label: category.name })) },
          { name: 'sku', label: 'SKU' },
          { name: 'name', label: 'Name' },
          { name: 'description', label: 'Description', multiline: true },
          { name: 'price', label: 'Price', type: 'number' },
          { name: 'currencyCode', label: 'Currency' },
          { name: 'isActive', label: 'Active', type: 'checkbox' }
        ]}
        onSubmit={(values) => save.mutateAsync(values)}
        onClose={() => setEditing(null)}
      />
    </Stack>
  );
}

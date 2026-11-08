import { Button, Checkbox, FormControlLabel, Link, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { api } from '../../api/client';
import { DataTable } from '../../components/DataTable';
import { formatMoney } from '../../components/format';
import { Pager, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Category, Paged, Product } from '../../types/api';

const sorts = [
  { value: 'name', label: 'Name A–Z' },
  { value: 'name_desc', label: 'Name Z–A' },
  { value: 'price', label: 'Price, low to high' },
  { value: 'price_desc', label: 'Price, high to low' },
  { value: 'newest', label: 'Newest' }
];

export function ProductListPage() {
  const { params, page, get, update } = useListParams();
  const [search, setSearch] = useState(get('search'));
  const [minPrice, setMinPrice] = useState(get('minPrice'));
  const [maxPrice, setMaxPrice] = useState(get('maxPrice'));
  const rangeInvalid = minPrice !== '' && maxPrice !== '' && Number(minPrice) > Number(maxPrice);
  const categories = useQuery({ queryKey: ['categories'], queryFn: async () => (await api.get<Category[]>('/categories')).data });
  const query = useQuery({
    queryKey: ['products', params.toString()],
    queryFn: async () => (await api.get<Paged<Product>>('/products', { params: { ...Object.fromEntries(params), page, pageSize: 20 } })).data,
    placeholderData: keepPreviousData
  });

  const apply = (event: FormEvent) => {
    event.preventDefault();
    if (!rangeInvalid) update({ search: search.trim() || null, minPrice: minPrice || null, maxPrice: maxPrice || null });
  };

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Products</Typography>
      <Stack component="form" role="search" onSubmit={apply} direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <TextField size="small" label="Search" value={search} onChange={(event) => setSearch(event.target.value)} />
        <TextField size="small" select label="Category" value={get('categoryId')} onChange={(event) => update({ categoryId: event.target.value || null })} sx={{ minWidth: 160 }}>
          <MenuItem value="">All</MenuItem>
          {categories.data?.map((category) => <MenuItem key={category.id} value={category.id}>{category.name}</MenuItem>)}
        </TextField>
        <TextField size="small" type="number" label="Min price" value={minPrice} onChange={(event) => setMinPrice(event.target.value)} sx={{ width: 130 }} slotProps={{ htmlInput: { min: 0 } }} />
        <TextField size="small" type="number" label="Max price" value={maxPrice} onChange={(event) => setMaxPrice(event.target.value)} sx={{ width: 130 }} slotProps={{ htmlInput: { min: 0 } }} error={rangeInvalid} helperText={rangeInvalid ? 'Below the minimum' : undefined} />
        <TextField size="small" select label="Sort" value={get('sort') || 'name'} onChange={(event) => update({ sort: event.target.value })} sx={{ minWidth: 170 }}>
          {sorts.map((sort) => <MenuItem key={sort.value} value={sort.value}>{sort.label}</MenuItem>)}
        </TextField>
        <FormControlLabel label="In stock only" control={<Checkbox checked={get('isAvailable') === 'true'} onChange={(event) => update({ isAvailable: event.target.checked ? 'true' : null })} />} />
        <Button type="submit" variant="outlined">Apply</Button>
      </Stack>
      {query.isPending ? <LoadingState label="Loading products" />
        : query.isError ? <ErrorState error={query.error} onRetry={() => query.refetch()} />
        : query.data.items.length === 0 ? <EmptyState title="No products found" hint="Try a different search or filter." />
        : (
          <>
            <DataTable
              rowKey={(product) => product.id}
              rows={query.data.items}
              columns={[
                { header: 'Name', render: (product) => <Link component={RouterLink} to={`/products/${product.id}`}>{product.name}</Link> },
                { header: 'Category', render: (product) => product.categoryName },
                { header: 'Price', align: 'right', render: (product) => formatMoney(product.price, product.currencyCode) },
                { header: 'Available', align: 'right', render: (product) => product.available }
              ]}
            />
            <Pager page={query.data.page} totalPages={query.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
    </Stack>
  );
}

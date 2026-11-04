import { Button, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Paged, Product } from '../../types/api';

export function ProductListPage() {
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get('page')) || 1);
  const query = useQuery({
    queryKey: ['products', page],
    queryFn: async () => (await api.get<Paged<Product>>('/products', { params: { page, pageSize: 20 } })).data,
    placeholderData: keepPreviousData
  });

  if (query.isPending) return <LoadingState label="Loading products" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />;
  if (query.data.items.length === 0) return <EmptyState title="No products yet" hint="Products appear here once an administrator adds them." />;

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Products</Typography>
      <TableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Name</TableCell>
              <TableCell>Category</TableCell>
              <TableCell align="right">Price</TableCell>
              <TableCell align="right">Available</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {query.data.items.map((product) => (
              <TableRow key={product.id}>
                <TableCell>{product.name}</TableCell>
                <TableCell>{product.categoryName}</TableCell>
                <TableCell align="right">{product.price.toLocaleString()} {product.currencyCode}</TableCell>
                <TableCell align="right">{product.available}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <Button disabled={page <= 1} onClick={() => setParams({ page: String(page - 1) })}>Previous</Button>
        <Typography variant="body2">Page {query.data.page} of {Math.max(1, query.data.totalPages)}</Typography>
        <Button disabled={page >= query.data.totalPages} onClick={() => setParams({ page: String(page + 1) })}>Next</Button>
      </Stack>
    </Stack>
  );
}

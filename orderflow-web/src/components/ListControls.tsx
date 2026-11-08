import { Button, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { useSearchParams } from 'react-router-dom';

// List state lives in the URL, so a filtered page survives a reload and can be shared with a colleague.
export function useListParams() {
  const [params, setParams] = useSearchParams();
  const update = (changes: Record<string, string | null>) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      Object.entries(changes).forEach(([key, value]) => (value ? next.set(key, value) : next.delete(key)));
      if (!('page' in changes)) next.delete('page');
      return next;
    });
  return { params, page: Math.max(1, Number(params.get('page')) || 1), get: (key: string) => params.get(key) ?? '', update };
}

export function Pager({ page, totalPages, onChange }: { page: number; totalPages: number; onChange: (page: number) => void }) {
  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
      <Button disabled={page <= 1} onClick={() => onChange(page - 1)}>Previous</Button>
      <Typography variant="body2">Page {page} of {Math.max(1, totalPages)}</Typography>
      <Button disabled={page >= totalPages} onClick={() => onChange(page + 1)}>Next</Button>
    </Stack>
  );
}

export function StatusFilter({ value, options, onChange }: { value: string; options: readonly string[]; onChange: (value: string) => void }) {
  return (
    <TextField select label="Status" size="small" value={value} onChange={(event) => onChange(event.target.value)} sx={{ minWidth: 200, alignSelf: 'flex-start' }}>
      <MenuItem value="">All</MenuItem>
      {options.map((option) => <MenuItem key={option} value={option}>{option}</MenuItem>)}
    </TextField>
  );
}

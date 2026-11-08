import { Table, TableBody, TableCell, TableContainer, TableHead, TableRow } from '@mui/material';
import type { ReactNode } from 'react';

export interface Column<T> {
  header: string;
  render: (row: T) => ReactNode;
  align?: 'right';
}

export function DataTable<T>({ columns, rows, rowKey }: { columns: Column<T>[]; rows: T[]; rowKey: (row: T) => string }) {
  return (
    <TableContainer>
      <Table size="small">
        <TableHead>
          <TableRow>
            {columns.map((column, index) => <TableCell key={index} align={column.align}>{column.header}</TableCell>)}
          </TableRow>
        </TableHead>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={rowKey(row)}>
              {columns.map((column, index) => <TableCell key={index} align={column.align}>{column.render(row)}</TableCell>)}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

import { Box, Link, List, ListItem, ListItemText, Stack, Typography } from '@mui/material';
import type { ReactNode } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { DataTable, type Column } from '../../components/DataTable';
import { formatDate, formatMoney } from '../../components/format';
import type { OrderDetail, OrderItem, Shipment, StatusChange } from '../../types/api';

export function History({ title = 'History', entries }: { title?: string; entries: StatusChange[] }) {
  return (
    <Box>
      <Typography variant="h6" component="h2">{title}</Typography>
      <List dense data-testid="history">
        {entries.map((entry) => (
          <ListItem key={`${entry.toStatus}-${entry.changedAtUtc}`} disableGutters>
            <ListItemText primary={entry.toStatus} secondary={`${formatDate(entry.changedAtUtc)}${entry.reason ? ` · ${entry.reason}` : ''}`} />
          </ListItem>
        ))}
      </List>
    </Box>
  );
}

export function ShipmentPanel({ shipment }: { shipment: Shipment }) {
  return (
    <Box data-testid="shipment">
      <Typography variant="h6" component="h2">Shipment</Typography>
      <Typography>Tracking code: <code>{shipment.trackingCode}</code> · {shipment.status}</Typography>
      <History title="Tracking" entries={shipment.history} />
    </Box>
  );
}

// Shared by the customer page and the staff page. All amounts are the server's own snapshot.
export function OrderSummary({ detail, itemAction }: { detail: OrderDetail; itemAction?: (item: OrderItem) => ReactNode }) {
  const { order, items, deliveryAddress: address } = detail;
  const columns: Column<OrderItem>[] = [
    { header: 'Product', render: (item) => <Link component={RouterLink} to={`/products/${item.productId}`}>{item.productNameSnapshot}</Link> },
    { header: 'SKU', render: (item) => item.skuSnapshot },
    { header: 'Unit price', align: 'right', render: (item) => formatMoney(item.unitPrice, order.currencyCode) },
    { header: 'Quantity', align: 'right', render: (item) => item.quantity },
    { header: 'Subtotal', align: 'right', render: (item) => formatMoney(item.lineSubtotal, order.currencyCode) }
  ];
  if (itemAction) columns.push({ header: '', render: itemAction });
  return (
    <Stack spacing={2}>
      <DataTable rowKey={(item) => item.id} rows={items} columns={columns} />
      <Box data-testid="totals">
        <Typography>Items: {formatMoney(order.subtotal, order.currencyCode)}</Typography>
        <Typography data-testid="discount">Discount: {formatMoney(order.discountTotal, order.currencyCode)}</Typography>
        <Typography>Shipping ({detail.shippingMethodName}): {formatMoney(order.shippingFee, order.currencyCode)}</Typography>
        <Typography variant="h6" data-testid="grand-total">Total: {formatMoney(order.grandTotal, order.currencyCode)}</Typography>
        <Typography color="text.secondary">Payment: {detail.paymentMethod}</Typography>
      </Box>
      {address && (
        <Box>
          <Typography variant="h6" component="h2">Delivery address</Typography>
          <Typography>{address.recipientName} · {address.phone}</Typography>
          <Typography>{address.line1}, {address.ward}, {address.district}, {address.province}, {address.countryCode}</Typography>
        </Box>
      )}
    </Stack>
  );
}

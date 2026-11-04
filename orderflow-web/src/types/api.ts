export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface Product {
  id: string;
  sku: string;
  name: string;
  categoryName: string;
  price: number;
  currencyCode: string;
  available: number;
}

export interface Order {
  id: string;
  orderNumber: string;
  status: string;
  grandTotal: number;
  currencyCode: string;
}

export interface AuditEntry {
  id: string;
  action: string;
  targetType: string;
  targetId: string | null;
  reason: string | null;
  correlationId: string;
  occurredAtUtc: string;
}

export interface TokenResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
}

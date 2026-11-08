export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface Category {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  isActive: boolean;
  version: string;
}

export interface Product {
  id: string;
  categoryId: string;
  categoryName: string;
  sku: string;
  name: string;
  description: string | null;
  price: number;
  currencyCode: string;
  isActive: boolean;
  available: number;
  version: string;
}

export interface ShippingMethod {
  id: string;
  code: string;
  name: string;
  fee: number;
  currencyCode: string;
  isActive?: boolean;
  version?: string;
}

export interface CartLine {
  id: string;
  productId: string;
  quantity: number;
  currencyCode: string | null;
  productName: string | null;
  currentUnitPrice: number;
  available: number;
  unavailable: boolean;
  lineSubtotal: number;
}

export interface Cart {
  id: string | null;
  items: CartLine[];
  subtotal: number;
}

export type PaymentMethod = 'COD' | 'BankTransferSimulation' | 'CardSimulation';

export interface DeliveryAddress {
  recipientName: string;
  phone: string;
  line1: string;
  ward: string;
  district: string;
  province: string;
  countryCode: string;
}

export interface CheckoutRequest {
  cartId: string;
  shippingMethodId: string;
  paymentMethod: PaymentMethod;
  couponCode: string | null;
  deliveryAddress: DeliveryAddress;
}

export interface Order {
  id: string;
  orderNumber: string;
  status: string;
  subtotal: number;
  discountTotal: number;
  shippingFee: number;
  grandTotal: number;
  currencyCode: string;
  statusUrl: string;
}

export interface OrderItem {
  id: string;
  productId: string;
  skuSnapshot: string;
  productNameSnapshot: string;
  unitPrice: number;
  quantity: number;
  lineSubtotal: number;
  discountAmount: number;
}

export interface StatusChange {
  fromStatus: string | null;
  toStatus: string;
  changedAtUtc: string;
  reason: string | null;
  correlationId: string;
}

export interface OrderDetail {
  order: Order;
  items: OrderItem[];
  history: StatusChange[];
  deliveryAddress: DeliveryAddress | null;
  paymentMethod: string;
  shippingMethodName: string;
}

export interface Shipment {
  id: string;
  orderId: string;
  status: string;
  trackingCode: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  history: StatusChange[];
}

export interface ReturnLine {
  id: string;
  orderItemId: string;
  productId: string;
  quantity: number;
  restocked: boolean | null;
}

export interface ReturnRequest {
  id: string;
  orderId: string;
  userId: string;
  status: string;
  reason: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  items: ReturnLine[];
  history: StatusChange[];
}

export interface WishlistItem {
  productId: string;
  name: string;
  price: number;
  currencyCode: string;
  createdAtUtc: string;
  isAvailable: boolean;
}

export interface PublicReview {
  id: string;
  rating: number;
  comment: string | null;
  createdAtUtc: string;
  authorName: string;
}

export interface ReviewList extends Paged<PublicReview> {
  summary: { count: number; averageRating: number | null };
}

export interface Review {
  id: string;
  productId: string;
  orderId: string;
  rating: number;
  comment: string | null;
  status: string;
  moderationReason: string | null;
  createdAtUtc: string;
  version: string;
}

export interface InventorySnapshot {
  productId: string;
  onHand: number;
  reserved: number;
  available: number;
  lowStockThreshold: number;
  version: string;
}

export interface Reservation {
  id: string;
  orderId: string;
  productId: string;
  quantity: number;
  status: string;
  expiresAtUtc: string;
}

export interface Coupon {
  id: string;
  code: string;
  discountType: 'Fixed' | 'Percentage';
  discountValue: number;
  minimumSubtotal: number;
  validFromUtc: string;
  validUntilUtc: string;
  totalUsageLimit: number | null;
  perUserUsageLimit: number | null;
  usageCount: number;
  isEnabled: boolean;
  version: string;
}

export interface CouponUsage {
  coupon: Coupon;
  activeUsages: number;
  returnedUsages: number;
  discountGiven: number;
}

export interface FailedMessage {
  id: string;
  consumerName: string;
  sourceTopic: string;
  eventId: string | null;
  eventType: string | null;
  attemptCount: number;
  errorSummary: string;
  failedAtUtc: string;
  status: string;
}

export interface FailedRefund {
  id: string;
  orderId: string;
  status: string;
  amount: number;
  currencyCode: string;
  reason: string | null;
  attemptCount: number;
  updatedAtUtc: string;
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

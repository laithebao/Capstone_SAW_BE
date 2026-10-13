# Distributor UC 48 49 50 51

## Scope

This implementation adds only the Distributor catalog and purchase-order flow. Approval, inventory reservation, picking, goods issue and Warehouse Manager screens are outside this change. Existing report files are unchanged. Payment remains external.

- UC 48: own order list with search, status/date filters and 10/20/50-row pagination.
- UC 49: select 1–20 different whole lots and submit a pending order with delivery information.
- UC 50: own order detail/history and receipt confirmation after all requested weight has a committed goods issue.
- UC 51: cancel only an unprocessed pending order, with a reason.

## Database installation

Use EF Core Code First as the normal installation workflow. From `Capstone_SAW_BE`, run:

```powershell
dotnet ef database update --project SAW/SAW.Infrastructure --startup-project SAW/SAW.API
```

Commit the entity/configuration changes, both files for each migration (`AddDistributorWholeLotOrders`, `AddDistributorOrderNumbering` and `MakeDistributorRequestedReceiptDateOptional`) and `AppDbContextModelSnapshot.cs` with the feature. Teammates configure their own connection string and run the same command; no database export is needed in Git. Do not commit local credentials or database backups. The migrations add `BATCH_SALE_OFFER`, receipt-confirmation fields, a separate retry request ID and a daily purchase-order counter. Existing public order codes remain intact.

`DistributorWholeLotOrders.sql` is an optional generated deployment script for environments that use SSMS instead of EF tooling. It applies only this migration and records it in `__EFMigrationsHistory`. Do not run the entire exported database creation script to install this feature.

For each batch to sell, enter the batch ID and the agreed whole-lot price in `SetDistributorLotPrice.sql` and execute it. Prices are not invented or seeded automatically. Set `IsPublished=0` to stop offering a lot. Pricing management UI is deliberately not included because it belongs to another role/UC.

Only published, positively priced, unexpired `IN_STOCK` lots with latest completed PASS QC (A–D), active crop/storage locations, no reservation or committed issue, and on-hand weight equal to verified whole-lot weight appear in the catalog. A lot linked to an order in any status other than `REJECTED` or `CANCELLED` is hidden from every distributor while that order remains active or has progressed. Rejected/cancelled orders release catalog visibility if the lot still meets all stock/QC/expiry rules. The catalog may therefore be empty until receiving and pricing data exist.

## Requested receipt date

The form labels this field “Ngày mong muốn nhận hàng” and leaves it empty by default. It is optional and expresses the distributor’s preference, not a confirmed delivery schedule. If supplied it must be today or later using Vietnam calendar dates. If omitted the API persists NULL and the list/detail display “Không yêu cầu ngày cụ thể”. The existing `ExpectedDeliveryDate` column/API field name is retained for compatibility; migration `MakeDistributorRequestedReceiptDateOptional` makes the database column nullable while preserving existing dates.

## Whole-lot order contract

Each order line stores `RequestedQuantity=1`, `Unit=Lô`, `UnitPrice=whole-lot price`, and `RequestedWeightKg=full verified lot weight`. The existing computed `LineSubtotal=RequestedQuantity*UnitPrice` therefore yields the whole-lot price without changing the formula or other UCs. Order and line taxes are zero; no payment status is introduced.

The browser sends batch IDs plus expected price/weight, never an editable purchase quantity. The server rechecks current prices, QC, stock and other orders inside a serializable transaction, rejects stale selections, and calculates totals itself. An idempotency request UUID prevents duplicate submission after a network retry. A pending order hides its lots from the catalog immediately, without creating a warehouse inventory reservation. Rejected/cancelled orders release catalog visibility; approval must atomically recheck availability and reserve the entire selected lot, and must not partially approve or substitute a different batch.

New orders use `PO-YYYYMMDD-01`, `PO-YYYYMMDD-02`, etc., based on the Vietnam calendar date (UTC+7); after 99 the suffix continues to 100. `PURCHASE_ORDER_DAILY_COUNTER` allocates numbers atomically inside the order transaction and resets by date. `RequestId` is independent of the displayed order code and uniquely scoped to the distributor, preserving idempotent retries. The numbering migration backfills request IDs from the previous UUID-based codes and initializes counters after any pre-existing numeric daily codes. Existing order codes are not renamed.

Contact phone numbers must contain 10–30 ASCII digits only. The browser strips non-digits and enforces the minimum length; the API independently rejects short numbers, letters, spaces, signs and other characters.

Lot detail shows product/category, supplier name, growing area/locality, harvest/expiry dates, verified weight and packaging, expected storage conditions, current whole-lot price and latest QC result/completion. The applied inspection standard name and version are resolved through that same QC record’s `InspectionStandardVersionId` to its version and standard set, never by choosing the newest currently published standard. It is readable for a currently saleable lot, or a lot referenced by the requesting distributor's own order; other unlisted lots return 404. Order ownership does not make an unavailable lot purchasable again. Internal supplier contact/tax information and warehouse locations are not exposed. The list selection is preserved when navigating through lot detail.

Cancellation checks ownership and locks the order before rechecking `PENDING`, absence of approval, reservations and goods issues. Receipt confirmation accepts `ISSUED` or `DISPATCHED` only after every line has its full requested weight approved and issued through committed goods issues; it records the buyer and UTC timestamp and transitions to `DELIVERED`. Repeating either successful operation is idempotent. Production status-history triggers are reused without creating duplicate history rows.

## API and frontend

All endpoints require the `DISTRIBUTOR` role and an active own distributor profile/account.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| GET | `/api/distributor/orders/dashboard` | Own all-order totals and four most recent orders |
| GET | `/api/distributor/orders/catalog` | Saleable whole lots |
| GET | `/api/distributor/orders/catalog/{id}` | Distributor lot detail |
| GET | `/api/distributor/orders` | Own orders |
| GET | `/api/distributor/orders/{id}` | Own detail and history |
| POST | `/api/distributor/orders` | Submit order |
| POST | `/api/distributor/orders/{id}/cancel` | Cancel pending order |
| POST | `/api/distributor/orders/{id}/receive` | Confirm receipt |

The Distributor sidebar links to `/distributor/catalog` and `/distributor/orders`. Lot detail is `/distributor/catalog/{id}`, checkout is `/distributor/orders/new`; order detail is `/distributor/orders/{id}`. UI includes loading, empty/error states, request retry, selection across catalog pages, confirmation forms and status refresh.

The dashboard at `/distributor` aggregates every order of the active authenticated distributor, independently of pagination. Pending is `PENDING`, successful is `DELIVERED` (receipt confirmed), cancelled is `CANCELLED`; spending sums only successful orders' stored `TotalAmount`. It reports order value rather than confirming external payment. Cards open the order list filtered by status, alongside four recent orders and shopping/tracking shortcuts. Statistics refresh when revisiting or refocusing the page. `REJECTED` remains a separate order status and is not counted as a distributor cancellation.

## Validation

Build the API and frontend; lint the changed frontend files. Unit tests are under `Application/DistributorOrders`. SQL/API tests under `Infrastructure/DistributorOrders` opt in through `SAW_TEST_SQL_CONNECTION`; they reuse only that server/login to create and drop an isolated GUID-named database, never modify the supplied warehouse database, and test pricing, whole-lot eligibility, idempotency, ownership, cancellation and receipt gating. The isolated schema includes the exported production order-history trigger and computed order totals.

Verified for the current change: API/test-project build in a separate `DistributorVerify` configuration, frontend production build and lint passed. All 26 Distributor tests passed with SQL and browser testing enabled, including Vietnam date rollover, concurrent sequential numbering, idempotent submission, phone validation, optional requested receipt date persistence/API/UI, applied QC standard version, lot visibility and the real Edge catalog → lot detail → checkout → own orders/detail → cancellation flow, role guards, mobile layout and network retry. Dashboard tests verify all-order aggregation beyond pagination, owner isolation, delivered-only spending, recent order sorting, cancellation refresh, card filters and mobile layout. The numbering migration was also tested against the exported schema/constraints/triggers and legacy order codes. Tests use isolated databases; business prices are not seeded automatically.

# Migration inventory: legacy action -> v2 endpoint

Source: every public action of every controller in `C:\jaftimv2\Jaftim\Jaftim\Controllers` (extracted 2026-09-17),
grouped by the v2 module that owns it. Status: `done` | `todo` | `drop` (dead/duplicate legacy code, with reason).
View-only actions that just render a page (`Index`, `*Detail` GET returning a View) map to the read endpoints the
page needs, not to an endpoint of their own.

Proposed route style: `api/<resource>` plural; ids in the path; `POST .../<verb>` for commands. The permission column
is the `RoleAction.ActionId` the legacy `_CSS_###` class used on that screen/button (see `Permissions.cs`).

## Module 0 - Platform (done)

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| Identity Login page | `POST /api/auth/login` (+ `tenantCode`), `/refresh`, `/switch-tenant`, `GET /api/auth/memberships`, `/logout`, `GET /api/auth/me`, `POST /api/auth/change-password` | - | done (catalog-backed, multi-tenant) |
| `BaseController` session/rights | JWT claims (`tid`, `uid`, primary `rid`) + `[HasPermission]` + `CachedPermissionService` (union over effective roles) | - | done |
| - (new) | `GET /api/tenants`, `GET /api/audit?entityType=&entityId=` | 304 | done |
| `_SidebarPartial.cshtml` (hand-written menu + `R_CSS` hiding, RoleId branches) | `GET /api/navigation/me` (data-driven tree filtered by effective permissions/roles), `GET/PUT/DELETE /api/navigation` (admin), `GET /api/permissions/catalog` (RoleAction tree) - `docs/NAVIGATION.md` | any / 304 | done |
| `BaseController` IP allowlist | `IpAllowlistMiddleware` | - | done |
| `BaseController.SaveActionURL` | `RequestAuditMiddleware` | - | done |
| `NotificationHub` | `/hubs/notifications` | - | done |
| `NotificationController` Summary/MarkRead/MarkAllRead/MarkAllSeen | `GET /api/notifications/summary`, `POST .../mark-all-seen`, `.../mark-all-read`, `.../{id}/mark-read` | auth | done |
| `NotificationController` List/Index | `GET /api/notifications?onlyUnread=&page=&pageSize=` (`Notification_GetByUser`; count + page, `PagedResult`) | auth | done |
| `HomeController`, `ExceptionController` | n/a | - | drop (views) |

## Module 1 - Lookups + Stock read

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `AuthFilterController.GetDllAuthTableValues` | `GET /api/lookups?tables=` | 2 | done |
| `AuthFilterController.GetAuthData` (SYS_AuthMethod keyword -> SP) | explicit endpoints per keyword: `GET /api/lookups/models?makeId=`, `/cities?countryId=`, `/discharge-ports?countryId=`, `/ports`, `/yards`, `/shipping-companies`, `/roles?userTypeId=`, `/reporting-line?roleId=`, `/shipment-parties?customerId=` (the 14 `SYS_Auth_*` procs) | 3 | todo |
| `APIController/FilterController.GetData` (SYS_PublicMethod) | public website - see Module 9 | - | todo |
| `APIController/BaseMakeController.BaseMakeGetAll` | covered by `/api/lookups?tables=Base_Make` | 2 | done |
| `ModelController.Base_Model_Save` | `POST /api/lookups/models` | 401? (confirm) | todo |
| `StockController.Index/IndexAsync` | `GET /api/stocks` (`StockGetAll_New`) | 417 | done |
| `StockController.StockDetail` | `GET /api/stocks/{id}` (`StockGetById` as the visibility gate, row from `StockGetAll_New ` so it equals the list row) + `GET /api/stocks/{id}/workflow` (`StockDetailWorkflowGetById`) + `GET /api/stocks/{id}/flags` (`GetStockFlagsByStockId`) + `GET /api/stocks/{id}/status` (`Stock_GetStatusDetail`) + `GET /api/stocks/{id}/profile-image` | 413/528/536/527 | done |
| `StockController.StockSectionGetById` | `GET /api/stocks/{id}/sections` (`Stock_GetSectionById`, grouped by SectionName) | 410 | done |
| `StockGetStatusDetailHistory`, `StockBLDetailHistory`, `StockPortDetailHistory`, `StockInspectionHistoryGetByStockId`, `StockFinancialActivityGetAll`, `StockActivityGetAll` (reservation history), `StockGetBossHistory` | `GET /api/stocks/{id}/history/{status|bl|portleave|inspection|financial|reservation}` and `/history/price` (JSON from the SP) | 557 | done |
| `StockImagesByFolderId`, `StockDocumentsByFolderId`, `StockVideoGetByStockId` | `GET /api/stocks/{id}/folders?kind=All|Documents|Images`, `/folders/{folderId}/files` (folder must belong to the stock), `/video` | 403/527 | done |
| `StockGetReservationBids` | `GET /api/stocks/{id}/reservation-bids` | 529 | done |
| `GetReservationBidCustomers`, `StockGetDiscountRequests` | `GET /api/stocks/{id}/reservation-bids/customers`, `GET /api/discount-requests` | 529/617 | todo |
| `StockFinancialGetAllByStockId` | `GET /api/stocks/{id}/financials?financialTypeId=` | 416 | done |
| `StockInternalNoteGetById` | `GET /api/stocks/{id}/notes` | 414 | done |
| `GetBossPriceByStockIdAndCurrencyId`, `GetCustomerDepositRatio`, `SearchShipmentPartyPhone` | `GET /api/stocks/{id}/boss-price?currencyId=`, `GET /api/customers/{id}/deposit-ratio`, `GET /api/shipment-parties/search?phone=` | 413 | todo |
| `ExportToExcel`, `GeneratePdf`, `DownloadStockTemplate` | `GET /api/stocks/export` (xlsx), `GET /api/stocks/{id}/reservation-pdf`, `GET /api/stocks/import-template` | 558 | todo (needs ClosedXML / PDF renderer decision - iText7 HTML->PDF used Razor views; pick a headless HTML renderer or a report template) |
| `SalesModuleController.Index` | `GET /api/sales-module/stocks` (`SalesModule_GetAllStock_Optimized`, ~50 filters) | 540 | todo |

## Module 2 - Users, roles, rights

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `UserController.UserGetAll` | `GET /api/users?userTypeId=` (`UserGetAll`; visibility branches on the caller PRIMARY role inside the SP) | 201 | done |
| `UserController.UserDetail` | `GET /api/users/{id}` (`UserGetById`) | 203 | done |
| `EmployeeSectionGetById`, `StockGetAllByEmployee` | `GET /api/users/{id}/sections/{name}`, `/stocks` | 204 | todo |
| `UserController.UserSave` (creates Identity user) | `POST /api/users` (catalog account reused-or-created + tenant `AspNetUsers` insert via `IPasswordHasher` + `UserSave` + `AccountTenant` membership; crypto-random temp password returned once), `PUT /api/users/{id}` (`UserSave`; email/login immutable). **Legacy quirks preserved**: `UserSave` leaves an orphan `UserProfileDetail` row with `UserProfileId = 0` on every insert; only `@CreatedBy = 1` can change `RemoteAccessAllowed`. | 202 | done |
| `UploadUserImage` (User + Customer) | `POST /api/users/{id}/image` | 202 | todo |
| `ActiveInActiveCall` | `POST /api/users/{id}/toggle-active` (+ `Catalog_Account_BumpTokenVersion` on deactivation, self-deactivation refused) | 202 | done |
| - (new: multiple / time-bound roles) | `GET/POST /api/users/{id}/roles`, `DELETE ./{roleId}`, `PUT ./primary` (`UserRole_*`, `UserProfile_SetPrimaryRole`) | 203/202 | done |
| `RolesGetAll`, `RoleSave`, `AssignRoles` (GET/POST), `GetRoleRightsByRoleId` | `GET /api/roles`, `PUT /api/roles`, `GET /api/roles/{id}/permissions` (tree + granted), `PUT /api/roles/{id}/permissions` (`RoleActionMapping_Replace`, ancestors added, cache invalidated, audited) | 301/302/304 | done |
| - (new) | `PUT /api/permissions` (create/rename/re-parent a RoleAction, ids 900+), `GET /api/permissions/catalog`; navigation upsert accepts `newPermissionName` - `docs/NAVIGATION.md` | 304 | done |
| `AssignEntities`, `UserEntityHierarchy`, `OrgChart` | `GET/PUT /api/users/{id}/entities`, `GET /api/users/{id}/hierarchy`, `GET /api/org-chart` | 548 | todo |
| `NotificationSettingsController.*` | `GET /api/notification-settings/types`, `/types/{id}`, `PUT /types/{id}/settings` (active, strategy, system-admins), `PUT /types/{id}/roles` (replaces the set), `PUT /types/{id}/users` (grant/deny one staff user), `DELETE /types/{id}/users/{userId}`, `GET /roles`, `GET /users?query=`. Every change audited; routing applies on the next raise (no cache). | 904 (legacy gated `RoleId IN (1,10)`) | done |

## Module 3 - Inquiry, leads, tagging, public

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `InquiryController.InquiryGetAll` | `GET /api/inquiries` (`GetInquiryAll`, all filters; `partyKind=Contact\|Customer` replaces the raw `@IsContact` flag). Row-level visibility still inside the procedure. | 538 | done |
| `InquiryDetail`, `ContactDetail` | `GET /api/inquiries/{id}` (fetched through the list, so an invisible row is 404, never someone else's); `GET /api/parties/{id}/kind` + `/inquiries` | 537 | done |
| `ContactSectionGetById` | `GET /api/parties/{id}/sections` (`Contact_GetSectionById`) | 106 | done |
| `InquirySectionGetById` | - | - | **drop (broken)**: `Inquiry_GetSectionById` selects from `Base_InquiryType`, a table that does not exist (confirmed on UAT; 1 procedure references it), so it throws on every call. `GET /api/inquiries/{id}` returns the fields instead. |
| - (new: Customer vs Contact as data) | `UserProfile.PartyKind` stored + indexed (`database/v2/005`), auto-maintained by the API and `PartyKindSyncJob`, human-overridable via `PUT /api/parties/{id}/kind`; `Customer.Qualified` audit event. Replaces the non-SARGable CASE repeated in 3 procedures - see `docs/INQUIRIES.md`. | 103/523 | done |
| `InquirySave` | `POST /api/inquiries` (create) · `PUT /api/inquiries/{id}` (update). `InquirySave` + login + `CustomerSave` run in ONE transaction and set `Inquiry.UserProfileId`; needs `database/v2/006` (see `docs/INQUIRIES.md` defect 0 - `CustomerSave` rejects every new enquirer on UAT today). | 539 | done |
| `BulkInquirySave` | `POST /api/inquiries/bulk` -> Hangfire job (`BulkInquiryImport_V2`, TVP) with progress | 539 | todo |
| `BulkInquirySave_`, `BulkInquirySave_DontUSE` | - | - | drop |
| `CheckEmail`, `CheckPhone` | `GET /api/inquiries/check-email?email=` · `GET /api/inquiries/check-phone?countryCode=&phone=` (kept as two probes, as the add form calls them) | 539 | done |
| `ContactList`, `ContactAdd` (CustomerContact log) | `GET/POST /api/parties/{id}/interactions` - named "interaction" because it is a touchpoint log, not a party | 103/608 | done |
| `CustomerController.ContactStatus` | `POST /api/inquiries/{id}/contact-status` (`Inquiry_ContactStatusSave`; appends `CustomerRemarks` history, refreshes the cached latest values, then re-qualifies the party) | 608 | done |
| `CustomerController.Contacted` | - | - | drop (superseded by contact status, README 6.3) |
| `TaggedFromInquiries` | `POST /api/inquiries/{id}/tag` (`Inquiry_TaggedFromInquiries`, audited, `CUSTOMER_TAGGED`; 422 for an unlinked inquiry) | 550 | done |
| `TaggedFromInquiriesBulk`, `UnTagFromInquiriesBulk` | `POST /api/inquiries/tag-bulk`, `/untag-bulk` - loops the single-row procedures, partial success, 422 only when nothing succeeded (`docs/INQUIRIES.md` "Tagging") | 550 | done |
| `CustomerTaggingController.*` | `GET /api/tagging/agents` (526), `/agents/{id}/customers` (551), `/agents/{id}/history` (552), `/me/customers`, `/me/history` (907), `/available-customers` (550); `POST /api/tagging/tag`, `/untag` (550, audited, CUSTOMER_TAGGED/UNTAGGED). Rejections are 422 and notify nobody. `docs/INQUIRIES.md` "Customer tagging" | 526/550-552/907 | done |
| `JaftimWebhooks` Function `POST /api/leads` | **Do not port without the owner's say-so.** Standing instruction (2026-09-23): the Azure Function keeps running as-is on its own connection string, and the whole ingestion chain (`InsertLead` -> `InquiryImport_FromLead` -> `InquirySave_FromLead` -> `CustomerSaveInternal`) stays untouched - `docs/INQUIRIES.md`, "Inbound leads are not handled here". A v2 `POST /api/public/leads` (bearer key vs `AuthorizationKeys`) is a *possible future* alternative, not queued work. | key | **not scheduled (owner constraint)** |
| `PublicController.InquirySave` | `POST /api/public/inquiries` (crypto-random password) | anonymous + captcha/rate limit | todo |

## Module 4 - Customer core, wallet, finance requests, My Task

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `CustomerGetAll` | `GET /api/customers` (`GetCustomerAll` / `GetCustomerAllNew` - confirm) | 101 | todo |
| `CustomerDetail`, `CustomerSectionGetById`, `CustomerAccountStatisticsById`, `CustomerInquiries`, `CustomerRemarks`, `AddRemarks` | `GET /api/customers/{id}`, `/sections/{name}`, `/statistics`, `/inquiries`, `GET/POST /remarks` | 103/106/546/547 | todo |
| `CustomerSave` (GET/POST), `BulkCustomerSave` | `POST/PUT /api/customers`, `POST /api/customers/bulk` (job) | 102/523 | todo |
| `BulkCustomerSave_NOTINUSE` | - | - | drop |
| `ChangeCustomerStatus`, `Status` | `POST /api/customers/{id}/status` | 523 | todo |
| `CustomerGetConsigneeAll`, `CustomerGetNotifyPartyAll`, `CustomerGetCourierInfoHistory` | `GET /api/customers/{id}/shipment-parties?type=2|3`, `/courier-history` | 554/555/556 | todo |
| `StockGetAllByCustomer`, `StockAllocationByCustomer`, `StockAllocationGetByCustomer`, `StockAllocationHistorySave`, `AllAllocationGetByCustomer`, `RemainingAllocationGetByCustomer`, `IncomeAllocationGetAllByCustomer`, `IncomeAllocationHistoryAllByCustomer`, `CancellationHistoryAllByCustomer` | `GET /api/customers/{id}/stocks`, `/allocations`, `/allocations/history`, `/allocations/remaining`, `/income-allocations`, `/income-allocations/history`, `/cancellations`; `POST /api/customers/{id}/allocations` | 104/105/542/544/545 | todo |
| `StockDeallocationByCustomer`, `StockDeallocationSave`, `LoadDeallocationRequest`, `GetDeallocationRequests`, `DeallocationRequestApprovalSave` | `GET/POST /api/customers/{id}/deallocations`, `GET /api/deallocation-requests`, `POST .../{id}/decide` | 578/600-602/616 | todo |
| `GetCustomerStatement`, `GetCustomerStatementRows` | `GET /api/customers/{id}/statement?walletId=` | 559 | todo |
| `WalletConversion`, `ConversionRequest`, `ConversionRequestFromAgent`, `ConversionRequestSave`, `PendingConversionRequest`, `GetAllWalletConversion` | `GET /api/customers/{id}/wallets`, `POST /api/customers/{id}/conversions`, `GET /api/conversion-requests`, `POST .../{id}/decide` | 525/543/613 | todo |
| `RefundRequest` (GET/POST), `GetPendingRefundRequests`, `GetPendingRefundRequest`, `RefundRequestApprovalSave` | `POST /api/customers/{id}/refund-requests`, `GET /api/refund-requests`, `/{id}`, `POST /{id}/decide` | 614 | todo |
| `AdjustmentRequest`, `AdjustmentRequestSave`, `LoadAdjustmentRequest`, `CustomerAdjustmentGetById`, `GetAdjustmentRequests`, `AdjustmentRequestApprovalSave` | `POST /api/stocks/{stockId}/adjustments`, `GET /api/adjustment-requests`, `/{id}`, `POST /{id}/decide` | 562/615 | todo |
| `CustomerBalanceForfeiture*` (4) , `GetCustomerBalanceForfeitures` | `GET/POST /api/customers/{id}/forfeitures`, `POST .../{id}/release` | 579/581 | todo |
| `CustomerWalletTransfer*` (5), `SearchCustomerWalletTransferTargets`, `GetCustomerWalletTransfers` | `GET/POST /api/wallet-transfers`, `POST /{id}/decide`, `GET /api/wallet-transfers/targets?q=` | 621-625 | todo |
| `CustomerConversationLogs` | `GET /api/customers/{id}/conversations` (Respond.io mirror, SAS links) | 629 | todo |
| `SendResetLink` | `POST /api/auth/forgot-password` (token table needed - Identity's data-protection tokens go away) | anon | todo |
| `UserController.MyTask`, `TaskHistory`, `RemittanceRequests`, `ShippingRequests`, `RejectShippingRequest` | `GET /api/my-tasks/{remittances|conversions|refunds|adjustments|deallocations|discounts|shipping|wallet-transfers}?history=` (the 14 `MyTask*_GetPage` SPs), `POST /api/shipping-requests/{id}/reject` | 524 + 612-618/624 | todo |
| `BankController.BaseBankGetAll/Save` | `GET/POST /api/banks` | 500 | todo |
| `BankStatementController.Index/RemittancePage/AddStatement/DownloadSampleFile/SaveConfirmStatment` | `GET /api/bank-statements`, `POST /api/bank-statements` (CSV upload -> `BankStatement_UploadFile` TVP), `GET /api/bank-statements/sample` | 501/503 | todo |
| `BankStatementReconcilation`, `BankStatementReconcilationApproval`, `IncomeReconcilation`, `IncomeReconcilationGetAll` | `POST /api/bank-statements/{detailId}/reconcile`, `POST /api/reconciliations/{id}/decide`, `GET /api/income-reconciliations` | 502/504/521/522 | todo |

## Module 5 - Stock write

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `StockSave` | `POST /api/stocks` (`StockSave`, then enqueue `LegacyErpStockSyncJob` for types 1/2, notify STOCK_CREATED) | 419 | todo |
| `BulkStockSave` | `POST /api/stocks/bulk` (job; `BulkStockUpload` TVP - pass audit params explicitly) | 419 | todo |
| `StockDetailSave`, `StockSpecificationSave`, `StockWeightDimensionlSave`, `StockCostDetailSave`, `StockRefundSave`, `StockPriceSave`, `StockInspectionSave`, `StockShipmentDetailSave`, `StockBLDetailSave`, `StockPortDetailSave`, `StockCourierInfoSave`, `StockReservationDetailSave`, `StockClassificationSave`, `StockFeatureSave` | `PUT /api/stocks/{id}/{detail|specification|weight-dimension|cost|refund|price|inspection|shipment|bl|port|courier|reservation|classification|features}` - each a `SaveAuthTable`/`UpdateAuthTable("<table>", json)` call kept **server-side** with a typed DTO (never accept table names from the client) | 401/420/423/422/406/424/408/418/412/411/426/404/402 | todo |
| `StockInspectionSaveNoAuth` | - | - | drop unless a caller is found |
| `StockStatusSave` | `POST /api/stocks/{id}/status` (`Stock_SaveStatusDetail`; only ids 14,12,10,9,8,6,7,13,2 accepted - validator) | 405 | todo |
| `Stock_MarkReadyForSale`, `Stock_MarkAvailableForSale`, `Stock_MarkNotSold`, `ArchiveStock`, `Stock_SendToGarage`, `StockInGarage`, `Stock_PreSaleGarageSave`, `Stock_PostSaleGarageSave` | `POST /api/stocks/{id}/{ready-for-sale|available-for-sale|not-sold|archive|send-to-garage}`, `GET/PUT /api/stocks/{id}/garage/{pre|post}` (+ notifications) | 568/569/571/572/575/576 | todo |
| `StockReservationBidsDetailSave` | `POST/PUT /api/stocks/{id}/reservation-bids` (controller validation: FOB >= Boss, no decrease on edit; `Stock_Financial_Save`; notify BID_RAISED) | 404/529 | todo |
| `ConfirmApproveBid`, `ConfirmApproveBidFinance`, `ConfirmRejectBidFinance` | `POST /api/reservation-bids/{id}/approve` (branch `check_discounted_bid` vs `StockReservation_ConfirmApproveBid`, read returned ErrorNumber/ErrorMessage result set), `/approve-finance`, `/reject-finance` (+ notifications) | 529/617 | todo |
| `StockFinancialSave`, `StockFinancialDelete`, `StockCostRepairSave/Delete`, `StockModificationPriceSave/Delete` | `POST/DELETE /api/stocks/{id}/financials`, `/repair-costs`, `/modification-prices` | 407/425/561/580 | todo |
| `StockShipmentPartySave` | `PUT /api/stocks/{id}/shipment-parties/{type}` (`SaveShipmentParty`) | 421/619/620 | todo |
| `StockFileSave`, `StockFileDelete`, `UploadStockImage`, `StockVideoSave` | `POST /api/stocks/{id}/folders/{folderId}/files`, `DELETE .../files/{fileId}`, `POST /api/stocks/{id}/images` (thumbnail rule: profile image only from Stock Images folder), `POST /api/stocks/{id}/videos` (+ enqueue image sync) | 409/403/527 | todo |
| `StockInternalNoteSave` | `POST /api/stocks/{id}/notes` | 415 | todo |
| `StockCourierInfoNotifyManagers`, `StockSurrenderRequest` | `POST /api/stocks/{id}/courier/notify`, `POST /api/stocks/{id}/surrender-request` | 426 / confirm | todo |
| `PricingModuleController.*` | `GET /api/pricing/stocks` (`prc_get_stocks`), `POST /api/pricing/stocks/{id}/price` (`prc_stock_price_updates_save`), `GET .../history`, `POST /api/pricing/bulk-percentage`, `POST /api/pricing/bulk-upload`, `GET /api/pricing/export` | 577 | todo |

## Module 6 - Shipping, Document, Inspection, Vendor, Transport

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `ShippingController.*` (17) | `GET /api/shipping-schedules?status=&stockId=`, `GET/POST/PUT/DELETE /api/shipping-schedules/{id}`, `/vessels`, `/loading-ports`, `/{id}/assignable-stocks`, `POST /{id}/assign-stocks`, `/containers` CRUD, `/{id}/assign-container-stocks`, `/{id}/complete`, `/{id}/freight-status`, `/{id}/stocks/{stockId}/job-number` | 611/599/610 | todo |
| `DocumentController.*` (4) | `GET /api/documents/stocks`, `/stocks/{id}/files`, `POST /api/documents/tracking` (ACL rules for OBL folder + Paid-before-OBL for Sales Executive) | 590-598 | todo |
| `InspectionController.*` (3) | `GET /api/inspections/stocks`, `POST /api/inspections` (`InspectionModule_Save`) | 603/604 | todo |
| `VendorController.*` (7) | `GET /api/vendors`, `/{id}`, `/cities?countryId=`, `GET /api/vendors/stock-by-chassis?chassis=`, `POST /api/vendors`, `/jobs`, `/settlements` | 605-607/609 | todo |
| `TransportController.*` (21; already `/api/transport/*` in legacy) | port routes 1:1: `GET /api/transport/config`, `/suburbs`, `/admin/suburbs`, `/locations`, `/pricing-rules`, `/quotes`, `/admin/quotes`, `/audit`; `POST /api/transport/quote`, `/settings`, `/country`, `/bodytypeprofit`, `/bodytypepolicy`, `/location`, `/route`, `/pricingrule`, `/import` (optimistic `revision`) | 630-632 (`TransportModule/TransportQuote/TransportAdmin` css classes) | todo |

## Module 7 - Reporting

| Legacy | v2 | Perm | Status |
|---|---|---|---|
| `ReportController.Index` | `GET /api/reports/dashboard?from=&to=` (`GetReportingDashboardData`), `GET /api/reports` (`RPT_Get_Report_Sections`) | 549 | todo |
| `ViewReport` GET | `GET /api/reports/{id}/definition` (master + filters + columns; lookup procs executed for filter options - whitelist `RPT_Lookup_*`) | 549 | todo |
| `ViewReport` POST, `Export` | `POST /api/reports/{id}/run` (paged; `ReportMaster.ProcedureName` must match `^RPT_` and exist in `sys.procedures`), `POST /api/reports/{id}/export` | 549 | todo |

## Module 8 - Integrations (Jobs)

| Legacy | v2 | Status |
|---|---|---|
| StockJourneyStatusUpdates (4 loops) | `StockJourneyStatusJob`, `StockStatusOutboxEnqueueJob`, `StockStatusOutboxDispatchJob`, `StockStatusRefreshOneJob` - all per tenant | done. **Legacy defect reproduced faithfully:** 8 stocks on the UAT copy fail `StockStatus_RefreshOne` inside `RefreshCustomerStatus` (`CustomerStatus.FK_CustomerId` NULL); they sit at `AttemptCount = 5` on UAT too |
| - (new) | `AccountSyncJob` (tenant `AspNetUsers`+`UserProfile` -> catalog `Account`/`AccountTenant`, per tenant, every minute), `TenantJobsRegistrarJob` (per-tenant schedule reconciliation, every 5 min) | done |
| StockSync `Worker`, `ImagesSyncWorker` | `LegacyErpStockSyncJob.SyncStockAsync/SyncImagesAsync` | skeleton |
| RespondIOSync x3 | `RespondIoContactSyncJob`, `RespondIoCustomerPushJob`, `RespondIoConversationSyncJob` | skeleton |
| `EmailService` (forgot password) | `IEmailSender` + a `SendEmailJob` | sender done, job todo |
| Legacy `AzureQueueService` producer in `StockController` | replaced by `IJobScheduler.Enqueue<LegacyErpStockSyncJob>` | done (abstraction) |

## Module 9 - Public website procedures (`web_*`, `WEB_*`, `SYS_Public_*`)

Not called by the legacy MVC app (README section 15) except `web_sync_*`. The public site queries them directly.
Decision needed: expose them as `/api/public/*` (API-key auth via `WEB_ApiKeys`, which already exists) so the public
site stops talking to the database directly. Out of scope until the owner decides.

## Dropped legacy code (do not port)

`EF_*Controller` x22 (scaffolded EF CRUD on lookup tables - replace with `POST/PUT /api/lookups/{table}` only for
tables in `SYS_DropDownsWithAuth`, admin-permissioned), `UpdateJsFileAsync` (generated `dlls/*.js`), `CMSController`,
`ApproveBidManager` SP, `RefundClaim*`, `TestLead`, `AuthFilterController.GetAuthData` generic keyword indirection
(replaced by explicit endpoints), `RoleRedirectFilter` (replaced by permissions).

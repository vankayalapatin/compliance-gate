// Design-time snapshot of the ErpDbContext model. Kept in sync with
// OnModelCreating so future migrations scaffold a correct diff.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
public partial class ErpDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "8.0.0");

        modelBuilder.Entity("Gate.Erp.ConfigRow", b =>
        {
            b.Property<string>("Key").HasColumnType("TEXT");
            b.Property<string>("Value").HasColumnType("TEXT");
            b.HasKey("Key");
            b.ToTable("Config", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.SequenceRow", b =>
        {
            b.Property<string>("Name").HasColumnType("TEXT");
            b.Property<long>("Next").HasColumnType("INTEGER");
            b.Property<string>("Format").HasColumnType("TEXT");
            b.Property<int>("Padding").HasColumnType("INTEGER");
            b.HasKey("Name");
            b.ToTable("Sequences", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.IdempotencyRow", b =>
        {
            b.Property<string>("Key").HasColumnType("TEXT");
            b.Property<string>("Payload").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.HasKey("Key");
            b.ToTable("IdempotencyKeys", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.ItemRow", b =>
        {
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<string>("Description").HasColumnType("TEXT");
            b.Property<bool>("IsCritical").HasColumnType("INTEGER");
            b.Property<int>("UnitsPerCarton").HasColumnType("INTEGER");
            b.Property<string>("Unit").HasColumnType("TEXT");
            b.HasKey("ItemId");
            b.ToTable("Items", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.SalesOrderRow", b =>
        {
            b.Property<string>("SoNumber").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<string>("Currency").HasColumnType("TEXT");
            b.HasKey("SoNumber");
            b.ToTable("SalesOrders", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.SalesOrderLineRow", b =>
        {
            b.Property<string>("SoNumber").HasColumnType("TEXT");
            b.Property<int>("LineNo").HasColumnType("INTEGER");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<decimal>("Quantity").HasColumnType("TEXT");
            b.HasKey("SoNumber", "LineNo");
            b.ToTable("SalesOrderLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.BomRow", b =>
        {
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<string>("ComponentItemId").HasColumnType("TEXT");
            b.Property<decimal>("QtyPer").HasColumnType("TEXT");
            b.HasKey("ItemId", "ComponentItemId");
            b.ToTable("BomLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.InventoryRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<string>("ReceiptRef").HasColumnType("TEXT");
            b.Property<string>("LotRef").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<decimal>("Quantity").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("ItemId");
            b.ToTable("InventoryTransactions", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.VendorRow", b =>
        {
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("Name").HasColumnType("TEXT");
            b.Property<string>("CountryOfOrigin").HasColumnType("TEXT");
            b.Property<bool>("IsBlocked").HasColumnType("INTEGER");
            b.Property<string>("BlockReason").HasColumnType("TEXT");
            b.Property<string>("BlockedBy").HasColumnType("TEXT");
            b.Property<string>("BlockedAt").HasColumnType("TEXT");
            b.Property<string>("PaymentTermsId").HasColumnType("TEXT");
            b.Property<string>("Email").HasColumnType("TEXT");
            b.HasKey("VendorId");
            b.ToTable("Vendors", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.VendorDocumentRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("Kind").HasColumnType("TEXT");
            b.Property<string>("Reference").HasColumnType("TEXT");
            b.Property<bool>("Present").HasColumnType("INTEGER");
            b.HasKey("RowId");
            b.HasIndex("VendorId");
            b.ToTable("VendorDocuments", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PaymentTermsRow", b =>
        {
            b.Property<string>("TermsId").HasColumnType("TEXT");
            b.Property<int>("NetDays").HasColumnType("INTEGER");
            b.HasKey("TermsId");
            b.ToTable("PaymentTerms", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.VendorBankRow", b =>
        {
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("AccountRef").HasColumnType("TEXT");
            b.Property<bool>("Verified").HasColumnType("INTEGER");
            b.Property<string>("VerifiedAt").HasColumnType("TEXT");
            b.HasKey("VendorId");
            b.ToTable("VendorBanks", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.VendorPriceRow", b =>
        {
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<decimal>("UnitPrice").HasColumnType("TEXT");
            b.HasKey("VendorId", "ItemId");
            b.ToTable("VendorPrices", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.LastPriceRow", b =>
        {
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<decimal>("UnitPrice").HasColumnType("TEXT");
            b.HasKey("ItemId");
            b.ToTable("LastPrices", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.LegacyShareRow", b =>
        {
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<decimal>("Share").HasColumnType("TEXT");
            b.HasKey("VendorId", "ItemId");
            b.ToTable("LegacyShares", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.UserRow", b =>
        {
            b.Property<string>("UserId").HasColumnType("TEXT");
            b.Property<string>("Role").HasColumnType("TEXT");
            b.Property<decimal>("SigningLimit").HasColumnType("TEXT");
            b.Property<string>("LimitCurrency").HasColumnType("TEXT");
            b.HasKey("UserId");
            b.ToTable("Users", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.DelegationRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("FromUser").HasColumnType("TEXT");
            b.Property<string>("ToUser").HasColumnType("TEXT");
            b.Property<string>("Start").HasColumnType("TEXT");
            b.Property<string>("End").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.ToTable("Delegations", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.CompanyInfoRow", b =>
        {
            b.Property<string>("EntityCode").HasColumnType("TEXT");
            b.Property<string>("LegalName").HasColumnType("TEXT");
            b.Property<string>("Currency").HasColumnType("TEXT");
            b.Property<string>("BillToAddress").HasColumnType("TEXT");
            b.Property<string>("ShipToAddress").HasColumnType("TEXT");
            b.Property<string>("SenderEmail").HasColumnType("TEXT");
            b.HasKey("EntityCode");
            b.ToTable("company_info", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.SiteRow", b =>
        {
            b.Property<string>("SiteId").HasColumnType("TEXT");
            b.Property<string>("Name").HasColumnType("TEXT");
            b.Property<string>("Address").HasColumnType("TEXT");
            b.Property<string>("Capabilities").HasColumnType("TEXT");
            b.HasKey("SiteId");
            b.ToTable("sites", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.InventoryJournalRow", b =>
        {
            b.Property<string>("JournalId").HasColumnType("TEXT");
            b.Property<string>("JournalType").HasColumnType("TEXT");
            b.Property<string>("PostingDate").HasColumnType("TEXT");
            b.Property<string>("Reason").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.HasKey("JournalId");
            b.ToTable("inventory_journals", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.InventoryJournalLineRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("JournalId").HasColumnType("TEXT");
            b.Property<int>("LineNo").HasColumnType("INTEGER");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<string>("LotRef").HasColumnType("TEXT");
            b.Property<decimal>("Quantity").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("JournalId");
            b.ToTable("inventory_journal_lines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PrRow", b =>
        {
            b.Property<string>("PrNumber").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<string>("Requester").HasColumnType("TEXT");
            b.Property<string>("Currency").HasColumnType("TEXT");
            b.Property<decimal>("EstimatedAmount").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("ApprovedBy").HasColumnType("TEXT");
            b.Property<string>("ApprovedAt").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.HasKey("PrNumber");
            b.ToTable("PurchaseRequisitions", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PrLineRow", b =>
        {
            b.Property<string>("PrNumber").HasColumnType("TEXT");
            b.Property<int>("LineNo").HasColumnType("INTEGER");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<decimal>("Quantity").HasColumnType("TEXT");
            b.Property<string>("Unit").HasColumnType("TEXT");
            b.HasKey("PrNumber", "LineNo");
            b.ToTable("PurchaseRequisitionLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PoRow", b =>
        {
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("PrNumber").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("PaymentTermsId").HasColumnType("TEXT");
            b.Property<string>("ApprovalStatus").HasColumnType("TEXT");
            b.Property<string>("DocStatus").HasColumnType("TEXT");
            b.Property<string>("Currency").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("ApprovedBy").HasColumnType("TEXT");
            b.Property<string>("ApprovedAt").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.HasKey("PoNumber");
            b.ToTable("PurchaseOrders", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PoLineRow", b =>
        {
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<int>("LineNumber").HasColumnType("INTEGER");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<decimal>("QtyOrdered").HasColumnType("TEXT");
            b.Property<decimal>("QtyReceived").HasColumnType("TEXT");
            b.Property<decimal>("QtyInvoiced").HasColumnType("TEXT");
            b.Property<decimal>("QtyQuarantined").HasColumnType("TEXT");
            b.Property<decimal>("QtyRejected").HasColumnType("TEXT");
            b.Property<decimal>("UnitPrice").HasColumnType("TEXT");
            b.Property<decimal>("LineAmount").HasColumnType("TEXT");
            b.HasKey("PoNumber", "PoLineRef");
            b.ToTable("PurchaseOrderLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PoNoteRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("Note").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("PoNumber");
            b.ToTable("PurchaseOrderNotes", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.ConfirmationRow", b =>
        {
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("ConfirmDate").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.HasKey("PoNumber");
            b.ToTable("VendorConfirmations", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.ConfirmationLineRow", b =>
        {
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<decimal>("ConfirmedQty").HasColumnType("TEXT");
            b.Property<string>("BackorderNote").HasColumnType("TEXT");
            b.HasKey("PoNumber", "PoLineRef");
            b.ToTable("VendorConfirmationLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.AsnRow", b =>
        {
            b.Property<string>("AsnNumber").HasColumnType("TEXT");
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.HasKey("AsnNumber");
            b.ToTable("Asns", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.AsnCartonRow", b =>
        {
            b.Property<string>("CartonId").HasColumnType("TEXT");
            b.Property<string>("AsnNumber").HasColumnType("TEXT");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.Property<int>("UnitsPerCarton").HasColumnType("INTEGER");
            b.Property<string>("LotRef").HasColumnType("TEXT");
            b.HasKey("CartonId");
            b.HasIndex("AsnNumber");
            b.ToTable("AsnCartons", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.ReceiptRow", b =>
        {
            b.Property<string>("ReceiptNumber").HasColumnType("TEXT");
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("SupplierPackingSlipNumber").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("PostedBy").HasColumnType("TEXT");
            b.Property<string>("PostedAt").HasColumnType("TEXT");
            b.Property<string>("PostingDate").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.HasKey("ReceiptNumber");
            b.HasIndex("PoNumber");
            b.ToTable("ShipmentReceipts", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.ReceiptLineRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("ReceiptNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<decimal>("QtyReceived").HasColumnType("TEXT");
            b.Property<decimal>("QtyQuarantined").HasColumnType("TEXT");
            b.Property<decimal>("QtyRejected").HasColumnType("TEXT");
            b.Property<string>("LotRef").HasColumnType("TEXT");
            b.Property<string>("ConditionNote").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("ReceiptNumber");
            b.ToTable("ShipmentReceiptLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.LotMapRow", b =>
        {
            b.Property<string>("LotRef").HasColumnType("TEXT");
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<string>("ItemId").HasColumnType("TEXT");
            b.HasKey("LotRef");
            b.ToTable("LotMap", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.HoldRow", b =>
        {
            b.Property<string>("HoldId").HasColumnType("TEXT");
            b.Property<string>("ReasonCode").HasColumnType("TEXT");
            b.Property<string>("Note").HasColumnType("TEXT");
            b.Property<string>("RuleId").HasColumnType("TEXT");
            b.Property<string>("CaseId").HasColumnType("TEXT");
            b.Property<string>("SetBy").HasColumnType("TEXT");
            b.Property<string>("SetAt").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<string>("DocumentType").HasColumnType("TEXT");
            b.Property<string>("DocumentNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<string>("LotRef").HasColumnType("TEXT");
            b.Property<string>("CartonId").HasColumnType("TEXT");
            b.HasKey("HoldId");
            b.ToTable("Holds", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.HoldReleaseRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("HoldId").HasColumnType("TEXT");
            b.Property<string>("ReleasedBy").HasColumnType("TEXT");
            b.Property<string>("ReleasedAt").HasColumnType("TEXT");
            b.Property<string>("ApprovalRef").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("HoldId");
            b.ToTable("HoldReleases", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.InvoiceRow", b =>
        {
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<string>("PoNumber").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<string>("Voucher").HasColumnType("TEXT");
            b.Property<string>("Currency").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("ApprovedBy").HasColumnType("TEXT");
            b.Property<string>("ApprovedAt").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.Property<string>("PostedBy").HasColumnType("TEXT");
            b.Property<string>("PostedAt").HasColumnType("TEXT");
            b.Property<string>("DueDate").HasColumnType("TEXT");
            b.HasKey("VendorId", "SupplierInvoiceNumber");
            b.ToTable("VendorInvoices", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.InvoiceLineRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<decimal>("Quantity").HasColumnType("TEXT");
            b.Property<decimal>("UnitPrice").HasColumnType("TEXT");
            b.Property<string>("Disposition").HasColumnType("TEXT");
            b.Property<string>("HoldReason").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("VendorId", "SupplierInvoiceNumber");
            b.ToTable("VendorInvoiceLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.MatchResultRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<bool>("Matched").HasColumnType("INTEGER");
            b.Property<string>("Message").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("VendorId", "SupplierInvoiceNumber");
            b.ToTable("MatchResults", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.HeldReleaseRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<string>("PoLineRef").HasColumnType("TEXT");
            b.Property<decimal>("Quantity").HasColumnType("TEXT");
            b.Property<string>("ReleasedBy").HasColumnType("TEXT");
            b.Property<string>("ReleasedAt").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.ToTable("HeldReleases", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PaymentRow", b =>
        {
            b.Property<string>("PaymentNumber").HasColumnType("TEXT");
            b.Property<string>("Status").HasColumnType("TEXT");
            b.Property<string>("Currency").HasColumnType("TEXT");
            b.Property<string>("PayeeAccountRef").HasColumnType("TEXT");
            b.Property<bool>("PayeeVerified").HasColumnType("INTEGER");
            b.Property<string>("DueDate").HasColumnType("TEXT");
            b.Property<string>("Source").HasColumnType("TEXT");
            b.Property<string>("CreatedBy").HasColumnType("TEXT");
            b.Property<string>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("ApprovedBy").HasColumnType("TEXT");
            b.Property<string>("ApprovedAt").HasColumnType("TEXT");
            b.Property<string>("PostedBy").HasColumnType("TEXT");
            b.Property<string>("PostedAt").HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").HasColumnType("TEXT");
            b.HasKey("PaymentNumber");
            b.ToTable("Payments", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PaymentLineRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("PaymentNumber").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<decimal>("Amount").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("PaymentNumber");
            b.ToTable("PaymentLines", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.SettlementRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<string>("PaymentNumber").HasColumnType("TEXT");
            b.Property<decimal>("SettledAmount").HasColumnType("TEXT");
            b.Property<decimal>("RemainingBalance").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("VendorId", "SupplierInvoiceNumber");
            b.ToTable("Settlements", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.PaidAmountRow", b =>
        {
            b.Property<string>("VendorId").HasColumnType("TEXT");
            b.Property<string>("SupplierInvoiceNumber").HasColumnType("TEXT");
            b.Property<decimal>("Amount").HasColumnType("TEXT");
            b.HasKey("VendorId", "SupplierInvoiceNumber");
            b.ToTable("PaidAmounts", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.ApprovalRow", b =>
        {
            b.Property<string>("RowId").HasColumnType("TEXT");
            b.Property<string>("DocumentType").HasColumnType("TEXT");
            b.Property<string>("DocumentNumber").HasColumnType("TEXT");
            b.Property<string>("RoutedTo").HasColumnType("TEXT");
            b.Property<string>("ActedBy").HasColumnType("TEXT");
            b.Property<bool>("ActingAsDelegate").HasColumnType("INTEGER");
            b.Property<string>("Timestamp").HasColumnType("TEXT");
            b.Property<bool>("Approved").HasColumnType("INTEGER");
            b.Property<string>("Comment").HasColumnType("TEXT");
            b.Property<decimal>("DocumentAmount").HasColumnType("TEXT");
            b.Property<decimal>("LimitChecked").HasColumnType("TEXT");
            b.HasKey("RowId");
            b.HasIndex("DocumentType", "DocumentNumber");
            b.ToTable("Approvals", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.RequesterRow", b =>
        {
            b.Property<string>("DocumentType").HasColumnType("TEXT");
            b.Property<string>("DocumentNumber").HasColumnType("TEXT");
            b.Property<string>("Requester").HasColumnType("TEXT");
            b.HasKey("DocumentType", "DocumentNumber");
            b.ToTable("Requesters", (string)null);
        });

        modelBuilder.Entity("Gate.Erp.RoutedToRow", b =>
        {
            b.Property<string>("DocumentType").HasColumnType("TEXT");
            b.Property<string>("DocumentNumber").HasColumnType("TEXT");
            b.Property<string>("RoutedTo").HasColumnType("TEXT");
            b.Property<string>("SubmittedBy").HasColumnType("TEXT");
            b.HasKey("DocumentType", "DocumentNumber");
            b.ToTable("RoutedTo", (string)null);
        });
    }
}

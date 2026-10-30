// Real mini-ERP store: EF Core over sqlite. Journals are queried by
// document keys; every write in the adapter runs inside a transaction.
// Schema is created by EF migrations (see Gate.Erp/Migrations).
using Microsoft.EntityFrameworkCore;

namespace Gate.Erp;

public sealed class ErpDbContext : DbContext
{
    public ErpDbContext(DbContextOptions<ErpDbContext> options)
        : base(options)
    {
    }

    public DbSet<ConfigRow> Config => Set<ConfigRow>();
    public DbSet<SequenceRow> Sequences => Set<SequenceRow>();
    public DbSet<IdempotencyRow> IdempotencyKeys => Set<IdempotencyRow>();
    public DbSet<ItemRow> Items => Set<ItemRow>();
    public DbSet<SalesOrderRow> SalesOrders => Set<SalesOrderRow>();
    public DbSet<SalesOrderLineRow> SalesOrderLines => Set<SalesOrderLineRow>();
    public DbSet<BomRow> BomLines => Set<BomRow>();
    public DbSet<InventoryRow> InventoryTransactions => Set<InventoryRow>();
    public DbSet<VendorRow> Vendors => Set<VendorRow>();
    public DbSet<VendorDocumentRow> VendorDocuments => Set<VendorDocumentRow>();
    public DbSet<PaymentTermsRow> PaymentTerms => Set<PaymentTermsRow>();
    public DbSet<VendorBankRow> VendorBanks => Set<VendorBankRow>();
    public DbSet<VendorPriceRow> VendorPrices => Set<VendorPriceRow>();
    public DbSet<LastPriceRow> LastPrices => Set<LastPriceRow>();
    public DbSet<LegacyShareRow> LegacyShares => Set<LegacyShareRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<DelegationRow> Delegations => Set<DelegationRow>();
    public DbSet<VolumeSeedRow> VolumeSeeds => Set<VolumeSeedRow>();
    public DbSet<PrRow> PurchaseRequisitions => Set<PrRow>();
    public DbSet<PrLineRow> PurchaseRequisitionLines => Set<PrLineRow>();
    public DbSet<PoRow> PurchaseOrders => Set<PoRow>();
    public DbSet<PoLineRow> PurchaseOrderLines => Set<PoLineRow>();
    public DbSet<PoNoteRow> PurchaseOrderNotes => Set<PoNoteRow>();
    public DbSet<ConfirmationRow> VendorConfirmations => Set<ConfirmationRow>();
    public DbSet<ConfirmationLineRow> VendorConfirmationLines => Set<ConfirmationLineRow>();
    public DbSet<AsnRow> Asns => Set<AsnRow>();
    public DbSet<AsnCartonRow> AsnCartons => Set<AsnCartonRow>();
    public DbSet<ReceiptRow> ShipmentReceipts => Set<ReceiptRow>();
    public DbSet<ReceiptLineRow> ShipmentReceiptLines => Set<ReceiptLineRow>();
    public DbSet<LotMapRow> LotMap => Set<LotMapRow>();
    public DbSet<HoldRow> Holds => Set<HoldRow>();
    public DbSet<HoldReleaseRow> HoldReleases => Set<HoldReleaseRow>();
    public DbSet<InvoiceRow> VendorInvoices => Set<InvoiceRow>();
    public DbSet<InvoiceLineRow> VendorInvoiceLines => Set<InvoiceLineRow>();
    public DbSet<MatchResultRow> MatchResults => Set<MatchResultRow>();
    public DbSet<HeldReleaseRow> HeldReleases => Set<HeldReleaseRow>();
    public DbSet<PaymentRow> Payments => Set<PaymentRow>();
    public DbSet<PaymentLineRow> PaymentLines => Set<PaymentLineRow>();
    public DbSet<SettlementRow> Settlements => Set<SettlementRow>();
    public DbSet<PaidAmountRow> PaidAmounts => Set<PaidAmountRow>();
    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();
    public DbSet<RequesterRow> Requesters => Set<RequesterRow>();
    public DbSet<RoutedToRow> RoutedTo => Set<RoutedToRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConfigRow>(b =>
        {
            b.ToTable("Config");
            b.HasKey(x => x.Key);
        });
        modelBuilder.Entity<SequenceRow>(b =>
        {
            b.ToTable("Sequences");
            b.HasKey(x => x.Name);
        });
        modelBuilder.Entity<IdempotencyRow>(b =>
        {
            b.ToTable("IdempotencyKeys");
            b.HasKey(x => x.Key);
        });
        modelBuilder.Entity<ItemRow>(b =>
        {
            b.ToTable("Items");
            b.HasKey(x => x.ItemId);
        });
        modelBuilder.Entity<SalesOrderRow>(b =>
        {
            b.ToTable("SalesOrders");
            b.HasKey(x => x.SoNumber);
        });
        modelBuilder.Entity<SalesOrderLineRow>(b =>
        {
            b.ToTable("SalesOrderLines");
            b.HasKey(x => new { x.SoNumber, x.LineNo });
        });
        modelBuilder.Entity<BomRow>(b =>
        {
            b.ToTable("BomLines");
            b.HasKey(x => new { x.ItemId, x.ComponentItemId });
        });
        modelBuilder.Entity<InventoryRow>(b =>
        {
            b.ToTable("InventoryTransactions");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.ItemId);
        });
        modelBuilder.Entity<VendorRow>(b =>
        {
            b.ToTable("Vendors");
            b.HasKey(x => x.VendorId);
        });
        modelBuilder.Entity<VendorDocumentRow>(b =>
        {
            b.ToTable("VendorDocuments");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.VendorId);
        });
        modelBuilder.Entity<PaymentTermsRow>(b =>
        {
            b.ToTable("PaymentTerms");
            b.HasKey(x => x.TermsId);
        });
        modelBuilder.Entity<VendorBankRow>(b =>
        {
            b.ToTable("VendorBanks");
            b.HasKey(x => x.VendorId);
        });
        modelBuilder.Entity<VendorPriceRow>(b =>
        {
            b.ToTable("VendorPrices");
            b.HasKey(x => new { x.VendorId, x.ItemId });
        });
        modelBuilder.Entity<LastPriceRow>(b =>
        {
            b.ToTable("LastPrices");
            b.HasKey(x => x.ItemId);
        });
        modelBuilder.Entity<LegacyShareRow>(b =>
        {
            b.ToTable("LegacyShares");
            b.HasKey(x => new { x.VendorId, x.ItemId });
        });
        modelBuilder.Entity<UserRow>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.UserId);
        });
        modelBuilder.Entity<DelegationRow>(b =>
        {
            b.ToTable("Delegations");
            b.HasKey(x => x.RowId);
        });
        modelBuilder.Entity<VolumeSeedRow>(b =>
        {
            b.ToTable("VolumeSeeds");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.ItemId);
        });
        modelBuilder.Entity<PrRow>(b =>
        {
            b.ToTable("PurchaseRequisitions");
            b.HasKey(x => x.PrNumber);
        });
        modelBuilder.Entity<PrLineRow>(b =>
        {
            b.ToTable("PurchaseRequisitionLines");
            b.HasKey(x => new { x.PrNumber, x.LineNo });
        });
        modelBuilder.Entity<PoRow>(b =>
        {
            b.ToTable("PurchaseOrders");
            b.HasKey(x => x.PoNumber);
        });
        modelBuilder.Entity<PoLineRow>(b =>
        {
            b.ToTable("PurchaseOrderLines");
            b.HasKey(x => new { x.PoNumber, x.PoLineRef });
        });
        modelBuilder.Entity<PoNoteRow>(b =>
        {
            b.ToTable("PurchaseOrderNotes");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.PoNumber);
        });
        modelBuilder.Entity<ConfirmationRow>(b =>
        {
            b.ToTable("VendorConfirmations");
            b.HasKey(x => x.PoNumber);
        });
        modelBuilder.Entity<ConfirmationLineRow>(b =>
        {
            b.ToTable("VendorConfirmationLines");
            b.HasKey(x => new { x.PoNumber, x.PoLineRef });
        });
        modelBuilder.Entity<AsnRow>(b =>
        {
            b.ToTable("Asns");
            b.HasKey(x => x.AsnNumber);
        });
        modelBuilder.Entity<AsnCartonRow>(b =>
        {
            b.ToTable("AsnCartons");
            b.HasKey(x => x.CartonId);
            b.HasIndex(x => x.AsnNumber);
        });
        modelBuilder.Entity<ReceiptRow>(b =>
        {
            b.ToTable("ShipmentReceipts");
            b.HasKey(x => x.ReceiptNumber);
            b.HasIndex(x => x.PoNumber);
        });
        modelBuilder.Entity<ReceiptLineRow>(b =>
        {
            b.ToTable("ShipmentReceiptLines");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.ReceiptNumber);
        });
        modelBuilder.Entity<LotMapRow>(b =>
        {
            b.ToTable("LotMap");
            b.HasKey(x => x.LotRef);
        });
        modelBuilder.Entity<HoldRow>(b =>
        {
            b.ToTable("Holds");
            b.HasKey(x => x.HoldId);
        });
        modelBuilder.Entity<HoldReleaseRow>(b =>
        {
            b.ToTable("HoldReleases");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.HoldId);
        });
        modelBuilder.Entity<InvoiceRow>(b =>
        {
            b.ToTable("VendorInvoices");
            b.HasKey(x => new { x.VendorId, x.SupplierInvoiceNumber });
        });
        modelBuilder.Entity<InvoiceLineRow>(b =>
        {
            b.ToTable("VendorInvoiceLines");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => new { x.VendorId, x.SupplierInvoiceNumber });
        });
        modelBuilder.Entity<MatchResultRow>(b =>
        {
            b.ToTable("MatchResults");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => new { x.VendorId, x.SupplierInvoiceNumber });
        });
        modelBuilder.Entity<HeldReleaseRow>(b =>
        {
            b.ToTable("HeldReleases");
            b.HasKey(x => x.RowId);
        });
        modelBuilder.Entity<PaymentRow>(b =>
        {
            b.ToTable("Payments");
            b.HasKey(x => x.PaymentNumber);
        });
        modelBuilder.Entity<PaymentLineRow>(b =>
        {
            b.ToTable("PaymentLines");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => x.PaymentNumber);
        });
        modelBuilder.Entity<SettlementRow>(b =>
        {
            b.ToTable("Settlements");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => new { x.VendorId, x.SupplierInvoiceNumber });
        });
        modelBuilder.Entity<PaidAmountRow>(b =>
        {
            b.ToTable("PaidAmounts");
            b.HasKey(x => new { x.VendorId, x.SupplierInvoiceNumber });
        });
        modelBuilder.Entity<ApprovalRow>(b =>
        {
            b.ToTable("Approvals");
            b.HasKey(x => x.RowId);
            b.HasIndex(x => new { x.DocumentType, x.DocumentNumber });
        });
        modelBuilder.Entity<RequesterRow>(b =>
        {
            b.ToTable("Requesters");
            b.HasKey(x => new { x.DocumentType, x.DocumentNumber });
        });
        modelBuilder.Entity<RoutedToRow>(b =>
        {
            b.ToTable("RoutedTo");
            b.HasKey(x => new { x.DocumentType, x.DocumentNumber });
        });
    }
}

// Company baseline schema: company_info plus sites masters, inventory
// journals (consumption, counts, adjustments with reason), created plus
// approved plus posted audit stamps on PR/PO/receipt/invoice/payment,
// invoice due date, and vendor hold actor plus timestamp. Deletes the
// VolumeSeeds union: volume history reads posted receipt lines only.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
[Migration("20261101000001_CompanyBaseline")]
public partial class CompanyBaseline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "company_info",
            columns: table => new
            {
                EntityCode = table.Column<string>(type: "TEXT", nullable: false),
                LegalName = table.Column<string>(type: "TEXT", nullable: false),
                Currency = table.Column<string>(type: "TEXT", nullable: false),
                BillToAddress = table.Column<string>(type: "TEXT", nullable: false),
                ShipToAddress = table.Column<string>(type: "TEXT", nullable: false),
                SenderEmail = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_company_info", x => x.EntityCode);
            });

        migrationBuilder.CreateTable(
            name: "sites",
            columns: table => new
            {
                SiteId = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Address = table.Column<string>(type: "TEXT", nullable: false),
                Capabilities = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sites", x => x.SiteId);
            });

        migrationBuilder.CreateTable(
            name: "inventory_journals",
            columns: table => new
            {
                JournalId = table.Column<string>(type: "TEXT", nullable: false),
                JournalType = table.Column<string>(type: "TEXT", nullable: false),
                PostingDate = table.Column<string>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inventory_journals", x => x.JournalId);
            });

        migrationBuilder.CreateTable(
            name: "inventory_journal_lines",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                JournalId = table.Column<string>(type: "TEXT", nullable: false),
                LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                LotRef = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inventory_journal_lines", x => x.RowId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_inventory_journal_lines_JournalId",
            table: "inventory_journal_lines",
            column: "JournalId");

        migrationBuilder.AddColumn<string>(
            name: "CreatedAt",
            table: "PurchaseRequisitions",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedBy",
            table: "PurchaseRequisitions",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedAt",
            table: "PurchaseRequisitions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CreatedAt",
            table: "PurchaseOrders",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedBy",
            table: "PurchaseOrders",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedAt",
            table: "PurchaseOrders",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CreatedAt",
            table: "ShipmentReceipts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PostedBy",
            table: "ShipmentReceipts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PostedAt",
            table: "ShipmentReceipts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PostingDate",
            table: "ShipmentReceipts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CreatedAt",
            table: "VendorInvoices",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedBy",
            table: "VendorInvoices",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedAt",
            table: "VendorInvoices",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PostedBy",
            table: "VendorInvoices",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "DueDate",
            table: "VendorInvoices",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CreatedAt",
            table: "Payments",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedBy",
            table: "Payments",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ApprovedAt",
            table: "Payments",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PostedBy",
            table: "Payments",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PostedAt",
            table: "Payments",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BlockedBy",
            table: "Vendors",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "BlockedAt",
            table: "Vendors",
            type: "TEXT",
            nullable: true);

        migrationBuilder.DropTable(name: "VolumeSeeds");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VolumeSeeds",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Country = table.Column<string>(type: "TEXT", nullable: false),
                Cells = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VolumeSeeds", x => x.RowId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VolumeSeeds_ItemId",
            table: "VolumeSeeds",
            column: "ItemId");

        migrationBuilder.DropColumn(name: "BlockedBy", table: "Vendors");
        migrationBuilder.DropColumn(name: "BlockedAt", table: "Vendors");

        migrationBuilder.DropColumn(name: "CreatedAt", table: "Payments");
        migrationBuilder.DropColumn(name: "ApprovedBy", table: "Payments");
        migrationBuilder.DropColumn(name: "ApprovedAt", table: "Payments");
        migrationBuilder.DropColumn(name: "PostedBy", table: "Payments");
        migrationBuilder.DropColumn(name: "PostedAt", table: "Payments");

        migrationBuilder.DropColumn(name: "CreatedAt", table: "VendorInvoices");
        migrationBuilder.DropColumn(name: "ApprovedBy", table: "VendorInvoices");
        migrationBuilder.DropColumn(name: "ApprovedAt", table: "VendorInvoices");
        migrationBuilder.DropColumn(name: "PostedBy", table: "VendorInvoices");
        migrationBuilder.DropColumn(name: "DueDate", table: "VendorInvoices");

        migrationBuilder.DropColumn(name: "CreatedAt", table: "ShipmentReceipts");
        migrationBuilder.DropColumn(name: "PostedBy", table: "ShipmentReceipts");
        migrationBuilder.DropColumn(name: "PostedAt", table: "ShipmentReceipts");
        migrationBuilder.DropColumn(name: "PostingDate", table: "ShipmentReceipts");

        migrationBuilder.DropColumn(name: "CreatedAt", table: "PurchaseOrders");
        migrationBuilder.DropColumn(name: "ApprovedBy", table: "PurchaseOrders");
        migrationBuilder.DropColumn(name: "ApprovedAt", table: "PurchaseOrders");

        migrationBuilder.DropColumn(name: "CreatedAt", table: "PurchaseRequisitions");
        migrationBuilder.DropColumn(name: "ApprovedBy", table: "PurchaseRequisitions");
        migrationBuilder.DropColumn(name: "ApprovedAt", table: "PurchaseRequisitions");

        migrationBuilder.DropTable(name: "inventory_journal_lines");
        migrationBuilder.DropTable(name: "inventory_journals");
        migrationBuilder.DropTable(name: "sites");
        migrationBuilder.DropTable(name: "company_info");
    }
}

// Initial mini-ERP schema. Applied with Database.Migrate() at startup and
// in tests; every table mirrors its entity column for column.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
[Migration("20261030000000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Config",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Config", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "Sequences",
            columns: table => new
            {
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Next = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sequences", x => x.Name);
            });

        migrationBuilder.CreateTable(
            name: "IdempotencyKeys",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdempotencyKeys", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "Items",
            columns: table => new
            {
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                IsCritical = table.Column<bool>(type: "INTEGER", nullable: false),
                UnitsPerCarton = table.Column<int>(type: "INTEGER", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Items", x => x.ItemId);
            });

        migrationBuilder.CreateTable(
            name: "SalesOrders",
            columns: table => new
            {
                SoNumber = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Currency = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SalesOrders", x => x.SoNumber);
            });

        migrationBuilder.CreateTable(
            name: "SalesOrderLines",
            columns: table => new
            {
                SoNumber = table.Column<string>(type: "TEXT", nullable: false),
                LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SalesOrderLines", x => new { x.SoNumber, x.LineNo });
            });

        migrationBuilder.CreateTable(
            name: "BomLines",
            columns: table => new
            {
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                ComponentItemId = table.Column<string>(type: "TEXT", nullable: false),
                QtyPer = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BomLines", x => new { x.ItemId, x.ComponentItemId });
            });

        migrationBuilder.CreateTable(
            name: "InventoryTransactions",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                ReceiptRef = table.Column<string>(type: "TEXT", nullable: false),
                LotRef = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InventoryTransactions", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "Vendors",
            columns: table => new
            {
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                CountryOfOrigin = table.Column<string>(type: "TEXT", nullable: false),
                IsBlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                BlockReason = table.Column<string>(type: "TEXT", nullable: true),
                PaymentTermsId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Vendors", x => x.VendorId);
            });

        migrationBuilder.CreateTable(
            name: "VendorDocuments",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                Reference = table.Column<string>(type: "TEXT", nullable: false),
                Present = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorDocuments", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "PaymentTerms",
            columns: table => new
            {
                TermsId = table.Column<string>(type: "TEXT", nullable: false),
                NetDays = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PaymentTerms", x => x.TermsId);
            });

        migrationBuilder.CreateTable(
            name: "VendorBanks",
            columns: table => new
            {
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                AccountRef = table.Column<string>(type: "TEXT", nullable: false),
                Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                VerifiedAt = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorBanks", x => x.VendorId);
            });

        migrationBuilder.CreateTable(
            name: "VendorPrices",
            columns: table => new
            {
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorPrices", x => new { x.VendorId, x.ItemId });
            });

        migrationBuilder.CreateTable(
            name: "LastPrices",
            columns: table => new
            {
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LastPrices", x => x.ItemId);
            });

        migrationBuilder.CreateTable(
            name: "LegacyShares",
            columns: table => new
            {
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                Share = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LegacyShares", x => new { x.VendorId, x.ItemId });
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", nullable: false),
                SigningLimit = table.Column<decimal>(type: "TEXT", nullable: false),
                LimitCurrency = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.UserId);
            });

        migrationBuilder.CreateTable(
            name: "Delegations",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                FromUser = table.Column<string>(type: "TEXT", nullable: false),
                ToUser = table.Column<string>(type: "TEXT", nullable: false),
                Start = table.Column<string>(type: "TEXT", nullable: false),
                End = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Delegations", x => x.RowId);
            });

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

        migrationBuilder.CreateTable(
            name: "PurchaseRequisitions",
            columns: table => new
            {
                PrNumber = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Requester = table.Column<string>(type: "TEXT", nullable: false),
                Currency = table.Column<string>(type: "TEXT", nullable: false),
                EstimatedAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseRequisitions", x => x.PrNumber);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseRequisitionLines",
            columns: table => new
            {
                PrNumber = table.Column<string>(type: "TEXT", nullable: false),
                LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseRequisitionLines", x => new { x.PrNumber, x.LineNo });
            });

        migrationBuilder.CreateTable(
            name: "PurchaseOrders",
            columns: table => new
            {
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                PrNumber = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                PaymentTermsId = table.Column<string>(type: "TEXT", nullable: false),
                ApprovalStatus = table.Column<string>(type: "TEXT", nullable: false),
                DocStatus = table.Column<string>(type: "TEXT", nullable: false),
                Currency = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseOrders", x => x.PoNumber);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseOrderLines",
            columns: table => new
            {
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: false),
                LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                QtyOrdered = table.Column<decimal>(type: "TEXT", nullable: false),
                QtyReceived = table.Column<decimal>(type: "TEXT", nullable: false),
                QtyInvoiced = table.Column<decimal>(type: "TEXT", nullable: false),
                QtyQuarantined = table.Column<decimal>(type: "TEXT", nullable: false),
                QtyRejected = table.Column<decimal>(type: "TEXT", nullable: false),
                UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                LineAmount = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseOrderLines", x => new { x.PoNumber, x.PoLineRef });
            });

        migrationBuilder.CreateTable(
            name: "PurchaseOrderNotes",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                Note = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseOrderNotes", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "VendorConfirmations",
            columns: table => new
            {
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                ConfirmDate = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorConfirmations", x => x.PoNumber);
            });

        migrationBuilder.CreateTable(
            name: "VendorConfirmationLines",
            columns: table => new
            {
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: false),
                ConfirmedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                BackorderNote = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorConfirmationLines", x => new { x.PoNumber, x.PoLineRef });
            });

        migrationBuilder.CreateTable(
            name: "Asns",
            columns: table => new
            {
                AsnNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Asns", x => x.AsnNumber);
            });

        migrationBuilder.CreateTable(
            name: "AsnCartons",
            columns: table => new
            {
                CartonId = table.Column<string>(type: "TEXT", nullable: false),
                AsnNumber = table.Column<string>(type: "TEXT", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false),
                UnitsPerCarton = table.Column<int>(type: "INTEGER", nullable: false),
                LotRef = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AsnCartons", x => x.CartonId);
            });

        migrationBuilder.CreateTable(
            name: "ShipmentReceipts",
            columns: table => new
            {
                ReceiptNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                SupplierPackingSlipNumber = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ShipmentReceipts", x => x.ReceiptNumber);
            });

        migrationBuilder.CreateTable(
            name: "ShipmentReceiptLines",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                ReceiptNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: false),
                QtyReceived = table.Column<decimal>(type: "TEXT", nullable: false),
                QtyQuarantined = table.Column<decimal>(type: "TEXT", nullable: false),
                QtyRejected = table.Column<decimal>(type: "TEXT", nullable: false),
                LotRef = table.Column<string>(type: "TEXT", nullable: false),
                ConditionNote = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ShipmentReceiptLines", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "LotMap",
            columns: table => new
            {
                LotRef = table.Column<string>(type: "TEXT", nullable: false),
                PoNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: false),
                ItemId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LotMap", x => x.LotRef);
            });

        migrationBuilder.CreateTable(
            name: "Holds",
            columns: table => new
            {
                HoldId = table.Column<string>(type: "TEXT", nullable: false),
                ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                Note = table.Column<string>(type: "TEXT", nullable: false),
                RuleId = table.Column<string>(type: "TEXT", nullable: true),
                CaseId = table.Column<string>(type: "TEXT", nullable: false),
                SetBy = table.Column<string>(type: "TEXT", nullable: false),
                SetAt = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                DocumentType = table.Column<string>(type: "TEXT", nullable: true),
                DocumentNumber = table.Column<string>(type: "TEXT", nullable: true),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: true),
                LotRef = table.Column<string>(type: "TEXT", nullable: true),
                CartonId = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Holds", x => x.HoldId);
            });

        migrationBuilder.CreateTable(
            name: "HoldReleases",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                HoldId = table.Column<string>(type: "TEXT", nullable: false),
                ReleasedBy = table.Column<string>(type: "TEXT", nullable: false),
                ReleasedAt = table.Column<string>(type: "TEXT", nullable: false),
                ApprovalRef = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HoldReleases", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "VendorInvoices",
            columns: table => new
            {
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoNumber = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Voucher = table.Column<string>(type: "TEXT", nullable: true),
                Currency = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                PostedAt = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorInvoices", x => new { x.VendorId, x.SupplierInvoiceNumber });
            });

        migrationBuilder.CreateTable(
            name: "VendorInvoiceLines",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: true),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                Disposition = table.Column<string>(type: "TEXT", nullable: false),
                HoldReason = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VendorInvoiceLines", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "MatchResults",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: true),
                Matched = table.Column<bool>(type: "INTEGER", nullable: false),
                Message = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MatchResults", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "HeldReleases",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                PoLineRef = table.Column<string>(type: "TEXT", nullable: true),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                ReleasedBy = table.Column<string>(type: "TEXT", nullable: false),
                ReleasedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HeldReleases", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "Payments",
            columns: table => new
            {
                PaymentNumber = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Currency = table.Column<string>(type: "TEXT", nullable: false),
                PayeeAccountRef = table.Column<string>(type: "TEXT", nullable: false),
                PayeeVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                DueDate = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Payments", x => x.PaymentNumber);
            });

        migrationBuilder.CreateTable(
            name: "PaymentLines",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                PaymentNumber = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PaymentLines", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "Settlements",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                PaymentNumber = table.Column<string>(type: "TEXT", nullable: false),
                SettledAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                RemainingBalance = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Settlements", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "PaidAmounts",
            columns: table => new
            {
                VendorId = table.Column<string>(type: "TEXT", nullable: false),
                SupplierInvoiceNumber = table.Column<string>(type: "TEXT", nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PaidAmounts", x => new { x.VendorId, x.SupplierInvoiceNumber });
            });

        migrationBuilder.CreateTable(
            name: "Approvals",
            columns: table => new
            {
                RowId = table.Column<string>(type: "TEXT", nullable: false),
                DocumentType = table.Column<string>(type: "TEXT", nullable: false),
                DocumentNumber = table.Column<string>(type: "TEXT", nullable: false),
                RoutedTo = table.Column<string>(type: "TEXT", nullable: false),
                ActedBy = table.Column<string>(type: "TEXT", nullable: true),
                ActingAsDelegate = table.Column<bool>(type: "INTEGER", nullable: false),
                Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                Approved = table.Column<bool>(type: "INTEGER", nullable: false),
                Comment = table.Column<string>(type: "TEXT", nullable: true),
                DocumentAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                LimitChecked = table.Column<decimal>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Approvals", x => x.RowId);
            });

        migrationBuilder.CreateTable(
            name: "Requesters",
            columns: table => new
            {
                DocumentType = table.Column<string>(type: "TEXT", nullable: false),
                DocumentNumber = table.Column<string>(type: "TEXT", nullable: false),
                Requester = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Requesters", x => new { x.DocumentType, x.DocumentNumber });
            });

        migrationBuilder.CreateTable(
            name: "RoutedTo",
            columns: table => new
            {
                DocumentType = table.Column<string>(type: "TEXT", nullable: false),
                DocumentNumber = table.Column<string>(type: "TEXT", nullable: false),
                RoutedTo = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoutedTo", x => new { x.DocumentType, x.DocumentNumber });
            });

        migrationBuilder.CreateIndex(
            name: "IX_InventoryTransactions_ItemId",
            table: "InventoryTransactions",
            column: "ItemId");

        migrationBuilder.CreateIndex(
            name: "IX_VendorDocuments_VendorId",
            table: "VendorDocuments",
            column: "VendorId");

        migrationBuilder.CreateIndex(
            name: "IX_VolumeSeeds_ItemId",
            table: "VolumeSeeds",
            column: "ItemId");

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseOrderNotes_PoNumber",
            table: "PurchaseOrderNotes",
            column: "PoNumber");

        migrationBuilder.CreateIndex(
            name: "IX_AsnCartons_AsnNumber",
            table: "AsnCartons",
            column: "AsnNumber");

        migrationBuilder.CreateIndex(
            name: "IX_ShipmentReceipts_PoNumber",
            table: "ShipmentReceipts",
            column: "PoNumber");

        migrationBuilder.CreateIndex(
            name: "IX_ShipmentReceiptLines_ReceiptNumber",
            table: "ShipmentReceiptLines",
            column: "ReceiptNumber");

        migrationBuilder.CreateIndex(
            name: "IX_HoldReleases_HoldId",
            table: "HoldReleases",
            column: "HoldId");

        migrationBuilder.CreateIndex(
            name: "IX_VendorInvoiceLines_VendorId_SupplierInvoiceNumber",
            table: "VendorInvoiceLines",
            columns: new[] { "VendorId", "SupplierInvoiceNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_MatchResults_VendorId_SupplierInvoiceNumber",
            table: "MatchResults",
            columns: new[] { "VendorId", "SupplierInvoiceNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_PaymentLines_PaymentNumber",
            table: "PaymentLines",
            column: "PaymentNumber");

        migrationBuilder.CreateIndex(
            name: "IX_Settlements_VendorId_SupplierInvoiceNumber",
            table: "Settlements",
            columns: new[] { "VendorId", "SupplierInvoiceNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_Approvals_DocumentType_DocumentNumber",
            table: "Approvals",
            columns: new[] { "DocumentType", "DocumentNumber" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Config");
        migrationBuilder.DropTable(name: "Sequences");
        migrationBuilder.DropTable(name: "IdempotencyKeys");
        migrationBuilder.DropTable(name: "Items");
        migrationBuilder.DropTable(name: "SalesOrders");
        migrationBuilder.DropTable(name: "SalesOrderLines");
        migrationBuilder.DropTable(name: "BomLines");
        migrationBuilder.DropTable(name: "InventoryTransactions");
        migrationBuilder.DropTable(name: "Vendors");
        migrationBuilder.DropTable(name: "VendorDocuments");
        migrationBuilder.DropTable(name: "PaymentTerms");
        migrationBuilder.DropTable(name: "VendorBanks");
        migrationBuilder.DropTable(name: "VendorPrices");
        migrationBuilder.DropTable(name: "LastPrices");
        migrationBuilder.DropTable(name: "LegacyShares");
        migrationBuilder.DropTable(name: "Users");
        migrationBuilder.DropTable(name: "Delegations");
        migrationBuilder.DropTable(name: "VolumeSeeds");
        migrationBuilder.DropTable(name: "PurchaseRequisitions");
        migrationBuilder.DropTable(name: "PurchaseRequisitionLines");
        migrationBuilder.DropTable(name: "PurchaseOrders");
        migrationBuilder.DropTable(name: "PurchaseOrderLines");
        migrationBuilder.DropTable(name: "PurchaseOrderNotes");
        migrationBuilder.DropTable(name: "VendorConfirmations");
        migrationBuilder.DropTable(name: "VendorConfirmationLines");
        migrationBuilder.DropTable(name: "Asns");
        migrationBuilder.DropTable(name: "AsnCartons");
        migrationBuilder.DropTable(name: "ShipmentReceipts");
        migrationBuilder.DropTable(name: "ShipmentReceiptLines");
        migrationBuilder.DropTable(name: "LotMap");
        migrationBuilder.DropTable(name: "Holds");
        migrationBuilder.DropTable(name: "HoldReleases");
        migrationBuilder.DropTable(name: "VendorInvoices");
        migrationBuilder.DropTable(name: "VendorInvoiceLines");
        migrationBuilder.DropTable(name: "MatchResults");
        migrationBuilder.DropTable(name: "HeldReleases");
        migrationBuilder.DropTable(name: "Payments");
        migrationBuilder.DropTable(name: "PaymentLines");
        migrationBuilder.DropTable(name: "Settlements");
        migrationBuilder.DropTable(name: "PaidAmounts");
        migrationBuilder.DropTable(name: "Approvals");
        migrationBuilder.DropTable(name: "Requesters");
        migrationBuilder.DropTable(name: "RoutedTo");
    }
}

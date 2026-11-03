// Carton identity is per shipment notice: the AsnCartons key becomes
// (AsnNumber, CartonId) so two suppliers can ship the same carton
// number, with duplicates refused only within one ASN. SQLite cannot
// alter a primary key, so the table is rebuilt; the old ASN-only index
// goes away with it (the composite key's prefix covers ASN lookups).
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
[Migration("20261103000000_CartonScopePerAsn")]
public partial class CartonScopePerAsn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE TABLE AsnCartons_new (" +
            "CartonId TEXT NOT NULL, " +
            "AsnNumber TEXT NOT NULL, " +
            "ItemId TEXT NOT NULL, " +
            "UnitsPerCarton INTEGER NOT NULL, " +
            "LotRef TEXT NOT NULL, " +
            "CONSTRAINT PK_AsnCartons PRIMARY KEY (AsnNumber, CartonId));");
        migrationBuilder.Sql(
            "INSERT INTO AsnCartons_new (CartonId, AsnNumber, ItemId, UnitsPerCarton, LotRef) " +
            "SELECT CartonId, AsnNumber, ItemId, UnitsPerCarton, LotRef FROM AsnCartons;");
        migrationBuilder.Sql("DROP TABLE AsnCartons;");
        migrationBuilder.Sql("ALTER TABLE AsnCartons_new RENAME TO AsnCartons;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE TABLE AsnCartons_old (" +
            "CartonId TEXT NOT NULL, " +
            "AsnNumber TEXT NOT NULL, " +
            "ItemId TEXT NOT NULL, " +
            "UnitsPerCarton INTEGER NOT NULL, " +
            "LotRef TEXT NOT NULL, " +
            "CONSTRAINT PK_AsnCartons PRIMARY KEY (CartonId));");
        migrationBuilder.Sql(
            "INSERT INTO AsnCartons_old (CartonId, AsnNumber, ItemId, UnitsPerCarton, LotRef) " +
            "SELECT CartonId, AsnNumber, ItemId, UnitsPerCarton, LotRef FROM AsnCartons;");
        migrationBuilder.Sql("DROP TABLE AsnCartons;");
        migrationBuilder.Sql("ALTER TABLE AsnCartons_old RENAME TO AsnCartons;");
        migrationBuilder.CreateIndex(
            name: "IX_AsnCartons_AsnNumber",
            table: "AsnCartons",
            column: "AsnNumber");
    }
}

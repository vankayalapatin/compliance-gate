// Stamps the shipment notice on holds: carton numbers repeat across
// notices, so a hold touches its carton only on its own ASN. Existing
// rows keep a null stamp and fall back to the bare carton id.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
[Migration("20261103000001_HoldAsnScope")]
public partial class HoldAsnScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AsnNumber",
            table: "Holds",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AsnNumber",
            table: "Holds");
    }
}

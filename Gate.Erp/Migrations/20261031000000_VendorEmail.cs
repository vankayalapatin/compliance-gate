// Adds the vendor contact email column. Seeded from vendors[].email in
// the scenario file; the agent sends invoice queries to this address.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
[Migration("20261031000000_VendorEmail")]
public partial class VendorEmail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Email",
            table: "Vendors",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Email",
            table: "Vendors");
    }
}

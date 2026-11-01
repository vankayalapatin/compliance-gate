// Stores number-sequence formats on Sequences (template plus zero-padding
// width, applied from the scenario numberSequences section) and records who
// submitted each routed document so the pending-approval read can project
// SubmittedBy. Existing rows keep minting: null format means the default
// template, zero padding means no padding, blank submitter falls back to the
// recorded requester.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gate.Erp.Migrations;

[DbContext(typeof(ErpDbContext))]
[Migration("20261101000000_SequenceFormatAndSubmittedBy")]
public partial class SequenceFormatAndSubmittedBy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Format",
            table: "Sequences",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "Padding",
            table: "Sequences",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<string>(
            name: "SubmittedBy",
            table: "RoutedTo",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Format",
            table: "Sequences");
        migrationBuilder.DropColumn(
            name: "Padding",
            table: "Sequences");
        migrationBuilder.DropColumn(
            name: "SubmittedBy",
            table: "RoutedTo");
    }
}

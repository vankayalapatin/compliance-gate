// Vendor masters after reset: wipe plus reload rewrites every vendor from
// the scenario file, so renamed vendors never leave stale names behind,
// and supplier legs (ASN, receipt, invoice) run on the reset-seeded POs
// with no case involved. Runs against both adapters; scenario-driven
// expectations are read from JSON at runtime, never hard-coded.
using System.Text.Json;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

public sealed class VendorResetTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ResetReplacesVendorNames_Fake()
    {
        await RenameAsync(useEfCore: false);
    }

    [Fact]
    public async Task ResetReplacesVendorNames_EfCore()
    {
        await RenameAsync(useEfCore: true);
    }

    [Fact]
    public async Task ResetVendorsMatchScenario_Demo_Fake()
    {
        await MatchScenarioAsync("scenarios/demo-v1.json", useEfCore: false);
    }

    [Fact]
    public async Task ResetVendorsMatchScenario_Demo_EfCore()
    {
        await MatchScenarioAsync("scenarios/demo-v1.json", useEfCore: true);
    }

    [Fact]
    public async Task ResetVendorsMatchScenario_Alt_Fake()
    {
        await MatchScenarioAsync("scenarios/alt-v1.json", useEfCore: false);
    }

    [Fact]
    public async Task ResetVendorsMatchScenario_Alt_EfCore()
    {
        await MatchScenarioAsync("scenarios/alt-v1.json", useEfCore: true);
    }

    private static async Task RenameAsync(bool useEfCore)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "gate-vendor-reset-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var scenarioFile = Path.Combine(workDir, "scenario.json");
            var sqlite = Path.Combine(workDir, "gate.db");
            var manifestFile = Path.Combine(workDir, "run-manifest.json");
            var erpFile = Path.Combine(workDir, "erp.db");

            IErpAdapter erp;
            ErpDbContext? db = null;
            if (useEfCore)
            {
                var options = new DbContextOptionsBuilder<ErpDbContext>()
                    .UseSqlite($"Data Source={erpFile}")
                    .Options;
                db = new ErpDbContext(options);
                erp = new EfCoreErpAdapter(options);
            }
            else
            {
                erp = new InMemoryErpAdapter();
            }

            await using (db ?? (IAsyncDisposable)new NoopAsync())
            {
                await File.WriteAllTextAsync(scenarioFile, ScenarioJson(
                    "Alpha Test Industries", "alpha@test.example",
                    "Beta Test Works", "beta@test.example"));
                await ScenarioLoader.ResetAsync(erp, scenarioFile, sqlite, manifestFile);
                await AssertVendorsAsync(erp,
                    ("V-T1", "Alpha Test Industries", "US", "NET0", "alpha@test.example"),
                    ("V-T2", "Beta Test Works", "US", "NET0", "beta@test.example"));

                await File.WriteAllTextAsync(scenarioFile, ScenarioJson(
                    "Gamma Test Corp", "gamma@test.example",
                    "Delta Test LLC", "delta@test.example"));
                var manifest = await ScenarioLoader.ResetAsync(erp, scenarioFile, sqlite, manifestFile);
                await AssertVendorsAsync(erp,
                    ("V-T1", "Gamma Test Corp", "US", "NET0", "gamma@test.example"),
                    ("V-T2", "Delta Test LLC", "US", "NET0", "delta@test.example"));

                var vendors = await erp.GetVendorsAsync();
                Assert.DoesNotContain(vendors, v => v.Name.Contains("Alpha", StringComparison.Ordinal));
                Assert.DoesNotContain(vendors, v => v.Name.Contains("Beta", StringComparison.Ordinal));

                // The reset-seeded PO stays fully actionable with no case:
                // listed per vendor, already confirmed, ASN plus receipt
                // plus invoice all post through plain adapter writes.
                var poNumber = manifest.Documents.Single(d => d.Event == "openPoInbound.po").Number;
                var listed = await erp.GetPurchaseOrdersByVendorAsync("V-T1");
                Assert.Contains(listed, p => p.PoNumber == poNumber);
                Assert.NotNull(await erp.GetVendorConfirmationAsync(poNumber));
                var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
                var line = po.Lines.Single(l => l.ItemId == "T-1");
                await erp.CreateAsnAsync("ASN-RESET-1", poNumber, RecordSource.Vendor, "V-T1",
                    [new AsnCarton("CTN-RESET-1", line.ItemId, 10, "LOT-RESET-1")], Idem());
                await erp.PostReceiptAsync(poNumber, "PACK-RESET-1", RecordSource.Vendor, "V-T1",
                    [new ReceiptLine(line.PoLineRef, line.QtyOrdered, 0, 0, "LOT-RESET-1", null)], Idem());
                var invoice = await erp.CreateInvoiceAsync("INV-RESET-1", "V-T1", poNumber,
                    RecordSource.Vendor, "V-T1",
                    [new InvoiceLineInput(line.PoLineRef, line.QtyOrdered, line.UnitPrice)], Idem());
                Assert.Equal(InvoiceStatus.Received, invoice.Status);
                Assert.Equal(poNumber, invoice.PoNumber);
            }
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static async Task AssertVendorsAsync(
        IErpAdapter erp, params (string Id, string Name, string Country, string Terms, string Email)[] expected)
    {
        var vendors = await erp.GetVendorsAsync();
        Assert.Equal(expected.Length, vendors.Count);
        foreach (var (id, name, country, terms, email) in expected)
        {
            var vendor = vendors.Single(v => v.VendorId == id);
            Assert.Equal(name, vendor.Name);
            Assert.Equal(country, vendor.CountryOfOrigin);
            Assert.Equal(terms, vendor.PaymentTermsId);
            Assert.Equal(email, vendor.Email);
            Assert.False(vendor.IsBlocked);
        }
    }

    private static async Task MatchScenarioAsync(string scenarioPath, bool useEfCore)
    {
        var resolved = ScenarioLoader.ResolvePath(scenarioPath);
        Assert.True(File.Exists(resolved), $"Scenario file not found: {resolved}");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resolved));
        var expected = json.RootElement.GetProperty("vendors").EnumerateArray()
            .Select(v => (
                v.GetProperty("id").GetString()!,
                v.GetProperty("name").GetString()!,
                v.GetProperty("country").GetString()!,
                v.GetProperty("terms").GetString()!,
                v.GetProperty("email").GetString()!))
            .ToList();

        var workDir = Path.Combine(Path.GetTempPath(), "gate-vendor-reset-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            IErpAdapter erp;
            ErpDbContext? db = null;
            if (useEfCore)
            {
                var options = new DbContextOptionsBuilder<ErpDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(workDir, "erp.db")}")
                    .Options;
                db = new ErpDbContext(options);
                erp = new EfCoreErpAdapter(options);
            }
            else
            {
                erp = new InMemoryErpAdapter();
            }

            await using (db ?? (IAsyncDisposable)new NoopAsync())
            {
                await ScenarioLoader.ResetAsync(erp, resolved,
                    Path.Combine(workDir, "gate.db"), Path.Combine(workDir, "run-manifest.json"));
                await AssertVendorsAsync(erp, expected.ToArray());
            }
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static string ScenarioJson(string name1, string email1, string name2, string email2) => $$"""
        {
          "$comment": "Vendor-reset test scenario. Small test-only data.",
          "name": "vendor-reset-test",
          "currency": "USD",
          "salesOrder": { "item": "T-1", "quantity": 2 },
          "bom": [],
          "stock": [{ "item": "T-1", "quantity": 1 }],
          "openPoInbound": { "item": "T-1", "quantity": 3, "vendor": "V-T1" },
          "items": [
            { "id": "T-1", "description": "Test part", "critical": false, "packSize": 10, "unit": "EA" }
          ],
          "paymentTerms": [{ "id": "NET0", "netDays": 0 }],
          "vendors": [
            { "id": "V-T1", "name": "{{name1}}", "country": "US", "role": "test", "item": "T-1", "unitPrice": 5.00, "terms": "NET0", "bankAccount": "BANK-T1", "documents": [], "email": "{{email1}}" },
            { "id": "V-T2", "name": "{{name2}}", "country": "US", "role": "test", "item": "T-1", "unitPrice": 6.00, "terms": "NET0", "bankAccount": "BANK-T2", "documents": [], "email": "{{email2}}" }
          ],
          "historyPart": "T-1",
          "historyCells": [{ "source": "test", "country": "US", "cells": 10 }],
          "users": [
            { "name": "tester", "role": "requester", "signingLimit": 100000 },
            { "name": "boss", "role": "approver", "signingLimit": 100000 }
          ],
          "delegations": [],
          "autoApproveUnder": 1000000,
          "cartonPlan": { "unitsPerCarton": 10, "cartons": 1 },
          "smallPos": []
        }
        """;

    private sealed class NoopAsync : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

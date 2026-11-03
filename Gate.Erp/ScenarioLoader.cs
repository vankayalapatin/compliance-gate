// Scenario loader: fills the mini-ERP from a scenario file on startup (when
// empty) and on reset (wipe plus reload). Masters seed raw; EVERY document
// (SO, PR, PO, ASN, receipt, invoice, payment) is created through adapter
// writes so all ERP validation is enforced and no raw document rows exist.
// The adapter's own sequences mint every generated number in the padded
// scenario format (supplier numbers such as ASNs and packing slips come from
// the scenario file). Every generated id is recorded in sqlite and in
// scenarios/run-manifest.json (git-ignored), keyed by scenario event. No
// business values live here; all of them come from the scenario file.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gate.Erp;

public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Loads the scenario unless the sqlite store already holds
    /// documents (startup no-op). Returns the run manifest.</summary>
    public static async Task<ScenarioManifest> LoadAsync(
        IErpAdapter adapter,
        string scenarioPath,
        string sqlitePath,
        string manifestPath,
        CancellationToken ct = default)
    {
        var file = ResolvePath(scenarioPath);
        var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        var scenario = JsonSerializer.Deserialize<ScenarioFile>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Scenario file {file} did not parse.");

        using var store = ScenarioSqliteStore.Open(sqlitePath);
        if (!store.IsEmpty())
        {
            return ReadManifest(scenario, file, sqlitePath, store);
        }

        var now = DateTimeOffset.UtcNow;
        var documents = new List<ManifestDocument>();
        void Record(string eventKey, string docType, string number)
        {
            store.RecordDocument(eventKey, docType, number, now);
            documents.Add(new ManifestDocument(eventKey, docType, number));
        }

        if (adapter is EfCoreErpAdapter ef)
        {
            using var db = ef.DbFactory.CreateDbContext();
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            ErpSeeder.SeedMasters(db, scenario, now);
            ErpSeeder.ApplyNumberSequences(db, scenario.NumberSequences ?? []);
        }
        else if (adapter is InMemoryErpAdapter erp)
        {
            erp.SeedBaseCurrency(scenario.Currency);
            erp.SeedAutoApproveThreshold(scenario.AutoApproveUnder);
            foreach (var seq in scenario.NumberSequences ?? [])
            {
                erp.ConfigureSequence(seq.Prefix, seq.Format, seq.Padding, seq.Start);
            }
            foreach (var item in scenario.Items)
            {
                erp.SeedItem(new ItemDto(item.Id, item.Description, item.Critical, item.PackSize, item.Unit));
            }
            foreach (var terms in scenario.PaymentTerms)
            {
                erp.SeedPaymentTerms(new PaymentTermsDto(terms.Id, terms.NetDays));
            }

            foreach (var group in scenario.Bom.GroupBy(b => b.Item))
            {
                erp.SeedBom(group.Key, group.Select(b => new BomLine(b.Component, b.QtyPer)));
            }

            foreach (var s in scenario.Stock)
            {
                erp.SeedStock(s.Item, s.Quantity);
            }

            foreach (var v in scenario.Vendors)
            {
                erp.SeedVendor(new VendorDto(v.Id, v.Name, v.Country, false, null, v.Terms, v.Email));
                erp.SeedVendorDocuments(v.Id, v.Documents.Select(d => new VendorDocumentDto(v.Id, d.Kind, d.Reference, d.Present)));
                erp.SeedVendorBank(new VendorBankDto(v.Id, v.BankAccount, true, now));
                erp.SeedVendorPrice(v.Id, v.Item, v.UnitPrice);
            }

            // Raw trailing history only. Shares are computed by compliance at
            // evaluation time; nothing is precomputed here.
            erp.SeedVolumeHistory(
                scenario.HistoryPart,
                scenario.HistoryCells.Select(h => new VolumeHistory(h.Source, h.Country, h.Cells)));

            foreach (var u in scenario.Users)
            {
                erp.SeedUser(new UserDto(u.Name, u.Role, u.SigningLimit, scenario.Currency));
            }
            foreach (var d in scenario.Delegations)
            {
                erp.SeedDelegation(new DelegationDto(d.From, d.To, DateOnly.Parse(d.Start), DateOnly.Parse(d.End)));
            }
        }
        else
        {
            throw new InvalidOperationException("ScenarioLoader seeds the mini-ERP adapter only.");
        }

        var requester = scenario.Users.FirstOrDefault(u => u.Role == "requester")?.Name ?? "scenario";
        var approver = scenario.Users.FirstOrDefault(u => u.Role == "approver")?.Name ?? "scenario";
        var finance = scenario.Users.FirstOrDefault(u => u.Role == "payment-approver")?.Name ?? "scenario";
        string UnitFor(string itemId) => scenario.Items.First(i => i.Id == itemId).Unit;

        // The sales order is a document: minted through the adapter like every
        // other document, never inserted raw.
        var salesOrder = await adapter.CreateSalesOrderAsync(
            scenario.Currency,
            [new SalesOrderLine(scenario.SalesOrder.Item, scenario.SalesOrder.Quantity)],
            RecordSource.Erp, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        Record("salesOrder", "SO", salesOrder.SoNumber);

        var inbound = scenario.OpenPoInbound;
        var inboundPo = await CreateApprovedPoAsync(
            adapter, requester, approver, UnitFor(inbound.Item),
            inbound.Item, inbound.Quantity, inbound.Vendor, ct).ConfigureAwait(false);
        Record("openPoInbound.pr", "PR", inboundPo.PrNumber);
        Record("openPoInbound.po", "PO", inboundPo.PoNumber);

        foreach (var small in scenario.SmallPos)
        {
            if (adapter is InMemoryErpAdapter mem)
            {
                mem.SeedVendorPrice(small.Vendor, small.Item, small.UnitPrice);
            }
            else if (adapter is EfCoreErpAdapter efPrices)
            {
                using var priceDb = efPrices.DbFactory.CreateDbContext();
                ErpSeeder.UpsertVendorPrice(priceDb, small.Vendor, small.Item, small.UnitPrice);
            }
            var created = await CreateApprovedPoAsync(
                adapter, requester, approver, UnitFor(small.Item),
                small.Item, small.Quantity, small.Vendor, ct).ConfigureAwait(false);
            Record($"smallPo.{small.Key}.pr", "PR", created.PrNumber);
            Record($"smallPo.{small.Key}.po", "PO", created.PoNumber);
            // Small POs are closed history (SCENARIO.md 12): post one invoice each,
            // then close. No seed receipts: open small POs would net into the
            // shortfall and receipt rows would pollute CORP-02 volume history.
            var seedInvoice = $"SEED-INV-{created.PoNumber}";
            var smallPo = await adapter.GetPurchaseOrderAsync(created.PoNumber, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Small PO {created.PoNumber} vanished after seeding.");
            await adapter.CreateInvoiceAsync(seedInvoice, small.Vendor, created.PoNumber, RecordSource.Erp, requester,
                smallPo.Lines.Select(l => new InvoiceLineInput(l.PoLineRef, l.QtyOrdered, l.UnitPrice)).ToList(),
                Guid.NewGuid().ToString("N"), ct: ct).ConfigureAwait(false);
            var seedInvKey = $"{small.Vendor}/{seedInvoice}";
            await adapter.SubmitForApprovalAsync("Invoice", seedInvKey, approver, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            await adapter.RecordApprovalAsync("Invoice", seedInvKey, approver, true, "scenario seed", Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            await adapter.PostInvoiceAsync(small.Vendor, seedInvoice, requester, Guid.NewGuid().ToString("N"), ct: ct).ConfigureAwait(false);
            Record($"smallPo.{small.Key}.invoice", "INV", seedInvoice);
            await adapter.ClosePoAsync(created.PoNumber, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        }

        // Lifecycle stages on the non-short lifecycle item: one document per
        // stage per type, pending approvals routed per person, every approval
        // recorded against a real scenario user. All adapter writes.
        if (scenario.Lifecycle is not null)
        {
            await SeedLifecycleAsync(
                adapter, scenario, scenario.Lifecycle, requester, approver, finance,
                UnitFor, Record, ct).ConfigureAwait(false);
        }

        store.WriteMeta("scenario", scenario.Name);
        store.WriteMeta("scenarioFile", file);
        store.WriteMeta("loadedAtUtc", now.ToString("O"));
        store.WriteMeta("scenarioJson", json);

        var manifest = new ScenarioManifest(scenario.Name, file, Path.GetFullPath(sqlitePath), now, documents);
        await WriteManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>Wipe plus reload: clears ERP rows, sequences, and documents,
    /// then loads the scenario fresh so every number is regenerated.</summary>
    public static async Task<ScenarioManifest> ResetAsync(
        IErpAdapter adapter,
        string scenarioPath,
        string sqlitePath,
        string manifestPath,
        CancellationToken ct = default)
    {
        if (adapter is InMemoryErpAdapter erp)
        {
            erp.Clear();
        }
        else if (adapter is EfCoreErpAdapter ef)
        {
            await ef.ResetDatabaseForTestsAsync(ct).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("ScenarioLoader seeds the mini-ERP adapter only.");
        }
        using (var store = ScenarioSqliteStore.Open(sqlitePath))
        {
            store.Clear();
        }

        return await LoadAsync(adapter, scenarioPath, sqlitePath, manifestPath, ct).ConfigureAwait(false);
    }

    private static async Task<(string PrNumber, string PoNumber)> CreateApprovedPoAsync(
        IErpAdapter erp,
        string requester,
        string approver,
        string unit,
        string item,
        decimal quantity,
        string vendor,
        CancellationToken ct)
    {
        var pr = await erp.CreatePrAsync(
            requester, RecordSource.P2pAgent, requester,
            [new PurchaseRequisitionLine(item, quantity, unit)], Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            await erp.RecordApprovalAsync("PR", pr.PrNumber, approver, true, "scenario seed", Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.P2pAgent, requester, Guid.NewGuid().ToString("N"), ct: ct).ConfigureAwait(false);
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, approver, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            await erp.RecordApprovalAsync("PO", po.PoNumber, approver, true, "scenario seed", Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        }
        await erp.RecordVendorConfirmationAsync(po.PoNumber, DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime),
            RecordSource.Vendor, vendor, po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(),
            Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);

        return (pr.PrNumber, po.PoNumber);
    }

    private static async Task SeedLifecycleAsync(
        IErpAdapter erp,
        ScenarioFile scenario,
        ScenarioLifecycle lc,
        string requester,
        string approver,
        string finance,
        Func<string, string> unitFor,
        Action<string, string, string> record,
        CancellationToken ct)
    {
        // The lifecycle item stays out of the demo math: it is not on the
        // sales BOM, so its open POs never net into the shortfall, and it is
        // not the CORP-02 history part, so its receipts never pollute volume
        // history. Fail fast when the scenario breaks that contract.
        if (scenario.SalesOrder.Item == lc.Item
            || scenario.Bom.Any(b => b.Component == lc.Item)
            || scenario.HistoryPart == lc.Item)
        {
            throw new InvalidOperationException(
                $"Lifecycle item {lc.Item} must not be the sales item, a BOM component, or the history part.");
        }
        foreach (var routedTo in new[] { lc.PrSubmittedTo, lc.PoSubmittedTo, lc.InvoiceSubmittedTo, lc.PaySubmittedTo })
        {
            if (await erp.GetUserAsync(routedTo, ct).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"Lifecycle routes to unknown user {routedTo}.");
        }
        var invoiceTotal = lc.Invoices.Sum(i => i.Quantity);
        if (invoiceTotal > lc.BackboneQty)
        {
            throw new InvalidOperationException(
                $"Lifecycle invoices total {invoiceTotal} but the backbone receipt is {lc.BackboneQty}.");
        }

        var unit = unitFor(lc.Item);
        var item = await erp.GetItemAsync(lc.Item, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Lifecycle item {lc.Item} is not seeded.");
        string Idem() => Guid.NewGuid().ToString("N");

        async Task<string> SubmitPrAsync(string prNumber, string routedTo)
        {
            var pr = await erp.GetPrAsync(prNumber, ct).ConfigureAwait(false);
            if (pr is null || pr.Status != PrStatus.Draft)
                throw new InvalidOperationException($"Lifecycle PR {prNumber} is not submittable (auto-approved totals cannot seed submitted stages).");
            await erp.SubmitForApprovalAsync("PR", prNumber, routedTo, requester, Idem(), ct).ConfigureAwait(false);
            return prNumber;
        }

        async Task<string> ApprovePrAsync(string prNumber)
        {
            await erp.SubmitForApprovalAsync("PR", prNumber, approver, requester, Idem(), ct).ConfigureAwait(false);
            await erp.RecordApprovalAsync("PR", prNumber, approver, true, "scenario seed", Idem(), ct).ConfigureAwait(false);
            return prNumber;
        }

        async Task<PurchaseOrder> SubmitPoAsync(string poNumber, string routedTo)
        {
            var po = await erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
            if (po is null || po.ApprovalStatus != PoApprovalStatus.Draft)
                throw new InvalidOperationException($"Lifecycle PO {poNumber} is not submittable (auto-approved totals cannot seed submitted stages).");
            await erp.SubmitForApprovalAsync("PO", poNumber, routedTo, requester, Idem(), ct).ConfigureAwait(false);
            return (await erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false))!;
        }

        async Task<PurchaseRequisition> DraftPrAsync(decimal quantity)
        {
            return await erp.CreatePrAsync(
                requester, RecordSource.P2pAgent, requester,
                [new PurchaseRequisitionLine(lc.Item, quantity, unit)], Idem(), ct).ConfigureAwait(false);
        }

        // Backbone: approved PR, confirmed PO, ASN, posted receipt.
        var backbonePr = await DraftPrAsync(lc.BackboneQty).ConfigureAwait(false);
        if (backbonePr.Status == PrStatus.Draft) await ApprovePrAsync(backbonePr.PrNumber).ConfigureAwait(false);
        var backbonePo = await erp.CreatePoAsync(
            backbonePr.PrNumber, lc.Vendor, RecordSource.P2pAgent, requester, Idem(), ct: ct).ConfigureAwait(false);
        if (backbonePo.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", backbonePo.PoNumber, approver, requester, Idem(), ct).ConfigureAwait(false);
            await erp.RecordApprovalAsync("PO", backbonePo.PoNumber, approver, true, "scenario seed", Idem(), ct).ConfigureAwait(false);
        }
        await erp.RecordVendorConfirmationAsync(backbonePo.PoNumber,
            DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime),
            RecordSource.Vendor, lc.Vendor,
            backbonePo.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(),
            Idem(), ct).ConfigureAwait(false);
        record("lc.backbone.pr", "PR", backbonePr.PrNumber);
        record("lc.backbone.po", "PO", backbonePo.PoNumber);
        var backboneLine = backbonePo.Lines[0].PoLineRef;
        var asn = await erp.CreateAsnAsync(lc.Asn, backbonePo.PoNumber, RecordSource.Vendor, lc.Vendor,
            [new AsnCarton(lc.Carton, lc.Item, item.UnitsPerCarton, lc.Lot)], Idem(), ct).ConfigureAwait(false);
        record("lc.backbone.asn", "ASN", asn.AsnNumber);
        var receipt = await erp.PostReceiptAsync(backbonePo.PoNumber, lc.PackingSlip,
            RecordSource.P2pAgent, requester,
            [new ReceiptLine(backboneLine, lc.BackboneQty, 0, 0, lc.Lot, null)], Idem(), ct: ct).ConfigureAwait(false);
        record("lc.backbone.receipt", "RCP", receipt.ReceiptNumber);

        // PR stages: draft, submitted (pending), approved.
        var prDraft = await DraftPrAsync(lc.PrDraftQty).ConfigureAwait(false);
        record("lc.prDraft.pr", "PR", prDraft.PrNumber);
        var prSubmitted = await DraftPrAsync(lc.PrSubmittedQty).ConfigureAwait(false);
        await SubmitPrAsync(prSubmitted.PrNumber, lc.PrSubmittedTo).ConfigureAwait(false);
        record("lc.prSubmitted.pr", "PR", prSubmitted.PrNumber);
        var prApproved = await DraftPrAsync(lc.PrApprovedQty).ConfigureAwait(false);
        if (prApproved.Status == PrStatus.Draft) await ApprovePrAsync(prApproved.PrNumber).ConfigureAwait(false);
        record("lc.prApproved.pr", "PR", prApproved.PrNumber);

        // PO stages: draft, submitted (pending), approved. Each rides its own
        // approved PR, which closes on PO creation.
        async Task<PurchaseOrder> LifecyclePoAsync(decimal quantity, string key)
        {
            var pr = await DraftPrAsync(quantity).ConfigureAwait(false);
            if (pr.Status == PrStatus.Draft) await ApprovePrAsync(pr.PrNumber).ConfigureAwait(false);
            var po = await erp.CreatePoAsync(pr.PrNumber, lc.Vendor, RecordSource.P2pAgent, requester, Idem(), ct: ct).ConfigureAwait(false);
            record($"lc.{key}.pr", "PR", pr.PrNumber);
            record($"lc.{key}.po", "PO", po.PoNumber);
            return po;
        }

        await LifecyclePoAsync(lc.PoDraftQty, "poDraft").ConfigureAwait(false);
        var poSubmitted = await LifecyclePoAsync(lc.PoSubmittedQty, "poSubmitted").ConfigureAwait(false);
        await SubmitPoAsync(poSubmitted.PoNumber, lc.PoSubmittedTo).ConfigureAwait(false);
        var poApproved = await LifecyclePoAsync(lc.PoApprovedQty, "poApproved").ConfigureAwait(false);
        if (poApproved.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", poApproved.PoNumber, approver, requester, Idem(), ct).ConfigureAwait(false);
            await erp.RecordApprovalAsync("PO", poApproved.PoNumber, approver, true, "scenario seed", Idem(), ct).ConfigureAwait(false);
        }

        // Invoice stages on the backbone PO line. Every invoice is matched
        // against the posted backbone receipt, so matched-and-beyond stages
        // carry accepted lines; the received stage stays held awaiting match.
        async Task<string> SeedInvoiceAsync(ScenarioLifecycleInvoice intent)
        {
            await erp.CreateInvoiceAsync(intent.Number, lc.Vendor, backbonePo.PoNumber,
                RecordSource.Vendor, requester,
                [new InvoiceLineInput(backboneLine, intent.Quantity, backbonePo.Lines[0].UnitPrice)],
                Idem(), ct: ct).ConfigureAwait(false);
            var key = $"{lc.Vendor}/{intent.Number}";
            switch (intent.Stage)
            {
                case "received":
                    break;
                case "matched":
                case "submitted":
                case "approved":
                case "posted":
                    await erp.RecordMatchResultAsync(lc.Vendor, intent.Number,
                        [new LineMatchResult(backboneLine, true, "Seed match.")], Idem(), ct).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Lifecycle invoice {intent.Number} has unknown stage {intent.Stage}.");
            }
            if (intent.Stage is "submitted" or "approved" or "posted")
            {
                var routedTo = intent.Stage == "submitted" ? lc.InvoiceSubmittedTo : approver;
                await erp.SubmitForApprovalAsync("Invoice", key, routedTo, requester, Idem(), ct).ConfigureAwait(false);
            }
            if (intent.Stage is "approved" or "posted")
            {
                await erp.RecordApprovalAsync("Invoice", key, approver, true, "scenario seed", Idem(), ct).ConfigureAwait(false);
            }
            if (intent.Stage == "posted")
            {
                await erp.PostInvoiceAsync(lc.Vendor, intent.Number, requester, Idem(), ct: ct).ConfigureAwait(false);
            }
            record($"lc.inv.{intent.Key}.inv", "INV", intent.Number);
            return intent.Number;
        }

        var invoiceNumbers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var intent in lc.Invoices)
        {
            invoiceNumbers[intent.Key] = await SeedInvoiceAsync(intent).ConfigureAwait(false);
        }

        // Payment stages. Each payment rides its own posted invoice so no
        // invoice is ever paid twice; invoice approver (approver) and payment
        // approver (finance) always differ per the SoD refusal.
        string PostedInvoice(string key)
        {
            if (!invoiceNumbers.TryGetValue(key, out var number))
                throw new InvalidOperationException($"Lifecycle has no invoice key {key} for its seed payments.");
            return number;
        }

        var payDraft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey(lc.Vendor, PostedInvoice("payDraft"))],
            RecordSource.P2pAgent, requester, Idem(), ct: ct).ConfigureAwait(false);
        record("lc.payDraft.pay", "PAY", payDraft.PaymentNumber);
        var paySubmitted = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey(lc.Vendor, PostedInvoice("paySubmitted"))],
            RecordSource.P2pAgent, requester, Idem(), ct: ct).ConfigureAwait(false);
        await erp.SubmitForApprovalAsync("Payment", paySubmitted.PaymentNumber, lc.PaySubmittedTo, requester, Idem(), ct).ConfigureAwait(false);
        record("lc.paySubmitted.pay", "PAY", paySubmitted.PaymentNumber);
        var payApproved = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey(lc.Vendor, PostedInvoice("payApproved"))],
            RecordSource.P2pAgent, requester, Idem(), ct: ct).ConfigureAwait(false);
        await erp.SubmitForApprovalAsync("Payment", payApproved.PaymentNumber, finance, requester, Idem(), ct).ConfigureAwait(false);
        await erp.RecordApprovalAsync("Payment", payApproved.PaymentNumber, finance, true, "scenario seed", Idem(), ct).ConfigureAwait(false);
        record("lc.payApproved.pay", "PAY", payApproved.PaymentNumber);
        var payPosted = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey(lc.Vendor, PostedInvoice("paid"))],
            RecordSource.P2pAgent, requester, Idem(), ct: ct).ConfigureAwait(false);
        await erp.SubmitForApprovalAsync("Payment", payPosted.PaymentNumber, finance, requester, Idem(), ct).ConfigureAwait(false);
        await erp.RecordApprovalAsync("Payment", payPosted.PaymentNumber, finance, true, "scenario seed", Idem(), ct).ConfigureAwait(false);
        var posted = await erp.PostPaymentAsync(payPosted.PaymentNumber, finance, Idem(), ct: ct).ConfigureAwait(false);
        record("lc.payPosted.pay", "PAY", posted.PaymentNumber);
    }

    private static ScenarioManifest ReadManifest(
        ScenarioFile scenario, string file, string sqlitePath, ScenarioSqliteStore store)
    {
        // Startup no-op path: the manifest file on disk already matches the store.
        return new ScenarioManifest(
            scenario.Name, file, Path.GetFullPath(sqlitePath), DateTimeOffset.UtcNow,
            store.ReadDocuments());
    }

    private static async Task WriteManifestAsync(string manifestPath, ScenarioManifest manifest, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(ResolvePathForWrite(manifestPath));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(manifest, JsonOpts), ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a configured path: as given, else by walking up from
    /// the app base directory (dotnet run uses the project directory).</summary>
    public static string ResolvePath(string configured)
    {
        if (File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configured);
    }

    private static string ResolvePathForWrite(string configured)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, configured))
                || Directory.Exists(Path.Combine(dir.FullName, Path.GetDirectoryName(configured) ?? string.Empty)))
            {
                return Path.Combine(dir.FullName, configured);
            }
        }

        return configured;
    }
}

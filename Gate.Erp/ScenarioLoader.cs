// Scenario loader: fills the mini-ERP from a scenario file on startup (when
// empty) and on reset (wipe plus reload). Additive only: it calls the
// adapter's existing seed and document APIs and follows returned ids. Number
// sequences in sqlite mint every SO/ASN/INV number at posting; PR/PO/RCT/PAY
// numbers come from the adapter's own sequences. Every generated id is
// recorded in sqlite and in scenarios/run-manifest.json (git-ignored), keyed
// by scenario event. No business values live here; all of them come from the
// scenario file.
using System.Text.Json;
using Gate.Contracts;

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
        if (adapter is not InMemoryErpAdapter erp)
        {
            throw new InvalidOperationException("ScenarioLoader seeds the mini-ERP adapter only.");
        }

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

        erp.SeedBaseCurrency(scenario.Currency);
        erp.SeedAutoApproveThreshold(scenario.AutoApproveUnder);
        foreach (var item in scenario.Items)
        {
            erp.SeedItem(new ItemDto(item.Id, item.Description, item.Critical, item.PackSize, item.Unit));
        }
        foreach (var terms in scenario.PaymentTerms)
        {
            erp.SeedPaymentTerms(new PaymentTermsDto(terms.Id, terms.NetDays));
        }

        var soNumber = store.NextNumber("SO");
        erp.SeedSalesOrder(new SalesOrder(
            soNumber,
            SalesOrderStatus.Created,
            scenario.Currency,
            [new SalesOrderLine(scenario.SalesOrder.Item, scenario.SalesOrder.Quantity)]));
        Record("salesOrder", "SO", soNumber);

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
            erp.SeedVendor(new VendorDto(v.Id, v.Name, v.Country, false, null, v.Terms));
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

        var requester = scenario.Users.FirstOrDefault(u => u.Role == "requester")?.Name ?? "scenario";
        var approver = scenario.Users.FirstOrDefault(u => u.Role == "approver")?.Name ?? "scenario";

        var inbound = scenario.OpenPoInbound;
        var inboundPo = await CreateApprovedPoAsync(
            erp, requester, approver, scenario.Currency,
            inbound.Item, inbound.Quantity, inbound.Vendor, ct).ConfigureAwait(false);
        Record("openPoInbound.pr", "PR", inboundPo.PrNumber);
        Record("openPoInbound.po", "PO", inboundPo.PoNumber);

        foreach (var small in scenario.SmallPos)
        {
            erp.SeedVendorPrice(small.Vendor, small.Item, small.UnitPrice);
            var created = await CreateApprovedPoAsync(
                erp, requester, approver, scenario.Currency,
                small.Item, small.Quantity, small.Vendor, ct).ConfigureAwait(false);
            Record($"smallPo.{small.Key}.pr", "PR", created.PrNumber);
            Record($"smallPo.{small.Key}.po", "PO", created.PoNumber);
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
        if (adapter is not InMemoryErpAdapter erp)
        {
            throw new InvalidOperationException("ScenarioLoader seeds the mini-ERP adapter only.");
        }

        erp.Clear();
        using (var store = ScenarioSqliteStore.Open(sqlitePath))
        {
            store.Clear();
        }

        return await LoadAsync(adapter, scenarioPath, sqlitePath, manifestPath, ct).ConfigureAwait(false);
    }

    private static async Task<(string PrNumber, string PoNumber)> CreateApprovedPoAsync(
        InMemoryErpAdapter erp,
        string requester,
        string approver,
        string currency,
        string item,
        decimal quantity,
        string vendor,
        CancellationToken ct)
    {
        var pr = await erp.CreatePrAsync(
            requester, RecordSource.P2pAgent, requester,
            [new PurchaseRequisitionLine(item, quantity, "EA")], Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            await erp.RecordApprovalAsync("PR", pr.PrNumber, approver, true, "scenario seed", Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.P2pAgent, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
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

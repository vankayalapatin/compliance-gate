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

        var soNumber = store.NextNumber("SO");
        erp.SeedSalesOrder(new SalesOrder(
            soNumber,
            DocStatus.Submitted,
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

        // TODO (erp-store): seeding through InMemoryErpAdapter is temporary
        // until the EF Core ERP lands. The loader will then seed through the
        // EF-backed adapter while keeping the same manifest contract
        // (sequence-minted numbers recorded per scenario event).
        foreach (var v in scenario.Vendors)
        {
            erp.SeedVendor(new VendorDto(v.Id, v.Name, v.Country, false, null));
            erp.SeedVendorDocuments(v.Id, v.Documents.Select(d => new VendorDocumentDto(v.Id, d.Kind, d.Reference, d.Present)));
        }

        // Raw trailing history only. Shares are computed by compliance at
        // evaluation time; nothing is precomputed here.
        erp.SeedVolumeHistory(
            scenario.HistoryPart,
            scenario.HistoryCells.Select(h => new VolumeHistory(h.Source, h.Country, h.Cells)));

        foreach (var u in scenario.Users)
        {
            erp.SeedSigningLimit(u.Name, u.SigningLimit);
        }

        var requester = scenario.Users.FirstOrDefault(u => u.Role == "requester")?.Name ?? "scenario";
        var approver = scenario.Users.FirstOrDefault(u => u.Role == "approver")?.Name ?? "scenario";

        var inbound = scenario.OpenPoInbound;
        var inboundPrice = scenario.Vendors.Single(v => v.Id == inbound.Vendor).UnitPrice;
        var inboundPo = await CreateApprovedPoAsync(
            erp, requester, approver, scenario.Currency,
            inbound.Item, inbound.Quantity, inboundPrice, inbound.Vendor, ct).ConfigureAwait(false);
        Record("openPoInbound.pr", "PR", inboundPo.PrNumber);
        Record("openPoInbound.po", "PO", inboundPo.PoNumber);

        foreach (var small in scenario.SmallPos)
        {
            var created = await CreateApprovedPoAsync(
                erp, requester, approver, scenario.Currency,
                small.Item, small.Quantity, small.UnitPrice, small.Vendor, ct).ConfigureAwait(false);
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
        decimal unitPrice,
        string vendor,
        CancellationToken ct)
    {
        var pr = await erp.CreatePrAsync(
            requester, RecordSource.P2pAgent,
            [new PurchaseRequisitionLine(item, quantity, "EA")], ct).ConfigureAwait(false);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, ct).ConfigureAwait(false);
        await erp.RecordApprovalAsync(new ApprovalDto(
            "PR", pr.PrNumber, approver, approver, false,
            DateTimeOffset.UtcNow, true, "scenario seed", quantity * unitPrice), ct).ConfigureAwait(false);
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, currency, RecordSource.P2pAgent, ct).ConfigureAwait(false);
        foreach (var line in po.Lines)
        {
            erp.SetPoLinePrice(po.PoNumber, line.PoLineRef, unitPrice);
        }

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

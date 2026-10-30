// Full-case scenario runs: the same driver walks demo-v1 and alt-v1 from the
// order webhook through vendor screening (cheaper vendor blocked citing
// CORP-02), dock holds plus reviews, invoice matching, and the pre-pay gate.
// Every expected number is computed from the scenario JSON plus the run
// manifest at runtime; nothing is hard-coded. Both scenarios seed NET30
// terms, so the ERP refuses same-day drafts and the runner parks in
// AwaitDueDate with the refusal quoted; payment posting itself is proven on
// NET0 data in CaseRunnerTests.
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ScenarioCaseTests
{
    private sealed record ScenarioCarton(
        string Id, string Part, int Units, bool MarkPresent, string Damage, IReadOnlyList<string> VisibleIn);

    private sealed record ScenarioSmallPo(
        string Key, string Item, decimal Quantity, string Vendor, decimal UnitPrice);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-scenario-test", Guid.NewGuid().ToString("N"));

    private static CaseFile Load(string caseDir, string caseId) =>
        CaseStore.Load(caseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    private static Decision Pending(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    private static List<DockCartonRead> ReadsFor(
        IReadOnlyList<ScenarioCarton> cartons, string poNumber, Func<string, string> lotOf)
    {
        var reads = new List<DockCartonRead>();
        foreach (var carton in cartons)
        {
            var damaged = carton.Damage != "none" && carton.Damage != "none, hidden";
            foreach (var image in carton.VisibleIn)
            {
                reads.Add(new DockCartonRead(
                    carton.Id, carton.Part, poNumber, lotOf(carton.Id), carton.Units,
                    carton.MarkPresent, carton.MarkPresent, "UN3480", true, true,
                    damaged, damaged ? carton.Damage : null, image, 0.95m));
            }
        }

        return reads;
    }

    [Theory]
    [InlineData("scenarios/demo-v1.json")]
    [InlineData("scenarios/alt-v1.json")]
    public async Task FullScenarioRun(string scenarioPath)
    {
        var resolved = ScenarioLoader.ResolvePath(scenarioPath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resolved));
        var root = json.RootElement;
        var scenarioName = root.GetProperty("name").GetString()!;
        var cartons = root.GetProperty("cartons").EnumerateArray().Select(c => new ScenarioCarton(
            c.GetProperty("id").GetString()!,
            c.GetProperty("part").GetString()!,
            c.GetProperty("units").GetInt32(),
            c.GetProperty("markPresent").GetBoolean(),
            c.GetProperty("damage").GetString()!,
            c.GetProperty("visibleIn").EnumerateArray().Select(v => v.GetString()!).ToList())).ToList();
        var main = root.GetProperty("invoiceIntents").GetProperty("main");
        var mainVendor = main.GetProperty("vendor").GetString()!;
        var mainQty = main.GetProperty("quantity").GetDecimal();
        var mainPrice = main.GetProperty("unitPrice").GetDecimal();
        var smallPos = root.GetProperty("smallPos").EnumerateArray().Select(s => new ScenarioSmallPo(
            s.GetProperty("key").GetString()!,
            s.GetProperty("item").GetString()!,
            s.GetProperty("quantity").GetDecimal(),
            s.GetProperty("vendor").GetString()!,
            s.GetProperty("unitPrice").GetDecimal())).ToList();
        var intents = root.GetProperty("invoiceIntents");
        var dupKey = intents.GetProperty("duplicate").GetProperty("smallPo").GetString()!;
        var bankKey = intents.GetProperty("bankChange").GetProperty("smallPo").GetString()!;
        var missingKey = intents.GetProperty("missingFields").GetProperty("smallPo").GetString()!;
        var candidates = root.GetProperty("vendors").EnumerateArray()
            .GroupBy(v => v.GetProperty("item").GetString()!)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(v => v.GetProperty("id").GetString()!).ToList());
        var users = root.GetProperty("users").EnumerateArray().ToList();
        var requester = users.First(u => u.GetProperty("role").GetString() == "requester")
            .GetProperty("name").GetString()!;
        var clerk = users.First(u => u.GetProperty("role").GetString() == "ap-clerk")
            .GetProperty("name").GetString()!;
        var delegateTo = root.GetProperty("delegations").EnumerateArray().First()
            .GetProperty("to").GetString()!;

        var tmp = TempDir();
        Directory.CreateDirectory(tmp);
        var erp = new InMemoryErpAdapter();
        var manifest = await ScenarioLoader.LoadAsync(
            erp, resolved, Path.Combine(tmp, "gate.db"), Path.Combine(tmp, "manifest.json"));
        string Doc(string eventKey) => manifest.Documents.Single(d => d.Event == eventKey).Number;
        var soNumber = Doc("salesOrder");

        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var caseDir = Path.Combine(tmp, "cases");
        var core = new P2pAgent(erp, models, compliance);
        var runner = new CaseRunner(erp, core, models, compliance, new DockEvaluatorAdapter(), caseDir);

        var caseIds = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput(scenarioName, soNumber, requester, candidates));
        Assert.Equal(2, caseIds.Count);
        Assert.Equal(SalesOrderStatus.Confirmed, (await erp.GetSalesOrderAsync(soNumber))!.Status);
        var batItem = cartons[0].Part;
        var bat = caseIds.Single(c => c.EndsWith("-" + batItem, StringComparison.Ordinal));
        var screw = caseIds.Single(c => c != bat);
        Assert.Equal(soNumber, Load(caseDir, bat).Case.SoNumber);

        await runner.AdvanceAsync(bat);
        var prDecision = Pending(Load(caseDir, bat), DecisionKind.PrApproval);
        Assert.Equal(delegateTo, prDecision.RequestedTo);
        await runner.ResolveDecisionAsync(bat, prDecision.DecisionId, true, delegateTo, "ok");

        await runner.AdvanceAsync(bat);
        var poDecision = Pending(Load(caseDir, bat), DecisionKind.PoApproval);
        Assert.Equal(delegateTo, poDecision.RequestedTo);
        await runner.ResolveDecisionAsync(bat, poDecision.DecisionId, true, delegateTo, "ok");

        var batPo = Load(caseDir, bat).Case.PoNumber!;
        Assert.Equal(mainVendor, (await erp.GetPurchaseOrderAsync(batPo))!.VendorId);
        Assert.Equal(
            ComplianceEngine.CorpOriginCapId,
            Load(caseDir, bat).Events.Single(e => e.Kind == CaseEventKind.VendorBlocked).RuleId);

        await runner.AdvanceAsync(bat);
        await runner.OnSupplierAckAsync(new SupplierAckInput(bat));
        await runner.AdvanceAsync(bat);
        string LotOf(string cartonId) => $"{batPo}-{cartonId}";
        await runner.OnAsnAsync(new SupplierAsnInput(bat, $"ASN-{batPo}", $"PACK-{batPo}",
            cartons.Select(c => new AsnCartonInput(c.Id, c.Part, c.Units, LotOf(c.Id))).ToList()));

        await runner.AdvanceAsync(bat);
        var verdicts = await runner.OnDockReadsAsync(bat, ReadsFor(cartons, batPo, LotOf));
        var unmarked = cartons.Single(c => !c.MarkPresent);
        Assert.Contains(verdicts[unmarked.Id],
            v => v.Kind == VerdictKind.Hold && v.RuleId == DockEvaluator.MarkRuleId);
        var docked = Load(caseDir, bat);
        Assert.Single(docked.Decisions, d =>
            d.Kind == DecisionKind.DamageReview && d.Status == DecisionStatus.Pending);
        Assert.Single(docked.Decisions, d =>
            d.Kind == DecisionKind.DockReview && d.Status == DecisionStatus.Pending);
        foreach (var review in docked.Decisions
            .Where(d => d.Status == DecisionStatus.Pending
                && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
            .ToList())
        {
            await runner.ResolveDecisionAsync(bat, review.DecisionId, true, review.RequestedTo, "confirmed on the dock");
        }

        await runner.AdvanceAsync(bat);
        var batLine = (await erp.GetPurchaseOrderAsync(batPo))!.Lines.Single();
        var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            bat, $"INV-{batPo}", mainVendor, batPo,
            [new SupplierInvoiceLineInput(batLine.PoLineRef, mainQty, mainPrice)]));
        var heldUnits = unmarked.Units;
        var acceptedUnits = cartons.Sum(c => c.Units) - heldUnits;
        Assert.True(intake.Persisted);
        Assert.Equal(acceptedUnits * mainPrice, intake.AcceptedValue);
        Assert.Equal(heldUnits * mainPrice, intake.HeldValue);

        var invDecision = Pending(Load(caseDir, bat), DecisionKind.InvoiceApproval);
        Assert.Equal("Invoice", invDecision.DocumentType);
        Assert.Equal($"{mainVendor}/INV-{batPo}", invDecision.DocumentNumber);
        await runner.ResolveDecisionAsync(bat, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        var parked = await runner.AdvanceAsync(bat);
        Assert.Equal(CaseNextStep.AwaitDueDate, parked.Step);
        var prepaid = Load(caseDir, bat);
        Assert.Contains(prepaid.Events, e =>
            e.Kind == CaseEventKind.Note && e.DocumentType == "Payment" && e.Summary.Contains("Nothing due"));
        Assert.Equal(ComplianceEngine.PrePayHoldId,
            prepaid.Events.First(e => e.Kind == CaseEventKind.Note && e.DocumentType == "Payment").RuleId);
        var batGate = Assert.Single(compliance.PrePayStates);
        Assert.Single(batGate.OpenHolds);
        Assert.Equal(DockEvaluator.MarkRuleId, batGate.OpenHolds[0].RuleId);
        Assert.Equal(unmarked.Id, batGate.OpenHolds[0].CartonId);
        Assert.Equal([LotOf(unmarked.Id)], batGate.QuarantinedLotRefs);
        Assert.Empty(batGate.OpenReviews);
        Assert.Empty(prepaid.Case.PaymentNumbers);

        await TrickyInboxAsync(erp, runner, caseDir, bat, smallPos, dupKey, bankKey, missingKey,
            requester, clerk, delegateTo, Doc);
        await StraightThroughAsync(erp, runner, caseDir, screw, compliance);
        Assert.Equal(2, compliance.PrePayStates.Count);
    }

    private static async Task TrickyInboxAsync(
        InMemoryErpAdapter erp, CaseRunner runner, string caseDir, string caseId,
        IReadOnlyList<ScenarioSmallPo> smallPos, string dupKey, string bankKey, string missingKey,
        string requester, string clerk, string delegateTo, Func<string, string> doc)
    {
        var dup = smallPos.Single(s => s.Key == dupKey);
        var dupPo = doc($"smallPo.{dupKey}.po");
        var dupLine = (await erp.GetPurchaseOrderAsync(dupPo))!.Lines.Single();
        var dupNumber = doc($"smallPo.{dupKey}.invoice");
        Assert.Equal(InvoiceStatus.Posted, (await erp.GetInvoiceAsync(dup.Vendor, dupNumber))!.Status);
        var dupSecond = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, dupNumber, dup.Vendor, dupPo,
            [new SupplierInvoiceLineInput(dupLine.PoLineRef, dup.Quantity, dup.UnitPrice)]));
        Assert.False(dupSecond.Persisted);
        var dupFile = Load(caseDir, caseId);
        Assert.Contains(dupFile.Events, e => e.Summary.Contains("AP-C1"));
        var dupDecision = dupFile.Decisions.Single(d => d.DecisionId == dupSecond.DecisionId);
        Assert.Equal(DecisionKind.ApReview, dupDecision.Kind);
        Assert.Equal(clerk, dupDecision.RequestedTo);

        var bank = smallPos.Single(s => s.Key == bankKey);
        var bankPo = doc($"smallPo.{bankKey}.po");
        var bankLine = (await erp.GetPurchaseOrderAsync(bankPo))!.Lines.Single();
        var master = (await erp.GetVendorBankAsync(bank.Vendor))!;
        var bankOutcome = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, $"INV-BANK-{bankPo}", bank.Vendor, bankPo,
            [new SupplierInvoiceLineInput(bankLine.PoLineRef, bank.Quantity, bank.UnitPrice)],
            BankAccountRef: master.AccountRef + "-NEW"));
        Assert.Equal(0, bankOutcome.AcceptedValue);
        var bankFile = Load(caseDir, caseId);
        var bankDecision = bankFile.Decisions.Single(d => d.DecisionId == bankOutcome.DecisionId);
        Assert.Equal(DecisionKind.BankVerification, bankDecision.Kind);
        Assert.Equal("VendorBank", bankDecision.DocumentType);
        Assert.Equal(bank.Vendor, bankDecision.DocumentNumber);
        await runner.ResolveDecisionAsync(caseId, bankDecision.DecisionId, true, clerk, "verified by phone");
        Assert.True((await erp.GetVendorBankAsync(bank.Vendor))!.Verified);

        var missing = smallPos.Single(s => s.Key == missingKey);
        var missingOutcome = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, $"INV-MISS-{missing.Key}", null, null,
            [new SupplierInvoiceLineInput(null, missing.Quantity, missing.UnitPrice)]));
        Assert.False(missingOutcome.Persisted);
        Assert.Equal(missing.Quantity * missing.UnitPrice, missingOutcome.HeldValue);
        var missFile = Load(caseDir, caseId);
        var missDecision = missFile.Decisions.Single(d => d.DecisionId == missingOutcome.DecisionId);
        Assert.Equal(DecisionKind.ApReview, missDecision.Kind);
        Assert.Equal(clerk, missDecision.RequestedTo);
    }

    private static async Task StraightThroughAsync(
        InMemoryErpAdapter erp, CaseRunner runner, string caseDir, string caseId,
        RecordingComplianceEngine compliance)
    {
        await runner.AdvanceAsync(caseId);
        var ordered = Load(caseDir, caseId);
        Assert.DoesNotContain(ordered.Decisions,
            d => d.Kind is DecisionKind.PrApproval or DecisionKind.PoApproval);
        var poNumber = ordered.Case.PoNumber!;
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        var item = ordered.Case.ItemId;
        var qty = po.Lines.Single().QtyOrdered;

        await runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await runner.AdvanceAsync(caseId);
        await runner.OnAsnAsync(new SupplierAsnInput(caseId, $"ASN-{poNumber}", $"PACK-{poNumber}",
            [new AsnCartonInput($"CTN-{poNumber}", item, (int)qty, $"LOT-{poNumber}")]));
        await runner.AdvanceAsync(caseId);
        var verdicts = await runner.OnDockReadsAsync(caseId,
            [new DockCartonRead($"CTN-{poNumber}", item, poNumber, $"LOT-{poNumber}", (int)qty,
                true, true, "UN3480", false, false, false, null, "IMG-S1", 0.95m)]);
        Assert.All(verdicts.Values.SelectMany(v => v), v => Assert.Equal(VerdictKind.Pass, v.Kind));

        await runner.AdvanceAsync(caseId);
        var line = (await erp.GetPurchaseOrderAsync(poNumber))!.Lines.Single();
        var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, $"INV-{poNumber}", po.VendorId, poNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, qty, line.UnitPrice)]));
        Assert.Equal(0, intake.HeldValue);

        var invoiced = Load(caseDir, caseId);
        var invDecision = Pending(invoiced, DecisionKind.InvoiceApproval);
        await runner.ResolveDecisionAsync(caseId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        var parked = await runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDueDate, parked.Step);
        var gate = compliance.PrePayStates[^1];
        Assert.Empty(gate.OpenHolds);
        Assert.Empty(gate.QuarantinedLotRefs);
        Assert.Empty(gate.OpenReviews);
    }
}

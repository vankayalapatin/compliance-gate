// Picker tests: every branch of the next-step pick, from case state plus ERP
// reads plus pending decisions. Pure function, no adapter needed.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CasePickerTests
{
    private static CaseFile File(
        CaseStage stage,
        string? pr = null, string? po = null, string? asn = null, string? receipt = null,
        IReadOnlyList<Decision>? decisions = null, IReadOnlyList<CaseEvent>? events = null,
        string? awarded = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new CaseFile(
            new Case("case-1", "SO-1", "BAT-1", "test", stage, now, now,
                pr, po, asn, receipt, [], [], null, null, awarded),
            events ?? [], decisions ?? []);
    }

    private static Decision Pending(DecisionKind kind, string doc = "DOC-1", string docType = "Doc") =>
        new("d1", "case-1", kind, "title", DecisionStatus.Pending, "someone",
            DateTimeOffset.UtcNow, null, null, docType, doc, null, null, null,
            "reason", [], null);

    private static CaseEvent MailEvent(CaseEventKind kind, string emailId) =>
        new(emailId, "case-1", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            kind, kind.ToString(), null, "Invoice", "V-X/INV-9", $"mail:{emailId}", null, null);

    private static CaseErpView View(
        bool soExists = true,
        PrStatus? prStatus = null,
        PoApprovalStatus? poApproval = null,
        bool confirmation = false,
        bool asn = false,
        bool receipt = false,
        int matched = 0,
        decimal accepted = 0,
        decimal paid = 0,
        bool held = false,
        int holds = 0,
        bool notified = false) =>
        new(soExists, prStatus, poApproval, confirmation, asn, receipt,
            matched, accepted, paid, held, holds, notified);

    private static CaseEvent DueMarker() =>
        new("e9", "case-1", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.Note, CasePicker.PaymentNotDueMarker + ": ERP refused the draft.",
            null, "Payment", null, null, null, null);

    [Fact]
    public void TerminalStagesAndMissingSoAreDone()
    {
        Assert.Equal(CaseNextStep.Done, CasePicker.Pick(File(CaseStage.Resolved), View()).Step);
        Assert.Equal(CaseNextStep.Done, CasePicker.Pick(File(CaseStage.Cancelled), View()).Step);
        Assert.Equal(CaseNextStep.Done, CasePicker.Pick(File(CaseStage.Created), View(soExists: false)).Step);
    }

    [Fact]
    public void CreatedComputesShortfallThenRaisesPr()
    {
        Assert.Equal(CaseNextStep.ComputeShortfall, CasePicker.Pick(File(CaseStage.Created), View()).Step);
        Assert.Equal(CaseNextStep.RaisePr,
            CasePicker.Pick(File(CaseStage.ShortfallComputed), View()).Step);
    }

    [Fact]
    public void PendingPrApprovalBlocksAndApprovedPrScreens()
    {
        Assert.Equal(CaseNextStep.AwaitDecision,
            CasePicker.Pick(
                File(CaseStage.ShortfallComputed, pr: "PR-1", decisions: [Pending(DecisionKind.PrApproval, "PR-1")]),
                View(prStatus: PrStatus.Submitted)).Step);
        Assert.Equal(CaseNextStep.ScreenVendors,
            CasePicker.Pick(File(CaseStage.PrSubmitted, pr: "PR-1"), View(prStatus: PrStatus.Approved)).Step);
        Assert.Equal(CaseNextStep.ScreenVendors,
            CasePicker.Pick(File(CaseStage.PrApproved, pr: "PR-1"), View(prStatus: PrStatus.Approved)).Step);
    }

    private static CaseEvent Screened(string vendorId) =>
        new("e7", "case-1", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.VendorScreened, $"Vendor {vendorId} passed screening.",
            null, null, null, null, null, null);

    [Fact]
    public void SourcingWalksScreenAwardCreate()
    {
        var view = View(prStatus: PrStatus.Approved);
        var unscreened = File(CaseStage.PrApproved, pr: "PR-1");
        Assert.Equal(CaseNextStep.ScreenVendors, CasePicker.Pick(unscreened, view).Step);
        Assert.Contains(AgentTools.ScreenVendors, CasePicker.LegalTools(unscreened, view));
        Assert.DoesNotContain(AgentTools.AwardSupplier, CasePicker.LegalTools(unscreened, view));

        var screened = File(CaseStage.PrApproved, pr: "PR-1", events: [Screened("V-B")]);
        Assert.Equal(CaseNextStep.AwardSupplier, CasePicker.Pick(screened, view).Step);
        Assert.Contains(AgentTools.AwardSupplier, CasePicker.LegalTools(screened, view));

        var awarded = File(CaseStage.PrApproved, pr: "PR-1", events: [Screened("V-B")], awarded: "V-B");
        Assert.Equal(CaseNextStep.CreatePo, CasePicker.Pick(awarded, view).Step);
        Assert.Contains(AgentTools.CreatePo, CasePicker.LegalTools(awarded, view));
    }

    [Fact]
    public void DraftPoWithoutDecisionResubmits()
    {
        Assert.Equal(CaseNextStep.SubmitPoApproval,
            CasePicker.Pick(
                File(CaseStage.PoPlaced, pr: "PR-1", po: "PO-1"),
                View(prStatus: PrStatus.Closed, poApproval: PoApprovalStatus.Draft)).Step);
        Assert.Equal(CaseNextStep.AwaitDecision,
            CasePicker.Pick(
                File(CaseStage.PoPlaced, pr: "PR-1", po: "PO-1",
                    decisions: [Pending(DecisionKind.PoApproval, "PO-1")]),
                View(prStatus: PrStatus.Closed, poApproval: PoApprovalStatus.InReview)).Step);
    }

    [Fact]
    public void SupplierLegWaitsOnAckAsnAndReads()
    {
        Assert.Equal(CaseNextStep.AwaitSupplierAck,
            CasePicker.Pick(
                File(CaseStage.PoPlaced, pr: "PR-1", po: "PO-1"),
                View(prStatus: PrStatus.Closed, poApproval: PoApprovalStatus.Approved)).Step);
        Assert.Equal(CaseNextStep.AwaitAsn,
            CasePicker.Pick(
                File(CaseStage.PoAcknowledged, pr: "PR-1", po: "PO-1"),
                View(prStatus: PrStatus.Closed, poApproval: PoApprovalStatus.Confirmed, confirmation: true)).Step);
        Assert.Equal(CaseNextStep.AwaitAsn,
            CasePicker.Pick(
                File(CaseStage.AsnRecorded, pr: "PR-1", po: "PO-1", asn: "ASN-1"),
                View(prStatus: PrStatus.Closed, confirmation: true, asn: true)).Step);
        Assert.Equal(CaseNextStep.AwaitDockReads,
            CasePicker.Pick(
                File(CaseStage.ReceiptPosted, pr: "PR-1", po: "PO-1", asn: "ASN-1", receipt: "RCT-1"),
                View(prStatus: PrStatus.Closed, confirmation: true, asn: true, receipt: true)).Step);
    }

    [Fact]
    public void NotifyFiresOnceMaterialsAreSecured()
    {
        var file = File(CaseStage.DockChecked, pr: "PR-1", po: "PO-1", asn: "ASN-1", receipt: "RCT-1");
        Assert.Equal(CaseNextStep.NotifyCustomer,
            CasePicker.Pick(file, View(prStatus: PrStatus.Closed, confirmation: true, asn: true, receipt: true)).Step);

        var held = File(CaseStage.DockChecked, pr: "PR-1", po: "PO-1", asn: "ASN-1", receipt: "RCT-1");
        Assert.Equal(CaseNextStep.AwaitInvoices,
            CasePicker.Pick(held,
                View(prStatus: PrStatus.Closed, confirmation: true, asn: true, receipt: true, holds: 1)).Step);
    }

    [Fact]
    public void MoneyDecisionsAndBankVerificationParkThePayLeg()
    {
        var invoicePending = File(CaseStage.InvoiceMatched, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.InvoiceApproval, "V-B/INV-1")]);
        Assert.Equal(CaseNextStep.AwaitDecision,
            CasePicker.Pick(invoicePending, View(matched: 1, notified: true)).Step);

        var bankPending = File(CaseStage.InvoiceMatched, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.BankVerification, "V-B")]);
        Assert.Equal(CaseNextStep.AwaitDecision,
            CasePicker.Pick(bankPending, View(matched: 1, accepted: 100, notified: true)).Step);

        Assert.Equal(CaseNextStep.AwaitInvoices,
            CasePicker.Pick(File(CaseStage.DockChecked), View(notified: true)).Step);
    }

    [Fact]
    public void PrePayRunsUntilPaidThenWaitsOrFinishes()
    {
        var file = File(CaseStage.InvoiceMatched, pr: "PR-1", po: "PO-1");
        Assert.Equal(CaseNextStep.DraftPayment,
            CasePicker.Pick(file, View(matched: 1, accepted: 100, paid: 60, held: true, notified: true)).Step);

        var parked = File(CaseStage.InvoiceMatched, pr: "PR-1", po: "PO-1", events: [DueMarker()]);
        Assert.Equal(CaseNextStep.AwaitDueDate,
            CasePicker.Pick(parked, View(matched: 1, accepted: 100, paid: 60, notified: true)).Step);

        var waiting = File(CaseStage.PaymentPartial, pr: "PR-1", po: "PO-1");
        Assert.Equal(CaseNextStep.AwaitDecision,
            CasePicker.Pick(waiting, View(matched: 1, accepted: 100, paid: 100, held: true, notified: true)).Step);

        var clean = File(CaseStage.PaymentPartial, pr: "PR-1", po: "PO-1");
        Assert.Equal(CaseNextStep.FinishCase,
            CasePicker.Pick(clean, View(matched: 1, accepted: 100, paid: 100, notified: true)).Step);

        var cleanView = View(matched: 1, accepted: 100, paid: 100, notified: true);
        var apOnly = File(CaseStage.PaymentPartial, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.ApReview, "V-X/INV-9", "Invoice")]);
        Assert.Equal(CaseNextStep.QueryVendor, CasePicker.Pick(apOnly, cleanView).Step);
        Assert.Contains(AgentTools.SendVendorEmail, CasePicker.LegalTools(apOnly, cleanView));

        var emailed = apOnly with
        {
            Events = [MailEvent(CaseEventKind.VendorEmailed, "q1")]
        };
        Assert.Equal(CaseNextStep.AwaitVendorReply, CasePicker.Pick(emailed, cleanView).Step);

        var answered = apOnly with
        {
            Events = [MailEvent(CaseEventKind.VendorEmailed, "q1"), MailEvent(CaseEventKind.VendorReplied, "r1")]
        };
        Assert.Equal(CaseNextStep.AwaitDecision, CasePicker.Pick(answered, cleanView).Step);

        var reviewStuck = File(CaseStage.PaymentPartial, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.DamageReview, "RCT-1")]);
        Assert.Equal(CaseNextStep.AwaitDecision,
            CasePicker.Pick(reviewStuck, View(matched: 1, accepted: 100, paid: 100, notified: true)).Step);
    }

    [Fact]
    public void PaymentStepOffersOnlyThePaymentTool()
    {
        // Each step offers only its own tool: at DraftPayment the agent
        // drafts, even while an unemailed invoice problem stands. Email
        // belongs to the vendor-query step alone.
        var file = File(CaseStage.InvoiceMatched, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.ApReview, "V-X/INV-9", "Invoice")]);
        var view = View(matched: 1, accepted: 100, paid: 60, notified: true);

        Assert.Equal(CaseNextStep.DraftPayment, CasePicker.Pick(file, view).Step);
        var legal = CasePicker.LegalTools(file, view);
        Assert.Contains(AgentTools.DraftPayment, legal);
        Assert.DoesNotContain(AgentTools.SendVendorEmail, legal);
        Assert.DoesNotContain(AgentTools.NotifyCustomer, legal);
    }

    [Fact]
    public void BlankRefsAreClerkOnlyNeverEmailed()
    {
        // A problem ref without a number names no queryable invoice: it
        // never qualifies for email and the clerk owns it.
        var file = File(CaseStage.PaymentPartial, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.ApReview, "V-X/", "Invoice")]);
        var view = View(matched: 1, accepted: 100, paid: 100, notified: true);

        Assert.Empty(CasePicker.UnemailedInvoiceProblems(file));
        Assert.Equal(CaseNextStep.AwaitDecision, CasePicker.Pick(file, view).Step);
        Assert.DoesNotContain(AgentTools.SendVendorEmail, CasePicker.LegalTools(file, view));
    }

    [Fact]
    public void VendorBlankRefsStayEmailableThroughThePo()
    {
        // A blank vendor half is fine: the query resolves the vendor from
        // the case PO, so the problem still qualifies for email.
        var file = File(CaseStage.PaymentPartial, pr: "PR-1", po: "PO-1",
            decisions: [Pending(DecisionKind.ApReview, "/INV-9", "Invoice")]);
        var view = View(matched: 1, accepted: 100, paid: 100, notified: true);

        Assert.Equal("/INV-9", Assert.Single(CasePicker.UnemailedInvoiceProblems(file)).DocumentNumber);
        Assert.Equal(CaseNextStep.QueryVendor, CasePicker.Pick(file, view).Step);
        Assert.Contains(AgentTools.SendVendorEmail, CasePicker.LegalTools(file, view));
    }
}

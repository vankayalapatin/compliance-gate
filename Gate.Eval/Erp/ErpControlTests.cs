// ERP control refusals: SoD, limits, PR requirements, held value, duplicates.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ErpControlTests
{
    [Fact]
    public async Task RequesterCannotApproveOwnPr()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, [new PurchaseRequisitionLine("BAT-1", 1, "EA")]);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync(new ApprovalDto("PR", pr.PrNumber, "approver", "planner", false, DateTimeOffset.UtcNow, true, null, 10)));
    }

    [Fact]
    public async Task OverLimitApprovalRefused()
    {
        var erp = SeededErp.Create();
        erp.SeedSigningLimit("approver", 100);
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, [new PurchaseRequisitionLine("BAT-1", 1, "EA")]);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync(new ApprovalDto("PR", pr.PrNumber, "approver", "approver", false, DateTimeOffset.UtcNow, true, null, 500)));
    }

    [Fact]
    public async Task DelegateApprovalCoversApprover()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, [new PurchaseRequisitionLine("BAT-1", 1, "EA")]);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver");
        await erp.RecordApprovalAsync(new ApprovalDto("PR", pr.PrNumber, "approver", "delegate", true, DateTimeOffset.UtcNow, true, null, 10));
        var stored = await erp.GetApprovalsAsync("PR", pr.PrNumber);
        Assert.Single(stored);
        Assert.True(stored[0].ActingAsDelegate);
    }

    [Fact]
    public async Task PoNeedsApprovedPr()
    {
        var erp = SeededErp.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreatePoAsync("PR-999", "V-B", "USD", RecordSource.P2pAgent));
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, [new PurchaseRequisitionLine("BAT-1", 1, "EA")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreatePoAsync(pr.PrNumber, "V-B", "USD", RecordSource.P2pAgent));
    }

    [Fact]
    public async Task DuplicateInvoiceRefused()
    {
        var erp = SeededErp.Create();
        var inv = new VendorInvoice("INV-1", "V-B", "PO-1", DocStatus.Submitted, "USD", "vendor",
            [new VendorInvoiceLine("PO-L1", 1, 10, LineDisposition.Accepted, null)], []);
        await erp.CreateInvoiceAsync(inv);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erp.CreateInvoiceAsync(inv));
    }

    [Fact]
    public async Task HeldValueCannotBePaid()
    {
        var erp = SeededErp.Create();
        var inv = new VendorInvoice("INV-1", "V-B", "PO-1", DocStatus.Submitted, "USD", "vendor",
            [
                new VendorInvoiceLine("PO-L1", 8, 10, LineDisposition.Accepted, null),
                new VendorInvoiceLine("PO-L2", 2, 10, LineDisposition.Held, "AP-C2: price variance.")
            ], []);
        await erp.CreateInvoiceAsync(inv);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePaymentAsync("USD", RecordSource.P2pAgent, [new PaymentLine("INV-1", 100)]));
        var pay = await erp.CreatePaymentAsync("USD", RecordSource.P2pAgent, [new PaymentLine("INV-1", 80)]);
        Assert.Equal(DocStatus.Posted, pay.Status);
    }

    [Fact]
    public async Task InvoiceAndPaymentApproversMustDiffer()
    {
        var erp = SeededErp.Create();
        erp.SeedPaymentWithInvoices(
            new Payment("PAY-1", DocStatus.Submitted, "USD", RecordSource.P2pAgent, [new PaymentLine("INV-1", 10)]),
            ["INV-1"]);
        await erp.RecordApprovalAsync(new ApprovalDto("Invoice", "INV-1", "finance", "finance", false, DateTimeOffset.UtcNow, true, null, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync(new ApprovalDto("Payment", "PAY-1", "finance", "finance", false, DateTimeOffset.UtcNow, true, null, 10)));
    }
}

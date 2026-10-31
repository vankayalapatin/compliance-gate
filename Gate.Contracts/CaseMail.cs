// Vendor email thread per case. The agent composes and sends queries about
// invoice problems it cannot resolve (recorded here; simulation only, no
// SMTP) and a simulation endpoint appends the vendor reply. Stored as
// <CaseId>.mail.json next to the case file with the same JSON options.
namespace Gate.Contracts;

public sealed record VendorEmail(
    string EmailId,
    string CaseId,
    string VendorId,
    string From,
    string To,
    string Subject,
    string Body,
    DateTimeOffset SentAt,
    string? InReplyTo = null,
    string? InvoiceRef = null);

public sealed record CaseMail(
    string CaseId,
    IReadOnlyList<VendorEmail> Messages);

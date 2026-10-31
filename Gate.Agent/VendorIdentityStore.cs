// File-backed vendor-identity pending store. One file per case named
// <CaseId>.vendor-identity.json holding the extracted invoice details that
// wait on a clerk VendorIdentity confirm. The proposal (Spark suggestion
// plus card) writes one entry; the resolve step reads it to complete the
// intake with the confirmed vendor (approve) or the missing-fields path
// (reject), then removes it. Missing files are normal, never an error.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

public sealed record VendorIdentityPending(
    string DecisionId,
    string CaseId,
    string? SupplierInvoiceNumber,
    string? RawVendorText,
    string ProposedVendorId,
    string? PoNumber,
    IReadOnlyList<SupplierInvoiceLineInput> Lines,
    string? BankAccountRef);

public sealed record VendorIdentityFile(
    string CaseId,
    IReadOnlyList<VendorIdentityPending> Pendings);

public static class VendorIdentityStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string PendingFilePath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".vendor-identity.json");

    public static VendorIdentityFile Load(string cacheDir, string caseId)
    {
        var path = PendingFilePath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return new VendorIdentityFile(caseId, []);
        }

        return JsonSerializer.Deserialize<VendorIdentityFile>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Vendor-identity file {path} did not parse.");
    }

    public static void Save(string cacheDir, VendorIdentityFile file)
    {
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(PendingFilePath(cacheDir, file.CaseId), JsonSerializer.Serialize(file, JsonOpts));
    }

    public static VendorIdentityPending Add(string cacheDir, VendorIdentityPending pending)
    {
        var file = Load(cacheDir, pending.CaseId);
        var existing = file.Pendings.FirstOrDefault(p =>
            string.Equals(p.DecisionId, pending.DecisionId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        Save(cacheDir, file with { Pendings = file.Pendings.Append(pending).ToList() });
        return pending;
    }

    public static void Remove(string cacheDir, string caseId, string decisionId)
    {
        var file = Load(cacheDir, caseId);
        if (file.Pendings.All(p => !string.Equals(p.DecisionId, decisionId, StringComparison.Ordinal)))
        {
            return;
        }

        Save(cacheDir, file with
        {
            Pendings = file.Pendings
                .Where(p => !string.Equals(p.DecisionId, decisionId, StringComparison.Ordinal))
                .ToList()
        });
    }

    public static VendorIdentityPending? Find(string cacheDir, string caseId, string decisionId) =>
        Load(cacheDir, caseId).Pendings.FirstOrDefault(p =>
            string.Equals(p.DecisionId, decisionId, StringComparison.Ordinal));

    public static VendorIdentityPending? FindByInvoiceNumber(
        string cacheDir, string caseId, string? supplierInvoiceNumber) =>
        Load(cacheDir, caseId).Pendings.FirstOrDefault(p =>
            string.Equals(p.SupplierInvoiceNumber, supplierInvoiceNumber, StringComparison.Ordinal));
}

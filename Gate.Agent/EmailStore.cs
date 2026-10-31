// File-backed vendor mail store. One file per case named <CaseId>.mail.json
// containing the CaseMail record (case id plus messages), with the same dir
// parameter and web JSON options as CaseStore. Outbound queries are composed
// by the agent and recorded here (simulation only, no SMTP); vendor replies
// arrive via the simulation endpoint. Single-user demo, last-writer-wins:
// callers re-read before each step.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

public static class EmailStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string MailFilePath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".mail.json");

    /// <summary>Loads the mail thread, or an empty thread when the case has
    /// never emailed. Missing files are normal, never an error.</summary>
    public static CaseMail Load(string cacheDir, string caseId)
    {
        var path = MailFilePath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return new CaseMail(caseId, []);
        }

        return JsonSerializer.Deserialize<CaseMail>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Mail file {path} did not parse as CaseMail.");
    }

    public static void Save(string cacheDir, CaseMail mail)
    {
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(MailFilePath(cacheDir, mail.CaseId), JsonSerializer.Serialize(mail, JsonOpts));
    }

    /// <summary>Appends one message idempotently: a message with the same id
    /// is returned unchanged instead of duplicated.</summary>
    public static VendorEmail Append(string cacheDir, VendorEmail message)
    {
        var mail = Load(cacheDir, message.CaseId);
        var existing = mail.Messages.FirstOrDefault(m =>
            string.Equals(m.EmailId, message.EmailId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        Save(cacheDir, mail with { Messages = mail.Messages.Append(message).ToList() });
        return message;
    }
}

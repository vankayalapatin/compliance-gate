// Spark tool catalog for the P2P loop. Spark sees these tool definitions plus
// a stable projection of the case file and ERP view, and returns tool calls;
// C# validates every call against CasePicker before executing it. Schemas
// stay flat (scalars only) per ModelTool. The prompt presents the tools
// without prescribing the choice: it never names the picked step or its
// reason, so the same state plus different model responses yields different
// executed actions. Prompt JSON excludes every timestamp so cached replays
// reproduce identical prompts and make zero live calls.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

public static class AgentTools
{
    public const string ComputeShortfall = "compute_shortfall";
    public const string RaisePr = "raise_pr";
    public const string ScreenVendors = "screen_vendors";
    public const string AwardSupplier = "award_supplier";
    public const string CreatePo = "create_po";
    public const string SubmitPoApproval = "submit_po_approval";
    public const string CheckPrePay = "check_pre_pay";
    public const string DraftPayment = "draft_payment";
    public const string NotifyCustomer = "notify_customer";
    public const string FinishCase = "finish_case";
    public const string SendVendorEmail = "send_vendor_email";
    public const string GetCase = "get_case";
    public const string GetErpView = "get_erp_view";
    public const string GetMail = "get_mail";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static IReadOnlyList<ModelTool> Catalog { get; } =
    [
        new(ComputeShortfall, "Compute the order shortfall for the case item from BOM, stock, and open POs.",
            [new("case_id", "Case id, e.g. demo-v1-SO-1-BAT-1.", ModelToolParameterType.String)]),
        new(RaisePr, "Raise (or reuse) the purchase requisition for the computed shortfall and route it for approval.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(ScreenVendors, "Screen the candidate vendors against compliance packs. Losers are blocked; winners await award.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(AwardSupplier, "Award the buy to one screened-passing vendor. Name the vendor and say why in your one-line reason, naming each losing vendor and its blocking rule id.",
            [
                new("case_id", "Case id.", ModelToolParameterType.String),
                new("vendor_id", "Passing vendor id to award, e.g. V-B.", ModelToolParameterType.String),
            ]),
        new(CreatePo, "Create the purchase order with the awarded vendor.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(SubmitPoApproval, "Submit a draft PO for human approval.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(CheckPrePay, "Read the pre-pay gate verdicts over holds, quarantine, and open reviews. Read-only: drafts nothing.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(DraftPayment, "Draft the next payment for accepted value and route it for human approval.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(NotifyCustomer, "Notify the customer that materials are secured.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(FinishCase, "Resolve the case once paid in full with no open holds, held lines, or pending reviews.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(SendVendorEmail, "Email the vendor about a held invoice problem the agent cannot resolve (missing fields, unresolvable PO).",
            [
                new("case_id", "Case id.", ModelToolParameterType.String),
                new("invoice_ref", "Held invoice key as vendor/number, e.g. V-B/INV-1.", ModelToolParameterType.String),
            ]),
        new(GetCase, "Read the full case file: stage, document refs, timeline, and decisions.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(GetErpView, "Re-read the fresh ERP view behind the pick: document states, payable vs paid money, open blocks.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
        new(GetMail, "Read the vendor email thread for the case.",
            [new("case_id", "Case id.", ModelToolParameterType.String)]),
    ];

    public static bool IsReadTool(string name) =>
        string.Equals(name, GetCase, StringComparison.Ordinal)
        || string.Equals(name, GetErpView, StringComparison.Ordinal)
        || string.Equals(name, GetMail, StringComparison.Ordinal)
        || string.Equals(name, CheckPrePay, StringComparison.Ordinal);

    /// <summary>Maps a runnable picker step to its action tool. Waiting steps
    /// have no tool: they end the advance instead of executing.</summary>
    /// <summary>Plain verb for a tool, for refusal timeline lines. Unknown
    /// tools (including invented names) read as acting, never echoed.</summary>
    public static string ToPlainVerb(string toolName) => toolName switch
    {
        ComputeShortfall => "check the shortfall",
        RaisePr => "raise the request",
        ScreenVendors => "screen vendors",
        AwardSupplier => "award the supplier",
        CreatePo => "place the order",
        SubmitPoApproval => "submit the approval",
        DraftPayment => "draft the payment",
        NotifyCustomer => "notify the customer",
        FinishCase => "close the case",
        SendVendorEmail => "email the supplier",
        _ => "act",
    };

    /// <summary>True for a vendor query target with an invoice number
    /// (vendor/number; the vendor half may be blank because the query
    /// resolves the vendor from the case PO). A blank number names no
    /// invoice: it is refused, never emailed or carded.</summary>
    public static bool IsEmailableInvoiceRef(string? invoiceRef)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef))
        {
            return false;
        }

        var parts = invoiceRef.Split('/', 2);
        return parts.Length == 2
            && !string.IsNullOrWhiteSpace(parts[1]);
    }

    public static string? ToToolName(CaseNextStep step) => step switch
    {
        CaseNextStep.ComputeShortfall => ComputeShortfall,
        CaseNextStep.RaisePr => RaisePr,
        CaseNextStep.ScreenVendors => ScreenVendors,
        CaseNextStep.AwardSupplier => AwardSupplier,
        CaseNextStep.CreatePo => CreatePo,
        CaseNextStep.SubmitPoApproval => SubmitPoApproval,
        CaseNextStep.DraftPayment => DraftPayment,
        CaseNextStep.NotifyCustomer => NotifyCustomer,
        CaseNextStep.QueryVendor => SendVendorEmail,
        CaseNextStep.FinishCase => FinishCase,
        _ => null,
    };

    public static CaseNextStep? ToStep(string toolName) => toolName switch
    {
        ComputeShortfall => CaseNextStep.ComputeShortfall,
        RaisePr => CaseNextStep.RaisePr,
        ScreenVendors => CaseNextStep.ScreenVendors,
        AwardSupplier => CaseNextStep.AwardSupplier,
        CreatePo => CaseNextStep.CreatePo,
        SubmitPoApproval => CaseNextStep.SubmitPoApproval,
        DraftPayment => CaseNextStep.DraftPayment,
        NotifyCustomer => CaseNextStep.NotifyCustomer,
        FinishCase => CaseNextStep.FinishCase,
        _ => null,
    };

    public static string SystemPrompt =>
        "You are the P2P buying agent. Read the case state and ERP view, then " +
        "drive the case forward by calling tools. Prefer exactly one action " +
        "tool per turn; you may call read tools first when you need fresher " +
        "detail. Only call send_vendor_email when a held invoice problem has " +
        "no other path forward. For a supplier award, name a vendor that " +
        "passed screening and say why in your one-line reason, naming each " +
        "other candidate and its blocking rule id from the screening events. Never invent " +
        "ids, quantities, prices, or dates: every value you use must come " +
        "from the case state, the ERP view, or a tool result. Illegal calls " +
        "are blocked and the refusal is fed back to you; adjust and continue.";

    /// <summary>Builds one turn prompt: stable case JSON plus the ERP view.
    /// No timestamps anywhere, so replays reproduce the prompt byte for byte.
    /// Names no step and no choice; the request carries only the step's own
    /// tools, so the prompt never needs to steer among them.</summary>
    public static string BuildTurnPrompt(CaseFile file, CaseErpView view)
    {
        var projection = new
        {
            Case = CaseProjection(file.Case),
            ErpView = ErpViewProjection(view),
            Decisions = file.Decisions.Select(DecisionProjection).ToList(),
            RecentEvents = file.Events.TakeLast(30).Select(EventProjection).ToList(),
        };
        return "Case state and ERP view (JSON):\n"
            + JsonSerializer.Serialize(projection, JsonOpts)
            + "\nCall the tool you judge best. Reply with tool calls only, plus a one-line reason.";
    }

    /// <summary>Stable case JSON for the get_case tool: full timeline, no
    /// timestamps, so tool results replay byte for byte.</summary>
    public static string CaseJson(CaseFile file) =>
        JsonSerializer.Serialize(new
        {
            Case = CaseProjection(file.Case),
            Decisions = file.Decisions.Select(DecisionProjection).ToList(),
            Events = file.Events.Select(EventProjection).ToList(),
        }, JsonOpts);

    /// <summary>Stable ERP-view JSON for the get_erp_view tool.</summary>
    public static string ErpViewJson(CaseErpView view) =>
        JsonSerializer.Serialize(ErpViewProjection(view), JsonOpts);

    /// <summary>Stable pre-pay gate JSON for the check_pre_pay tool: counts
    /// plus verdicts, no timestamps, so reads replay byte for byte.</summary>
    public static string PrePayJson(PrePayState state, IReadOnlyList<Verdict> verdicts) =>
        JsonSerializer.Serialize(new
        {
            OpenHolds = state.OpenHolds.Count,
            state.QuarantinedLotRefs,
            OpenReviews = state.OpenReviews.Count,
            Verdicts = verdicts.Select(v => new
            {
                v.RuleId,
                Kind = v.Kind.ToString(),
                v.Reason,
            }).ToList(),
        }, JsonOpts);

    /// <summary>Stable mail-thread JSON for the get_mail tool: no sent times.</summary>
    public static string MailJson(CaseMail mail) =>
        JsonSerializer.Serialize(new
        {
            mail.CaseId,
            Messages = mail.Messages.Select(m => new
            {
                m.EmailId,
                m.VendorId,
                m.From,
                m.To,
                m.Subject,
                m.Body,
                m.InReplyTo,
                m.InvoiceRef,
            }).ToList(),
        }, JsonOpts);

    private static object CaseProjection(Case current) => new
    {
        current.CaseId,
        current.SoNumber,
        current.ItemId,
        current.ScenarioName,
        Stage = current.Stage.ToString(),
        current.PrNumber,
        current.PoNumber,
        current.AsnNumber,
        current.ReceiptNumber,
        Invoices = current.Invoices.Select(k => $"{k.VendorId}/{k.SupplierInvoiceNumber}").ToList(),
        current.PaymentNumbers,
        current.BlockedByRuleId,
        current.BlockedReason,
    };

    private static object ErpViewProjection(CaseErpView view) => new
    {
        view.SoExists,
        PrStatus = view.PrStatus?.ToString(),
        PoApproval = view.PoApproval?.ToString(),
        view.ConfirmationExists,
        view.AsnExists,
        view.ReceiptExists,
        view.MatchedInvoices,
        view.AcceptedTotal,
        view.PaidTotal,
        view.HasHeldLines,
        view.OpenHoldCount,
        view.CustomerNotified,
    };

    private static object DecisionProjection(Decision d) => new
    {
        d.DecisionId,
        Kind = d.Kind.ToString(),
        Status = d.Status.ToString(),
        d.Title,
        d.RequestedTo,
        d.DecidedBy,
        d.DocumentType,
        d.DocumentNumber,
        d.Amount,
        d.Currency,
        d.RuleId,
        d.Reason,
        d.EvidenceRefs,
        d.Comment,
    };

    private static object EventProjection(CaseEvent e) => new
    {
        Kind = e.Kind.ToString(),
        e.Actor,
        e.Summary,
        e.RuleId,
        e.DocumentType,
        e.DocumentNumber,
        e.EvidenceRef,
        e.Amount,
        e.Currency,
    };

    /// <summary>Reads one required string argument. Returns null when the
    /// arguments are not an object or the value is missing or blank.</summary>
    public static string? ReadStringArg(string argumentsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

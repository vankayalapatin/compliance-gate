namespace Gate.Erp;

/// <summary>Non-person approval actors. Policy acts are recorded under an
/// explicit policy name, never as a person and never as "system".</summary>
public static class ErpApprovalActors
{
    public const string AutoApprovePolicy = "auto-approve policy";
}

namespace Fixia.Payments.Domain.Enums;

/// <summary>Acciones del ADMIN que ms-payments registra (GC-535, contrato <c>ms-users/docs/contracts/admin.md</c> §5).</summary>
public enum AdminAction
{
    PAYMENT_LIST = 1,
    PAYMENT_VIEW = 2,
    PAYMENT_REFUND = 3,
    AUDIT_LIST = 4
}

/// <summary>Resultado de una acción del ADMIN (contrato §5).</summary>
public enum AdminAuditOutcome
{
    OK = 1,
    DENIED = 2,
    CONFLICT = 3,
    NOT_FOUND = 4
}

namespace Ros.Domain.Work

open System

type ReconciliationReceiptStatus =
    | Applied
    | Rejected
    | AlreadyApplied

type ReconciliationReceipt = {
    TransactionId: string
    WorkItem: string
    Branch: string
    HeadCommit: string
    EnvelopeHash: string
    Status: ReconciliationReceiptStatus
    FindingCodes: string list
    ReconciledAt: DateTimeOffset
}

module ReconciliationReceipt =
    let path transactionId = $".praxis/reconciled/{transactionId}.json"

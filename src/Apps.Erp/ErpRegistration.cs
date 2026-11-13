using Apps.Erp.Hooks;
using Apps.Erp.Reports;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp;

public static class ErpRegistration
{
    /// <summary>
    /// Daftarkan semua hook, form action, handler event, job, dan laporan ERP.
    /// Panggil di Program.cs host: builder.Services.AddErpHooks();
    /// Butuh patch framework patches/0001-ledger-get-balances.patch (ILedgerReader.GetBalancesAsync).
    /// </summary>
    public static IServiceCollection AddErpHooks(this IServiceCollection services)
    {
        // CORE
        services.AddDocHook<CurrencyExchangeRate, CurrencyExchangeRateRulesHook>();
        services.AddDocHook<Address, PartyPrimaryHook>();
        services.AddDocHook<Contact, PartyPrimaryHook>();

        // ACCOUNTING
        services.AddDocHook<Account, AccountRulesHook>();
        services.AddDocHook<FiscalYear, FiscalYearRulesHook>();
        services.AddDocHook<AccountingPeriod, AccountingPeriodRulesHook>();
        services.AddDocHook<JournalEntry, JournalEntryHook>();
        services.AddDocHook<PeriodClosingVoucher, PeriodClosingVoucherHook>();
        services.AddDocHook<Budget, BudgetHook>();

        // FINANCE: AR/AP/kas
        services.AddDocHook<SalesInvoice, SalesInvoiceHook>();
        services.AddDocHook<SalesCreditNote, SalesCreditNoteHook>();
        services.AddDocHook<PurchaseInvoice, PurchaseInvoicePoHook>(); // sebelum PurchaseInvoiceHook: menyalin baris PO dulu
        services.AddDocHook<PurchaseInvoice, PurchaseStockHook>();     // lalu akun baris stok (persediaan / GRNI)
        services.AddDocHook<PurchaseInvoice, PurchaseInvoiceHook>();
        services.AddDocHook<PurchaseRequest, PurchaseRequestHook>();
        services.AddFormAction<PurchaseRequest, PurchaseRequestMakeOrderAction>();
        services.AddDocHook<PurchaseOrder, PurchaseOrderHook>();
        services.AddFormAction<PurchaseOrder, PurchaseOrderReceiveAction>();
        services.AddDocHook<GoodsReceipt, GoodsReceiptHook>();
        services.AddDocHook<GoodsReceipt, GoodsReceiptStockHook>();

        // PERSEDIAAN
        services.AddDocHook<SalesInvoice, SalesStockHook>();
        services.AddDocHook<SalesCreditNote, SalesStockHook>();
        services.AddDocHook<PurchaseDebitNote, PurchaseStockHook>();
        services.AddDocHook<StockEntry, StockEntryHook>();
        services.AddDocHook<StockReconciliation, StockReconciliationHook>();
        services.AddFormAction<StockReconciliation, StockReconciliationFetchAction>();
        services.AddDocHook<PurchaseDebitNote, PurchaseDebitNoteHook>();
        services.AddDocHook<PaymentEntry, PaymentEntryHook>();
        services.AddDocHook<BankStatement, BankStatementHook>();
        services.AddDocHook<BankReconciliation, BankReconciliationHook>();
        services.AddDocHook<PaymentReconciliation, PaymentReconciliationHook>();
        services.AddDocHook<PaymentRun, PaymentRunHook>();
        services.AddFormAction<PaymentRun, PaymentRunGetItemsAction>();
        // FINANCE: pajak
        services.AddDocHook<TaxInvoice, TaxInvoiceHook>();
        services.AddDocHook<WithholdingCertificate, WithholdingCertificateHook>();
        // FINANCE: aset
        services.AddDocHook<Asset, AssetHook>();
        services.AddDocHook<DepreciationRun, DepreciationRunHook>();
        services.AddDocHook<AssetDisposal, AssetDisposalHook>();
        // FINANCE: karyawan
        services.AddDocHook<ExpenseClaim, ExpenseClaimHook>();
        services.AddDocHook<EmployeeAdvance, EmployeeAdvanceHook>();

        // HR
        services.AddDocHook<Employee, EmployeeHook>();
        services.AddDocHook<Position, PositionHook>();
        services.AddDocHook<OrganizationUnit, OrganizationUnitHook>();
        services.AddDocHook<JobChange, JobChangeHook>();
        services.AddDocHook<EmployeeSeparation, EmployeeSeparationHook>();
        services.AddDocHook<EmploymentContract, EmploymentContractHook>();
        services.AddDocHook<LeaveAllocation, LeaveAllocationHook>();
        services.AddDocHook<LeaveApplication, LeaveApplicationHook>();

        services.AddDocHook<Attendance, AttendanceHook>();
        services.AddDocHook<OvertimeRequest, OvertimeRequestHook>();

        // RECRUITMENT
        services.AddDocHook<JobRequisition, JobRequisitionHook>();
        services.AddDocHook<JobApplicant, JobApplicantHook>();
        services.AddDocHook<JobOffer, JobOfferHook>();
        services.AddFormAction<JobOffer, JobOfferCreateEmployeeAction>();

        // KINERJA & PELATIHAN
        services.AddDocHook<Appraisal, AppraisalHook>();
        services.AddDocHook<TrainingEvent, TrainingEventHook>();

        // PAYROLL
        services.AddDocHook<PayrollSettings, PayrollSettingsHook>();
        services.AddDocHook<SalaryAssignment, SalaryAssignmentHook>();
        services.AddDocHook<Payslip, PayslipHook>();
        services.AddDocHook<PayrollRun, PayrollRunHook>();
        services.AddFormAction<PayrollRun, PayrollRunCreatePayslipsAction>();
        services.AddFormAction<PayrollRun, PayrollRunSubmitPayslipsAction>();

        // CRM
        services.AddDocHook<Lead, LeadHook>();
        services.AddDocHook<Opportunity, OpportunityHook>();
        services.AddDocHook<Quotation, QuotationHook>();
        services.AddFormAction<Lead, LeadConvertAction>();
        services.AddFormAction<Quotation, QuotationMakeInvoiceAction>();
        services.AddFormAction<Quotation, QuotationMakeSalesOrderAction>();
        services.AddDocHook<Opportunity, OpportunityTerritoryHook>();
        services.AddDocHook<Campaign, CampaignHook>();
        services.AddDocHook<SalesOrder, SalesOrderHook>();
        services.AddFormAction<SalesOrder, SalesOrderMakeInvoiceAction>();
        services.AddDocHook<SalesInvoice, SalesInvoiceSalesRepHook>();
        services.AddDocHook<CustomerContract, CustomerContractHook>();
        services.AddDocHook<CommissionStatement, CommissionStatementHook>();

        // Efek lintas dokumen & job harian
        services.AddEventHandler<ErpEventHandler>();
        services.AddScheduledJob<JournalAutoReverseJob>();
        services.AddScheduledJob<HrEffectiveDateJob>();
        services.AddScheduledJob<LeaveAllocationJob>();
        services.AddScheduledJob<ContractExpiryReminderJob>();
        services.AddScheduledJob<CrmDailyJob>();

        // Laporan (menu Reports; izin 'report' mengikuti DocType sumber tiap laporan)
        services.AddReport<TrialBalanceReport>();
        services.AddReport<ProfitAndLossReport>();
        services.AddReport<BalanceSheetReport>();
        services.AddReport<ReceivableAgingReport>();
        services.AddReport<PayableAgingReport>();
        services.AddReport<VatRegisterReport>();
        services.AddReport<WithholdingRegisterReport>();
        services.AddReport<LeaveBalanceReport>();
        services.AddReport<SalesPipelineReport>();
        services.AddReport<PayrollRegisterReport>();
        services.AddReport<Pph21AnnualReport>();
        services.AddReport<BankTransferListReport>();
        services.AddReport<BudgetVsActualReport>();
        services.AddReport<StockBalanceReport>();
        services.AddReport<StockLedgerReport>();
        return services;
    }
}

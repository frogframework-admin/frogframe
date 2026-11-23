using System.Runtime.CompilerServices;
using Apps.Erp.Hooks;
using Apps.Erp.Reports;
using Apps.Erp.Accounting;
using Framework.Accounting;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Web;
using Framework.Web.Home;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

[assembly: InternalsVisibleTo("Framework.Tests")]

namespace Apps.Erp;

public static class ErpRegistration
{
    /// <summary>
    /// ESS: mobile attendance punches are persisted to the lean <c>ess_attendance_punches</c> table (PostgreSQL) or memory,
    /// and a 5-minute background job materializes daily Attendance documents. Endpoints are mapped via <see cref="Ess.EssEndpoints.MapEssApi"/>.
    /// </summary>
    public static IServiceCollection AddErpEss(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton<Ess.IEssPunchStore>(sp => sp.GetService<NpgsqlDataSource>() is { } db
            ? new Ess.PostgresEssPunchStore(db)
            : new Ess.InMemoryEssPunchStore());
        services.TryAddSingleton<Ess.EssAttendanceService>();
        services.TryAddSingleton<Ess.EssRequestService>();
        services.AddDocHook<Employee, Ess.EssCacheInvalidationHook>();
        services.AddDocHook<Ess.Location, Ess.EssCacheInvalidationHook>();
        services.AddDocHook<Ess.ShiftType, Ess.EssCacheInvalidationHook>();
        services.AddDocHook<Ess.HRSettings, Ess.EssCacheInvalidationHook>();
        services.AddScheduledJob<Ess.EssAttendanceAggregationJob>();
        return services;
    }

    /// <summary>
    /// Careers portal: Job Posting (what applicants see of a requisition), the public API mapped by
    /// <see cref="Careers.CareersEndpoints.MapCareersApi"/>, its per-IP rate limit policy and the optional captcha check
    /// (<c>Careers:Captcha:VerifyUrl</c>, <c>Careers:Captcha:Secret</c>). See docs/guides/careers-portal-api.md.
    /// </summary>
    public static IServiceCollection AddErpCareers(this IServiceCollection services)
    {
        services.AddDocHook<Careers.JobPosting, Careers.JobPostingHook>();
        services.AddHttpClient(Careers.SiteVerifyHumanCheck.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.TryAddSingleton<Careers.ICareersHumanCheck, Careers.SiteVerifyHumanCheck>();
        services.TryAddSingleton<Careers.CareersService>();
        services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o =>
            o.AddPolicy(Careers.CareersEndpoints.RateLimitPolicy, Careers.CareersEndpoints.Partition));
        return services;
    }

    /// <summary>
    /// Home page contributions: the employee's day and requests ("Personal" slot, only for accounts linked to an
    /// employee), company information (side column), the first-run checklist for administrators (top) and quick
    /// actions, each shown only to users allowed to create that DocType. See docs/guides/home-page.md.
    /// </summary>
    public static IServiceCollection AddErpHome(this IServiceCollection services)
    {
        services.TryAddSingleton<Home.ErpHomeService>();
        static bool IsEmployee(Framework.Security.UserContext u) => Home.ErpHomeService.EmployeeOf(u) is not null;
        services.AddHomePanel<Components.Home.SetupChecklistPanel>("erp-setup", Framework.Web.Home.HomeSlot.Top, 10, u => u.IsAdmin);
        services.AddHomePanel<Components.Home.EmployeeTodayPanel>("erp-today", Framework.Web.Home.HomeSlot.Personal, 10, IsEmployee);
        services.AddHomePanel<Components.Home.MyRequestsPanel>("erp-my-requests", Framework.Web.Home.HomeSlot.Personal, 20, IsEmployee);
        services.AddHomePanel<Components.Home.CompanyInfoPanel>("erp-company-info", Framework.Web.Home.HomeSlot.Side, 50);

        // Order: self-service first (most users), then the transactions of each department.
        services.AddHomeQuickAction("LeaveApplication", "Apply for leave", "calendar-off", 10);
        services.AddHomeQuickAction("OvertimeRequest", "Request overtime", "clock-plus", 11);
        services.AddHomeQuickAction("ExpenseClaim", "Expense claim", "receipt", 12);
        services.AddHomeQuickAction("ShiftSwapRequest", "Swap shift", "repeat", 13);
        services.AddHomeQuickAction("PurchaseRequest", "Purchase request", "shopping-cart", 30);
        services.AddHomeQuickAction("SalesInvoice", "Sales invoice", "file-text", 40);
        services.AddHomeQuickAction("PaymentEntry", "Payment entry", "banknote", 41);
        services.AddHomeQuickAction("JournalEntry", "Journal entry", "book-open", 42);
        services.AddHomeQuickAction("Lead", "Lead", "user-plus", 50);
        services.AddHomeQuickAction("Quotation", "Quotation", "file-pen-line", 51);
        return services;
    }

    public static IServiceCollection AddErpAccounting(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IInMemoryUnitOfWorkExtensionHandler, InMemoryLedgerExtensionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostgresUnitOfWorkExtensionHandler, PostgresLedgerExtensionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostgresMigrationSource>(new EmbeddedPostgresMigrationSource(typeof(ErpRegistration).Assembly)));
        services.TryAddSingleton<ILedgerReader>(sp => sp.GetService<NpgsqlDataSource>() is { } db
            ? new PostgresLedgerReader(db)
            : new InMemoryLedgerReader(sp.GetRequiredService<IDocumentStore>()));
        return services;
    }

    /// <summary>
    /// Daftarkan semua hook, form action, handler event, job, dan laporan ERP.
    /// Panggil di Program.cs host: builder.Services.AddErpHooks();
    /// </summary>
    public static IServiceCollection AddErpHooks(this IServiceCollection services)
    {
        // Granular ERP roles and the former broad roles as groups (docs/guides/roles.md).
        ErpRoles.Register();
        services.AddErpAccounting();
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

        // HR & ORGANIZATION MANAGEMENT
        services.AddDocHook<Employee, EmployeeHook>();
        services.AddDocHook<Position, PositionHook>();
        services.AddDocHook<OrganizationUnitType, OrganizationUnitTypeHook>();
        services.AddFormAction<OrganizationUnitType, OrganizationUnitTypeSyncPositionsAction>();
        services.AddDocHook<OrganizationUnit, OrganizationUnitHook>();
        services.AddFormAction<OrganizationUnit, OrganizationUnitActivateAction>();
        services.AddDocHook<EmployeeAssignment, EmployeeAssignmentHook>();
        services.AddDocHook<JobChange, JobChangeHook>();
        services.AddDocHook<EmployeeSeparation, EmployeeSeparationHook>();
        services.AddDocHook<OnboardingList, OnboardingListHook>();
        services.AddDocHook<OffboardingList, OffboardingListHook>();
        services.AddDocHook<EmploymentContract, EmploymentContractHook>();
        services.AddDocHook<LeaveAllocation, LeaveAllocationHook>();
        services.AddDocHook<LeaveApplication, LeaveApplicationHook>();

        // WORKFORCE SCHEDULING & ATTENDANCE (WSM)
        services.AddDocHook<ShiftGroup, ShiftGroupHook>();
        services.AddDocHook<ShiftPattern, ShiftPatternHook>();
        services.AddDocHook<ShiftAssignment, ShiftAssignmentHook>();
        services.AddDocHook<EmployeeDailySchedule, EmployeeDailyScheduleHook>();
        services.AddDocHook<ScheduleGeneration, ScheduleGenerationHook>();
        services.AddFormAction<ScheduleGeneration, ScheduleGenerationRunAction>();
        services.AddDocHook<ShiftPreference, ShiftPreferenceHook>();
        services.AddDocHook<ScheduleOptimization, ScheduleOptimizationHook>();
        services.AddFormAction<ScheduleOptimization, ScheduleOptimizationRunAction>();
        services.AddFormAction<ScheduleOptimization, ScheduleOptimizationApplyAction>();
        services.AddFormAction<ScheduleOptimization, ScheduleOptimizationDiscardAction>();
        services.AddDocHook<ShiftOverride, ShiftOverrideHook>();
        services.AddDocHook<ShiftSwapRequest, ShiftSwapRequestHook>();
        services.AddDocHook<ShiftRequirement, ShiftRequirementHook>();
        services.AddDocHook<Attendance, AttendanceHook>();
        services.AddDocHook<OvertimeRequest, OvertimeRequestHook>();
        // ESS requests (docs/guides/ess-mobile-api.md): face registration, attendance correction, biodata change.
        services.AddDocHook<FaceRegistration, FaceRegistrationHook>();
        services.AddDocHook<AttendanceCorrection, AttendanceCorrectionHook>();
        services.AddDocHook<BiodataChangeRequest, BiodataChangeRequestHook>();
        // Approval review panels on the desk forms of the self-service requests.
        services.TryAddSingleton<Ess.EssReviewService>();
        services.AddFormPanel<Components.Review.FaceRegistrationReview>("FaceRegistration");
        services.AddFormPanel<Components.Review.AttendanceCorrectionReview>("AttendanceCorrection");
        services.AddFormPanel<Components.Review.BiodataChangeReview>("BiodataChangeRequest");
        services.AddDocHook<LeaveApplication, EssApprovalNotifier>();
        services.AddDocHook<OvertimeRequest, EssApprovalNotifier>();
        services.AddDocHook<AttendanceCorrection, EssApprovalNotifier>();
        services.AddDocHook<FaceRegistration, EssApprovalNotifier>();
        services.AddDocHook<BiodataChangeRequest, EssApprovalNotifier>();

        // RECRUITMENT
        services.AddDocHook<JobRequisition, JobRequisitionHook>();
        services.AddDocHook<JobApplicant, JobApplicantHook>();
        services.AddDocHook<JobOffer, JobOfferHook>();
        services.AddFormAction<JobOffer, JobOfferCreateEmployeeAction>();

        // Careers portal: Job Posting + public API /api/careers/v1/{tenant} (docs/guides/careers-portal-api.md)
        services.AddErpCareers();

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
        services.AddScheduledJob<AttendanceDailyJob>(); // daily attendance codes (ABS, DO, NCIN/NCOUT…), docs/guides/attendance-codes.md
        services.AddScheduledJob<HrEffectiveDateJob>();
        services.AddScheduledJob<RosterRollingHorizonJob>();
        services.AddScheduledJob<ApprovalDelegationRefreshJob>();
        services.AddScheduledJob<LeaveAllocationJob>();
        services.AddScheduledJob<ContractExpiryReminderJob>();
        services.AddScheduledJob<CrmDailyJob>();

        // ESS mobile (absen GPS, cuti, slip gaji, persetujuan): docs/guides/ess-mobile-api.md
        services.AddErpEss();

        // Home page (/): employee panels, company info, the admin setup checklist and "Create new" shortcuts.
        services.AddErpHome();

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

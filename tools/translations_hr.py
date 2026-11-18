# HR translations (HR-001 to HR-133)
HR_TRANSLATIONS = {
    "HR-001": {
        "name": "Total Active Headcount",
        "definition": "Number of active employees at the end of the period",
        "formula": "COUNT(Employee WHERE status='Active')"
    },
    "HR-002": {
        "name": "Headcount vs Filled Positions",
        "definition": "Ratio of filled positions to total active positions (Vacant + Filled)",
        "formula": "COUNT(Position status='Filled') / COUNT(Position status IN ('Filled','Vacant')) x100"
    },
    "HR-003": {
        "name": "Vacancy Rate",
        "definition": "Percentage of vacant positions",
        "formula": "COUNT(Position status='Vacant') / COUNT(Position status IN ('Filled','Vacant')) x100"
    },
    "HR-004": {
        "name": "Frozen Position Count",
        "definition": "Number of frozen positions",
        "formula": "COUNT(Position status='Frozen')"
    },
    "HR-005": {
        "name": "Span of Control",
        "definition": "Average number of direct reports per supervisor",
        "formula": "COUNT(Active employees) / COUNT(DISTINCT reports_to)"
    },
    "HR-006": {
        "name": "Manager Ratio",
        "definition": "Percentage of managerial positions out of total positions",
        "formula": "COUNT(Position is_manager_position=1) / COUNT(Position) x100"
    },
    "HR-007": {
        "name": "Gender Composition",
        "definition": "Percentage of female employees in active headcount",
        "formula": "COUNT(Employee gender='Female' AND status='Active') / COUNT(Active employees) x100"
    },
    "HR-008": {
        "name": "Average Employee Age",
        "definition": "Average age of active employees",
        "formula": "AVG(DATEDIFF(year, date_of_birth, CURRENT_DATE)) WHERE status='Active'"
    },
    "HR-009": {
        "name": "Average Tenure",
        "definition": "Average length of service of active employees",
        "formula": "AVG(DATEDIFF(month, original_hire_date OR hire_date, CURRENT_DATE))/12"
    },
    "HR-010": {
        "name": "Employment Status Composition",
        "definition": "Percentage of employees by employment type (Permanent/Fixed-term Contract/etc.)",
        "formula": "COUNT(Employee per employment_type) / COUNT(Active employees) x100"
    },
    "HR-011": {
        "name": "Employees on Probation",
        "definition": "Number of active employees whose probation end date has not passed",
        "formula": "COUNT(Employee WHERE probation_end_date >= CURRENT_DATE AND status='Active')"
    },
    "HR-012": {
        "name": "Probation Overdue",
        "definition": "Employees whose probation end date passed without a completed probation job change",
        "formula": "COUNT(Employee probation_end_date < CURRENT_DATE AND no JobChange change_reason='Probation completed')"
    },
    "HR-013": {
        "name": "Employees Approaching Retirement",
        "definition": "Active employees aged >= (retirement_age − 1)",
        "formula": "COUNT(Employee age >= HRSettings.retirement_age − 1)"
    },
    "HR-014": {
        "name": "Employee Data Completeness",
        "definition": "Percentage of active employees with national ID, tax ID, social security, and bank account filled",
        "formula": "COUNT(Employee nik,npwp,bpjs_health_no,bpjs_employment_no filled AND bank_accounts >= 1) / COUNT(Active) x100"
    },
    "HR-015": {
        "name": "Expired Identity Documents",
        "definition": "Employee identity documents expired or expiring within 30 days",
        "formula": "COUNT(EmployeeIdentityDocument expiry_date <= CURRENT_DATE + 30)"
    },
    "HR-016": {
        "name": "Emergency Contact Completeness",
        "definition": "Percentage of active employees with at least 1 emergency contact",
        "formula": "COUNT(Employee with emergency_contacts >= 1) / COUNT(Active) x100"
    },
    "HR-017": {
        "name": "Dependents per Employee",
        "definition": "Average number of dependents per employee (for insurance and benefit planning)",
        "formula": "COUNT(EmployeeDependent) / COUNT(Active employees)"
    },
    "HR-018": {
        "name": "Education Profile",
        "definition": "Percentage of employees with Bachelor's degree or higher",
        "formula": "COUNT(Employee with EmployeeEducation level IN ('Bachelor','Master','Doctorate')) / COUNT(Active) x100"
    },
    "HR-019": {
        "name": "Revenue per Employee",
        "definition": "Revenue per active employee",
        "formula": "SUM(SalesInvoice.base_grand_total − vat_amount, docstatus=1) / AVG headcount"
    },
    "HR-020": {
        "name": "Labour Cost to Revenue",
        "definition": "Labor costs compared to total revenue",
        "formula": "SUM(Payslip.gross_earnings + employer_contributions) / Revenue x100"
    },
    "HR-021": {
        "name": "Organization Units Without Manager",
        "definition": "Active organization units with an empty manager field",
        "formula": "COUNT(OrganizationUnit is_active=1 AND manager IS NULL)"
    },
    "HR-022": {
        "name": "Organization Units Without Cost Center",
        "definition": "Active organization units not yet mapped to a cost center",
        "formula": "COUNT(OrganizationUnit is_active=1 AND cost_center IS NULL)"
    },
    "HR-023": {
        "name": "Organization Structure Depth",
        "definition": "Number of hierarchy levels from top to bottom organization unit",
        "formula": "MAX depth(parent_org_unit)"
    },
    "HR-024": {
        "name": "Positions With Dotted Line",
        "definition": "Percentage of positions with matrix reporting lines",
        "formula": "COUNT(Position with dotted_lines >= 1) / COUNT(Position) x100"
    },
    "HR-025": {
        "name": "Employees Outside Grade Pay Range",
        "definition": "Employees with basic salary below minimum or above maximum of GradePayRange",
        "formula": "COUNT(SalaryAssignment base < min_salary OR > max_salary)"
    },
    "HR-026": {
        "name": "Compa-Ratio",
        "definition": "Employee salary compared to grade midpoint",
        "formula": "AVG(basic_salary / GradePayRange.mid_salary)"
    },
    "HR-027": {
        "name": "Job Profiles Without Default Grade",
        "definition": "Active job profiles without a default grade configured",
        "formula": "COUNT(JobProfile is_active=1 AND default_grade IS NULL)"
    },
    "HR-028": {
        "name": "Promotion Rate",
        "definition": "Percentage of employees promoted within the period",
        "formula": "COUNT(JobChange change_reason='Promotion', docstatus=1) / AVG headcount x100"
    },
    "HR-029": {
        "name": "Internal Mobility Rate",
        "definition": "Transfers and rotations compared to headcount",
        "formula": "COUNT(JobChange change_reason IN ('Transfer','Rotation')) / AVG headcount x100"
    },
    "HR-030": {
        "name": "Demotion Count",
        "definition": "Number of demotions",
        "formula": "COUNT(JobChange change_reason='Demotion')"
    },
    "HR-031": {
        "name": "Employment Status Conversion",
        "definition": "Contract employees converted to permanent status",
        "formula": "COUNT(JobChange change_reason='Employment status change' AND new_employment_type='Permanent (PKWTT)')"
    },
    "HR-032": {
        "name": "Average Time Since Last Job Change",
        "definition": "Average months since last job change date",
        "formula": "AVG(DATEDIFF(month, last_job_change_date, CURRENT_DATE))"
    },
    "HR-033": {
        "name": "Employees Affected by Reorganization",
        "definition": "Number of employees affected by reorganization",
        "formula": "COUNT(JobChange change_reason='Reorganization')"
    },
    "HR-034": {
        "name": "Expiring Contracts",
        "definition": "Active contracts ending within X days (X = HRSettings.contract_expiry_reminder_days)",
        "formula": "COUNT(EmploymentContract status='Active' AND end_date <= CURRENT_DATE + X)"
    },
    "HR-035": {
        "name": "Expired Contracts Without Follow-up",
        "definition": "Expired contracts where the employee remains active without a successor contract",
        "formula": "COUNT(EmploymentContract status='Expired' AND Employee.status='Active' AND no renewal_of)"
    },
    "HR-036": {
        "name": "Contract Renewal Rate",
        "definition": "Contracts renewed compared to contracts ending",
        "formula": "COUNT(status='Renewed') / COUNT(status IN ('Renewed','Expired','Terminated')) x100"
    },
    "HR-037": {
        "name": "Contracts Without Document Attachment",
        "definition": "Active employment contracts missing an attached contract document",
        "formula": "COUNT(EmploymentContract status='Active' AND document IS NULL)"
    },
    "HR-038": {
        "name": "Open Requisitions",
        "definition": "Number of job requisitions with Open status",
        "formula": "COUNT(JobRequisition requisition_status='Open')"
    },
    "HR-039": {
        "name": "Open Openings",
        "definition": "Total unfilled job openings",
        "formula": "SUM(openings − hired_count) WHERE requisition_status='Open'"
    },
    "HR-040": {
        "name": "Requisition Fill Rate",
        "definition": "Filled openings compared to total openings",
        "formula": "SUM(hired_count) / SUM(openings) x100"
    },
    "HR-041": {
        "name": "Time to Fill",
        "definition": "Days from job requisition creation to Filled status",
        "formula": "AVG(Filled date − creation) per JobRequisition"
    },
    "HR-042": {
        "name": "Time to Hire",
        "definition": "Days from applicant creation to Hired status",
        "formula": "AVG(Hired date − JobApplicant.creation)"
    },
    "HR-043": {
        "name": "Time to Start",
        "definition": "Days from offer date to proposed start date",
        "formula": "AVG(proposed_start_date − offer_date)"
    },
    "HR-044": {
        "name": "Applicants per Opening",
        "definition": "Average number of applicants per opening",
        "formula": "SUM(applicant_count) / SUM(openings)"
    },
    "HR-045": {
        "name": "Screening Pass Rate",
        "definition": "Applicants advancing to interview or later stages",
        "formula": "COUNT(JobApplicant status IN ('Interview','Offer','Hired')) / COUNT(JobApplicant) x100"
    },
    "HR-046": {
        "name": "Interview Pass Rate",
        "definition": "Interviews resulting in Pass",
        "formula": "COUNT(Interview result='Pass') / COUNT(Interview result IN ('Pass','Fail')) x100"
    },
    "HR-047": {
        "name": "Average Interview Rating",
        "definition": "Average interviewer evaluation rating",
        "formula": "AVG(CAST(rating AS INT))"
    },
    "HR-048": {
        "name": "Interview per Hire",
        "definition": "Number of interviews conducted per hired employee",
        "formula": "COUNT(Interview) / COUNT(JobApplicant status='Hired')"
    },
    "HR-049": {
        "name": "Interviews Pending Feedback",
        "definition": "Completed interviews past schedule where result is still Pending",
        "formula": "COUNT(Interview result='Pending' AND scheduled_at < now − 2 days)"
    },
    "HR-050": {
        "name": "Offer Acceptance Rate",
        "definition": "Accepted offers compared to total offers extended",
        "formula": "COUNT(JobOffer state='Accepted') / COUNT(JobOffer state IN ('Accepted','Declined')) x100"
    },
    "HR-051": {
        "name": "Offer Expired",
        "definition": "Offers past expiration date without candidate decision",
        "formula": "COUNT(JobOffer workflow_state='Expired' OR (workflow_state='Sent' AND valid_until < CURRENT_DATE))"
    },
    "HR-052": {
        "name": "Offers Above Requisition Budget",
        "definition": "Job offers with basic salary exceeding requisition budget maximum",
        "formula": "COUNT(JobOffer base_salary > salary_max)"
    },
    "HR-053": {
        "name": "Source of Hire Mix",
        "definition": "Percentage of hires by source channel (Referral, Job portal, Agency, etc.)",
        "formula": "COUNT(JobApplicant status='Hired' per source) / COUNT(Hired) x100"
    },
    "HR-054": {
        "name": "Referral Conversion",
        "definition": "Referral applicants successfully hired",
        "formula": "COUNT(source='Referral' AND status='Hired') / COUNT(source='Referral') x100"
    },
    "HR-055": {
        "name": "Candidate Withdrawal Rate",
        "definition": "Applicants withdrawing voluntarily from the hiring pipeline",
        "formula": "COUNT(status='Withdrawn') / COUNT(JobApplicant) x100"
    },
    "HR-056": {
        "name": "Replacement vs New Headcount",
        "definition": "Proportion of replacement requisitions vs new headcount",
        "formula": "COUNT(reason='Replacement') / COUNT(JobRequisition) x100"
    },
    "HR-057": {
        "name": "Requisition Overdue",
        "definition": "Open requisitions where target start date has passed",
        "formula": "COUNT(requisition_status='Open' AND target_start_date < CURRENT_DATE)"
    },
    "HR-058": {
        "name": "Salary Expectations vs Range",
        "definition": "Average expected salary compared to budget midpoint",
        "formula": "AVG(expected_salary / ((salary_min+salary_max)/2))"
    },
    "HR-059": {
        "name": "Hiring Quality (Probation Pass Rate)",
        "definition": "Percentage of new hires passing probation",
        "formula": "COUNT(Hired employees with JobChange 'Probation completed') / COUNT(Hires) x100"
    },
    "HR-060": {
        "name": "Attendance Rate",
        "definition": "Days present (Present, WFH, Half day) compared to scheduled working days",
        "formula": "COUNT(Attendance status IN ('Present','Work from home')) + 0.5×Half day / COUNT(Attendance) x100"
    },
    "HR-061": {
        "name": "Absenteeism Rate",
        "definition": "Unplanned absence days compared to scheduled working days",
        "formula": "COUNT(status='Absent') / COUNT(Attendance) x100"
    },
    "HR-062": {
        "name": "Tardiness Rate",
        "definition": "Percentage of present days with late arrival exceeding grace period",
        "formula": "COUNT(late_minutes > ShiftType.late_grace_minutes) / COUNT(Present) x100"
    },
    "HR-063": {
        "name": "Average Late Minutes",
        "definition": "Average minutes late per tardiness occurrence",
        "formula": "AVG(late_minutes) WHERE late_minutes > 0"
    },
    "HR-064": {
        "name": "Average Daily Working Hours",
        "definition": "Average working hours per present attendance record",
        "formula": "AVG(working_hours) WHERE status='Present'"
    },
    "HR-065": {
        "name": "WFH Ratio",
        "definition": "Work from home days compared to total present days",
        "formula": "COUNT(status='Work from home') / COUNT(Present) x100"
    },
    "HR-066": {
        "name": "Missing Check-out",
        "definition": "Attendance records with check-in but missing check-out",
        "formula": "COUNT(status='Present' AND check_out IS NULL)"
    },
    "HR-067": {
        "name": "Geolocation Compliance",
        "definition": "Check-in logs containing valid geolocation coordinates",
        "formula": "COUNT(check_in_location filled) / COUNT(check_in) x100"
    },
    "HR-068": {
        "name": "Night Shift Employees",
        "definition": "Percentage of active employees assigned to night shifts",
        "formula": "COUNT(Employee shift_type.is_night_shift=1) / COUNT(Active) x100"
    },
    "HR-069": {
        "name": "Leave Utilization Rate",
        "definition": "Approved leave days used compared to total leave allocation",
        "formula": "SUM(LeaveApplication.total_days approved) / SUM(LeaveAllocation.total_leaves) x100"
    },
    "HR-070": {
        "name": "Average Leave Balance",
        "definition": "Average remaining leave balance per employee",
        "formula": "AVG(total_leaves − approved leave) per employee"
    },
    "HR-071": {
        "name": "Leave Liability (IDR)",
        "definition": "Estimated monetary liability of untaken leave balances",
        "formula": "SUM(leave balance × (basic salary / working days))"
    },
    "HR-072": {
        "name": "Leave Approval Turnaround",
        "definition": "Average hours from Pending Approval to decision",
        "formula": "AVG(LeaveApproval workflow transition hours)"
    },
    "HR-073": {
        "name": "Leave Rejection Rate",
        "definition": "Percentage of leave requests rejected",
        "formula": "COUNT(state='Rejected') / COUNT(Submitted) x100"
    },
    "HR-074": {
        "name": "Pending Leave Requests",
        "definition": "Leave requests pending approval > 2 days",
        "formula": "COUNT(workflow_state='Pending Approval' AND modified < now − 2 days)"
    },
    "HR-075": {
        "name": "Sick Leave Rate",
        "definition": "Sick leave days taken compared to working days",
        "formula": "SUM(total_days WHERE leave_type='Sakit') / working days x100"
    },
    "HR-076": {
        "name": "Unpaid Leave Days",
        "definition": "Total unpaid leave days taken",
        "formula": "SUM(Payslip.unpaid_leave_days)"
    },
    "HR-077": {
        "name": "Leave Attachment Compliance",
        "definition": "Percentage of leaves requiring attachment that included proof",
        "formula": "COUNT(requires_attachment=1 AND attachment filled) / COUNT(requires_attachment=1) x100"
    },
    "HR-078": {
        "name": "Carry-forward Ratio",
        "definition": "Unused leave carried forward to next fiscal year",
        "formula": "SUM(carry_forwarded) / SUM(total_leaves) x100"
    },
    "HR-079": {
        "name": "Bradford Factor (Average)",
        "definition": "Absence disruption metric (frequency squared x total duration)",
        "formula": "AVG(S² × D) where S=absence instances, D=total days absent"
    },
    "HR-080": {
        "name": "Total Overtime Hours",
        "definition": "Total approved overtime hours across organization",
        "formula": "SUM(OvertimeRequest.hours, state='Approved')"
    },
    "HR-081": {
        "name": "Overtime Hours per Employee",
        "definition": "Average overtime hours per active employee",
        "formula": "SUM(hours) / COUNT(DISTINCT active employees)"
    },
    "HR-082": {
        "name": "Overtime Cost % Salary",
        "definition": "Overtime premium expense compared to total gross salary",
        "formula": "SUM(overtime component amount) / SUM(gross_earnings) x100"
    },
    "HR-083": {
        "name": "Employees Exceeding Overtime Limit",
        "definition": "Employees exceeding statutory weekly or monthly overtime hours",
        "formula": "COUNT(DISTINCT employee with SUM(hours) > statutory limit)"
    },
    "HR-084": {
        "name": "Holiday Overtime",
        "definition": "Proportion of overtime worked on rest days or public holidays",
        "formula": "SUM(hours WHERE day_type != 'Working day') / SUM(hours) x100"
    },
    "HR-085": {
        "name": "Weighted Hours Ratio",
        "definition": "Statutory weighted overtime hours multiplier compared to actual hours",
        "formula": "SUM(weighted_hours) / SUM(hours)"
    },
    "HR-086": {
        "name": "Overtime Approval Turnaround",
        "definition": "Average turnaround time for overtime request approvals",
        "formula": "AVG(OvertimeApproval workflow transition duration)"
    },
    "HR-087": {
        "name": "Total Payroll Cost (Gross)",
        "definition": "Total gross salary paid across organization per period",
        "formula": "SUM(PayrollRun.total_gross)"
    },
    "HR-088": {
        "name": "Total Employer Cost",
        "definition": "Total employee cost including gross salary and employer contributions",
        "formula": "SUM(total_gross + total_employer_contributions)"
    },
    "HR-089": {
        "name": "Average Gross Salary per Employee",
        "definition": "Average gross earnings per employee",
        "formula": "SUM(total_gross) / SUM(employee_count)"
    },
    "HR-090": {
        "name": "Net-to-Gross Ratio",
        "definition": "Take-home pay compared to gross salary",
        "formula": "SUM(total_net_pay) / SUM(total_gross) x100"
    },
    "HR-091": {
        "name": "Payroll Variance MoM",
        "definition": "Month-over-month percentage change in gross payroll",
        "formula": "(current month gross − last month gross) / last month gross x100"
    },
    "HR-092": {
        "name": "Payroll On-time Rate",
        "definition": "Payroll disbursement completed on or before scheduled pay date",
        "formula": "COUNT(PaymentEntry posting_date <= PayrollRun.payment_date) / COUNT(PayrollRun) x100"
    },
    "HR-093": {
        "name": "Payroll Accuracy",
        "definition": "Percentage of payslips generated without corrections or amendments",
        "formula": "1 − COUNT(Payslip amended/cancelled) / COUNT(Payslip) x100"
    },
    "HR-094": {
        "name": "Active Employees Without Salary Assignment",
        "definition": "Active employees missing an active salary structure assignment",
        "formula": "COUNT(Active employee without SalaryAssignment is_active=1)"
    },
    "HR-095": {
        "name": "Employees Without Bank Account",
        "definition": "Active employees without a default bank account configured",
        "formula": "COUNT(Active employee without bank_accounts is_default=1)"
    },
    "HR-096": {
        "name": "Effective PPh 21 Rate",
        "definition": "Income tax withholding (PPh 21) compared to taxable gross pay",
        "formula": "SUM(Payslip.pph21) / SUM(taxable_gross) x100"
    },
    "HR-097": {
        "name": "Government-borne Tax (PPh 21 DTP) Utilized",
        "definition": "Value of government-borne income tax incentives utilized",
        "formula": "SUM(PayrollRun.total_pph21_dtp)"
    },
    "HR-098": {
        "name": "December Tax Withholding Variance",
        "definition": "Annualized PPh 21 tax variance adjusted in December final payroll",
        "formula": "SUM(pph21_annual − pph21_before) WHERE is_last_period=1"
    },
    "HR-099": {
        "name": "Employer Social Security (BPJS TK) Cost",
        "definition": "Employer social security contributions (JHT + JP + JKK + JKM)",
        "formula": "SUM(jht_employer + jp_employer + jkk + jkm)"
    },
    "HR-100": {
        "name": "Employer Health Insurance (BPJS Kes) Cost",
        "definition": "Employer healthcare insurance premium contributions",
        "formula": "SUM(kes_employer)"
    },
    "HR-101": {
        "name": "Social Security (BPJS) Participation",
        "definition": "Active employees enrolled in statutory BPJS TK and Health schemes",
        "formula": "COUNT(SalaryAssignment bpjs_employment=1 AND bpjs_health=1) / COUNT(Active) x100"
    },
    "HR-102": {
        "name": "Non-regular Components % Gross",
        "definition": "Bonus, THR, and variable payments compared to gross payroll",
        "formula": "SUM(PayslipDetail amount is_regular=0) / SUM(gross_earnings) x100"
    },
    "HR-103": {
        "name": "Attendance Deductions",
        "definition": "Salary deductions incurred from absences and unpaid leave",
        "formula": "SUM(absent_days + unpaid_leave_days deduction value)"
    },
    "HR-104": {
        "name": "Payroll Cost per Cost Center",
        "definition": "Gross payroll and employer taxes distributed by cost center",
        "formula": "SUM(gross_earnings + employer_contributions) GROUP BY cost_center"
    },
    "HR-105": {
        "name": "Appraisal Completion Rate",
        "definition": "Performance appraisals completed within evaluation cycle",
        "formula": "COUNT(Appraisal state='Completed') / COUNT(Eligible active employees) x100"
    },
    "HR-106": {
        "name": "Average Final Score",
        "definition": "Average final performance evaluation appraisal score",
        "formula": "AVG(final_score)"
    },
    "HR-107": {
        "name": "Rating Distribution",
        "definition": "Percentage distribution of employees across performance rating tiers",
        "formula": "COUNT(Appraisal per rating) / COUNT(Appraisal) x100"
    },
    "HR-108": {
        "name": "High Performer Ratio",
        "definition": "Percentage of employees rated Outstanding or Exceeds Expectations",
        "formula": "COUNT(rating IN ('Outstanding','Exceeds expectations')) / COUNT x100"
    },
    "HR-109": {
        "name": "Low Performer Ratio",
        "definition": "Percentage of employees rated Needs Improvement or Unsatisfactory",
        "formula": "COUNT(rating IN ('Needs improvement','Unsatisfactory')) / COUNT x100"
    },
    "HR-110": {
        "name": "Goal Weight Compliance",
        "definition": "Percentage of appraisals where goal weights total exactly 100%",
        "formula": "COUNT(total_weight=100) / COUNT(Appraisal) x100"
    },
    "HR-111": {
        "name": "Self vs Manager Score Gap",
        "definition": "Average variance between employee self-score and manager rating",
        "formula": "AVG(self_score − manager_score)"
    },
    "HR-112": {
        "name": "Development Plans Completed",
        "definition": "Performance appraisals with defined individual development plans",
        "formula": "COUNT(development_plan filled) / COUNT(Appraisal) x100"
    },
    "HR-113": {
        "name": "Appraisal Overdue",
        "definition": "Incomplete appraisals after evaluation cycle closing date",
        "formula": "COUNT(Appraisal not Completed AND cycle.end_date < CURRENT_DATE)"
    },
    "HR-114": {
        "name": "Regretted Attrition High Performer",
        "definition": "High-performing employees resigning voluntarily",
        "formula": "COUNT(EmployeeSeparation separation_type='Resignation' AND high performer) / COUNT(High performers) x100"
    },
    "HR-115": {
        "name": "Training Hours per Employee",
        "definition": "Total employee training hours delivered compared to headcount",
        "formula": "SUM(duration hours × attendees present) / AVG headcount"
    },
    "HR-116": {
        "name": "Training Attendance Rate",
        "definition": "Actual training participants compared to registered attendees",
        "formula": "SUM(attended_count) / COUNT(TrainingAttendee) x100"
    },
    "HR-117": {
        "name": "Training Cost per Participant",
        "definition": "Total training expense per actual attendee",
        "formula": "SUM(cost) / SUM(attended_count)"
    },
    "HR-118": {
        "name": "Training Spend % Payroll",
        "definition": "Training budget investment compared to gross payroll expense",
        "formula": "SUM(TrainingEvent.cost) / SUM(PayrollRun.total_gross) x100"
    },
    "HR-119": {
        "name": "Average Training Score",
        "definition": "Average participant test or evaluation score post-training",
        "formula": "AVG(score)"
    },
    "HR-120": {
        "name": "Training Coverage",
        "definition": "Percentage of active employees participating in at least 1 training event",
        "formula": "COUNT(DISTINCT employee attended) / COUNT(Active) x100"
    },
    "HR-121": {
        "name": "Certificates Uploaded",
        "definition": "Trained participants who have uploaded certification proof",
        "formula": "COUNT(certificate filled) / COUNT(Attended) x100"
    },
    "HR-122": {
        "name": "Turnover Rate",
        "definition": "Total employee separations compared to average headcount",
        "formula": "COUNT(EmployeeSeparation docstatus=1 in period) / AVG headcount x100"
    },
    "HR-123": {
        "name": "Voluntary Turnover Rate",
        "definition": "Voluntary resignations compared to average headcount",
        "formula": "COUNT(separation_type='Resignation') / AVG headcount x100"
    },
    "HR-124": {
        "name": "Involuntary Turnover Rate",
        "definition": "Employer-initiated terminations compared to average headcount",
        "formula": "COUNT(separation_type='PHK') / AVG headcount x100"
    },
    "HR-125": {
        "name": "First-Year Attrition",
        "definition": "Employees leaving with less than 1 year of service",
        "formula": "COUNT(last_working_date − hire_date < 365) / COUNT(Hires in last 12 months) x100"
    },
    "HR-126": {
        "name": "Retention Rate",
        "definition": "Employees retained from beginning to end of period",
        "formula": "(Beginning headcount − departures from cohort) / Beginning headcount x100"
    },
    "HR-127": {
        "name": "Exit Interview Completion",
        "definition": "Separations with completed exit interview documentation",
        "formula": "COUNT(exit_interview_done=1) / COUNT(EmployeeSeparation) x100"
    },
    "HR-128": {
        "name": "Eligible for Rehire Ratio",
        "definition": "Departing employees designated as eligible for rehire",
        "formula": "COUNT(eligible_for_rehire=1) / COUNT(EmployeeSeparation) x100"
    },
    "HR-129": {
        "name": "Clearance Checklist Completion",
        "definition": "Offboarding asset and access clearance items completed",
        "formula": "COUNT(SeparationChecklistItem is_done=1) / COUNT(Items) x100"
    },
    "HR-130": {
        "name": "Average Notice Period",
        "definition": "Average days between notice date and last working date",
        "formula": "AVG(last_working_date − notice_date)"
    },
    "HR-131": {
        "name": "Total Severance Pay",
        "definition": "Total statutory severance entitlements paid (UP + UPMK + UPH + separation pay)",
        "formula": "SUM(total_entitlement)"
    },
    "HR-132": {
        "name": "Departed Employees With Active Access",
        "definition": "Employees with Left status retaining active salary structure or system access",
        "formula": "COUNT(Employee status='Left' AND SalaryAssignment is_active=1)"
    },
    "HR-133": {
        "name": "Exit Reason Breakdown",
        "definition": "Percentage distribution of departures by separation reason",
        "formula": "COUNT per separation_type / COUNT total x100"
    }
}

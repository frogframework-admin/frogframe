# Talent Management Guide

This guide covers Competency Management, Appraisals, Talent Reviews (9-Box), Succession Planning, Individual Development Plans (IDP), and Training in FrogFrame ERP.

## 1. Competencies & Job Profiles
- **Competency** (`Competency`): Master competency definition with `category` (`Core Value`, `Leadership`, `Functional`, `Technical`) and child table `proficiency_levels` (`CompetencyLevel`, 1–5 with behavioral indicators and assessment criteria).
- **Job Profile** (`JobProfile`): Lists required competencies and `required_level` (1–5) in `JobProfileCompetency`.
- **Employee** (`Employee`): Stores assessed `competencies` (`EmployeeCompetency`), automatically synced when an `Appraisal` is completed.
- **Competency Gap Analysis Report** (`erp-competency-gap`): Compares `JobProfileCompetency.required_level` against `EmployeeCompetency.proficiency_level` per employee/unit.

## 2. Performance Appraisals
- **Appraisal Cycle** (`AppraisalCycle`): Defines review period and status (`In progress` required to complete appraisals).
- **Appraisal** (`Appraisal`, workflow `AppraisalReview`):
  - On creation, competencies are prefilled from the employee's `JobProfile` if empty.
  - Employees fill `self_score`; reviewers (`reports_to`) fill `manager_score`.
  - Completing an appraisal syncs competency scores (`manager_score`) to the employee's profile.
  - Competencies show **360 (others)** from the employee's closed 360 feedback of the same cycle (development input, not part of the final score).
- **360 Feedback** (`MultiRaterReview`, `FeedbackResponse`, `FeedbackTemplate`): multi-rater feedback from self, manager, peers, direct reports and internal customers with an anonymity threshold. See [360-degree feedback](360-feedback.md).

## 3. Talent Review (9-Box) & Access Control
- **Talent Review** (`TalentReview`): One per `(employee, appraisal_cycle)`.
- Linked `Appraisal` must be **Completed** and belong to the same `appraisal_cycle`.
- Performance thresholds are configurable in **HR Settings → Talent Management**:
  - `talent_high_perf_threshold` (default `3.75`)
  - `talent_mod_perf_threshold` (default `2.5`)
- **Access control (`allow_manager_talent_access` in HR Settings)**:
  - The `Manager` permission on `TalentReview` and `SuccessionPlan` is scoped to the stored field `manager_viewer`
    (`match:manager_viewer=employee`). Lists, reports, exports, the API and the form all follow it.
  - **Off (default)**: `manager_viewer` is empty, so only `HR-Talent`, `HR-Admin` and `System Manager` see these records.
  - **On**: `manager_viewer` = the employee's direct manager (`reports_to`; for a succession plan, the incumbent's manager).
  - Changing the setting rewrites `manager_viewer` on all existing records at once (also submitted ones); the daily job
    `Erp.TalentAccessRefresh` follows job changes and resignations.
- **Calibration Session** (`CalibrationSession`): reviews linked to a session must be of the same appraisal cycle and,
  when the session has a unit, of that unit or a unit below it. After the session is submitted, linked reviews keep
  their 9-box levels and scores, and no review can be added or removed.
- **Potential**: when `aspiration_score`, `learning_agility_score` and `leadership_score` (1–5) are filled, their
  average sets `potential_level`.
- **9-Box Talent Grid report** (`erp-nine-box`): one row per box with count, share and names; filters appraisal cycle,
  unit (with sub-units), calibration session and draft reviews.

## 4. Succession Planning & Development Plans (IDP)
- **Succession Plan** (`SuccessionPlan`):
  - One active plan per `Position` (`is_key_position` flag available on `Position`).
  - Auto-fills `incumbent_employee` from the position holder, rejects candidates who have `Left`, and refreshes candidate `latest_rating` and `nine_box_position` on load.
- **Development Plan** (`DevelopmentPlan`):
  - Tracks 70-20-10 `DevelopmentGoal` items.
  - Visible to the employee, the mentor and the direct manager (`reports_to`, permission `match:reports_to=employee`).
  - Only `HR-Talent`, `mentor`, or `reports_to` (manager) can mark a plan `Completed`; completed plans are locked for non-HR users.

## 5. Training & Approval Delegation
- **Training Request** (`TrainingRequest`, workflow `TrainingRequestApproval`): Routed to `approver` (`reports_to`) then `HR-Talent`.
- `ApprovalDelegationRefreshJob` and `ReassignReportsAsync` automatically reassign pending `TrainingRequest` (`approver`) and `Appraisal` (`reviewer`) records when a manager resigns or transfers. Draft appraisals pick up the new manager because `reviewer` is fetched from `employee.reports_to` on save; Training Requests already waiting for the manager are moved by the daily job.
- **Propose Job Change** (on a Completed Appraisal) opens a new Job Change form prefilled with the employee, reason
  Promotion and the appraisal result; HR fills the new grade/position or salary. A Job Change with only a salary
  adjustment is valid.

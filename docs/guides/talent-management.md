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

## 3. Talent Review (9-Box) & Access Control
- **Talent Review** (`TalentReview`): One per `(employee, appraisal_cycle)`.
- Linked `Appraisal` must be **Completed** and belong to the same `appraisal_cycle`.
- Performance thresholds are configurable in **HR Settings → Talent Management**:
  - `talent_high_perf_threshold` (default `3.75`)
  - `talent_mod_perf_threshold` (default `2.5`)
- **Access Control (`allow_manager_talent_access` in HR Settings)**:
  - **Default (`false`)**: `TalentReview`, `SuccessionPlan`, and `DevelopmentPlan` are restricted to `HR-Talent`, `HR-Admin`, and `System Manager` (plus `employee`/`mentor` on their own `DevelopmentPlan`).
  - **Enabled (`true`)**: `Manager` can also read records for their direct reports (`match:reports_to=employee`).

## 4. Succession Planning & Development Plans (IDP)
- **Succession Plan** (`SuccessionPlan`):
  - One active plan per `Position` (`is_key_position` flag available on `Position`).
  - Auto-fills `incumbent_employee` from the position holder, rejects candidates who have `Left`, and refreshes candidate `latest_rating` and `nine_box_position` on load.
- **Development Plan** (`DevelopmentPlan`):
  - Tracks 70-20-10 `DevelopmentGoal` items.
  - Only `HR-Talent`, `mentor`, or `reports_to` (manager) can mark a plan `Completed`; completed plans are locked for non-HR users.

## 5. Training & Approval Delegation
- **Training Request** (`TrainingRequest`, workflow `TrainingRequestApproval`): Routed to `approver` (`reports_to`) then `HR-Talent`.
- `ApprovalDelegationRefreshJob` and `ReassignReportsAsync` automatically reassign pending `TrainingRequest` (`approver`) and `Appraisal` (`reviewer`) records when a manager resigns or transfers.

# 360-degree feedback

360 feedback collects ratings and comments about one employee from several people: the employee, their manager,
peers, direct reports and internal customers. It is meant for **development**: the result is shown next to the
appraisal's competencies and feeds talent discussions, but it is **not** part of the appraisal's final score.

Menu *Talent Management*: **360 Feedback** (`MultiRaterReview`), **My Feedback Requests** (`FeedbackResponse`),
**Feedback Template** (`FeedbackTemplate`). Report **360 Feedback Summary** (`erp-360-feedback`): HR-Talent, HR-Admin, dan administrator melihat semua review; atasan melihat review timnya, tetapi tidak review dirinya sendiri.

## Setting up

1. **Feedback Template** (HR-Talent): questions, each optionally linked to a **Competency**, answered with a rating 1–5
   (and an optional comment) or with a comment only. Rules:
   - *Minimum peers* (default 3) the employee must nominate; *Minimum direct reports* (default 0, applies only when the
     employee has that many);
   - *Maximum raters* (default 12, self and manager included);
   - *Anonymity threshold* (default 3, at least 2), see below.
2. **Appraisal Cycle**: choose the *360 feedback template* and *360 feedback due* date, then **Launch 360 feedback**.
   Every employee with an appraisal in the cycle gets a 360 feedback (status *Nominating*) and an inbox message.
   HR can also create a single 360 feedback by hand.

## Flow

| Status | Who | What |
|---|---|---|
| Nominating | Employee (or HR) | *Self* and the direct *Manager* are already listed. The employee adds peers, direct reports and internal customers, then presses **Send raters for approval** (checks the minimums). |
| Awaiting approval | Manager (or HR) | May add or remove raters, then presses **Send feedback requests**. Without a manager, HR approves. HR may also send the requests straight from *Nominating*. |
| In progress | Every rater | Each rater gets one *360 Feedback Response* (inbox message, *My Feedback Requests*, ESS app), answers and presses **Submit**. Required questions must be answered. Reminders: daily from 3 days before the due date, and after it. HR can add raters later and press *Send feedback requests* again. |
| Closed | HR | **Close and show results** computes the averages, copies them to the appraisal and informs the employee and the manager. Unanswered requests are withdrawn. |
| Cancelled | HR | **Cancel 360 feedback**; unanswered requests are withdrawn. |

Who may change what:

- Status, progress and results are written by the system only.
- Employee, cycle and template: HR only, while *Nominating*. Due date: HR only.
- The rater list: the employee while *Nominating*, the manager while *Awaiting approval*, HR until the review is closed.
  A rater who has already been asked cannot be removed.
- The *Manager* row must be the employee's direct manager (*Reports to*), the *Self* row the employee, and nobody may be
  listed twice.

## Anonymity

| Group | How it is shown |
|---|---|
| Self, Manager | By name (one person each). |
| Peers, Direct reports, Internal customers | Only as the **average of a group** that has at least *anonymity threshold* answers for that question. |
| Smaller groups | Merged into one pool; the pool counts in *All others* only if it reaches the threshold as well, otherwise it is left out. |

So every published number rests on at least the threshold number of anonymous raters (or on the named self/manager),
and no number can be worked out by subtracting others. Comments follow the same rule per response; anonymous comments
are shown sorted alphabetically, not in answer order. *How to read* on the result explains which groups were shown,
merged or left out.

Single responses are visible only to the **rater** (and, read-only, to HR-Talent and System Managers for support and
audit). The employee and the manager see only the closed 360 feedback, i.e. the averages and comments above. HR should
keep HR-Talent access to a small group.

## Results and integration

On closing, per question: Self, Manager, Peers, Direct reports, Internal customers, **All others** (manager + shown
groups + pool) and **Others − self** (positive: others see more than the employee does, a *hidden strength*; negative: a
*blind spot*). Overall averages for self, manager and all others.

- **Appraisal**: for each competency in the employee's appraisal of the same cycle, *360 (others)* = the average of
  *All others* over the questions of that competency. Only while the appraisal is still a draft (otherwise the close
  message says so). The field cannot be typed in.
- **Talent Review / calibration**: use the 360 result as input for leadership and potential scores.
- **Development Plan**: competencies with a large negative gap are good development goals.
- **ESS app**: `GET/PUT /api/ess/v1/feedback/requests…` and `GET /api/ess/v1/feedback/results`
  ([ESS API](ess-mobile-api.md) section 13).

## Not in this version

Declining a request (HR removes the rater instead before requesting, or the request simply stays unanswered), external
raters without an account, anytime (continuous) feedback, and question sets per relationship.

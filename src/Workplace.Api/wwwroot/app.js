const api = {
  user: null,
  employees: [],
  lookups: { sites: [], departments: [], shifts: [], employmentStatuses: [] },
  page: 1,
  pageSize: 8,
  total: 0,
  sort: "name",
  requestId: 0,
  attendancePage: 1,
  attendancePageSize: 8,
  demoMode: false,
  adminUsers: [],
  adminRoles: [],
  adminEmployees: [],
  attendanceReport: null,
  employeePortal: null,
  employeeAttendancePage: 1,
  employeeAttendancePageSize: 10,
  employeeAttendanceTotal: 0,
  auditLogs: [],
  auditPage: 1,
  auditPageSize: 25
};
let searchTimer;
let attendanceSearchTimer;
let scheduleSearchTimer;
let auditSearchTimer;

const demoLookups = {
  sites: [{ id: "demo-site-north", name: "North Campus" }],
  departments: [
    { id: "demo-dept-operations", name: "Operations" },
    { id: "demo-dept-logistics", name: "Logistics" },
    { id: "demo-dept-finance", name: "Finance" },
    { id: "demo-dept-people", name: "People & Culture" }
  ],
  shifts: [
    { id: "demo-shift-day", name: "Day shift" },
    { id: "demo-shift-early", name: "Early shift" },
    { id: "demo-shift-late", name: "Late shift" }
  ],
  employmentStatuses: ["active", "on_leave", "terminated"]
};

const demoSeedEmployees = [
  ["10000000-0000-4000-8000-000000000001", "EMP-1001", "Thabo", "Mokoena", "thabo.mokoena@example.com", "Systems Administrator", "Operations", "Day shift", "active"],
  ["10000000-0000-4000-8000-000000000002", "EMP-1042", "Lerato", "Nkosi", "lerato.nkosi@example.com", "Operations Lead", "Operations", "Day shift", "active"],
  ["10000000-0000-4000-8000-000000000003", "EMP-1087", "Sipho", "Dlamini", "sipho.dlamini@example.com", "Warehouse Supervisor", "Logistics", "Day shift", "active"],
  ["10000000-0000-4000-8000-000000000004", "EMP-1103", "Amara", "Patel", "amara.patel@example.com", "Accounts Officer", "Finance", "Day shift", "active"],
  ["10000000-0000-4000-8000-000000000005", "EMP-1128", "Thandiwe", "Khumalo", "thandiwe.khumalo@example.com", "HR Coordinator", "People & Culture", "Day shift", "active"],
  ["10000000-0000-4000-8000-000000000006", "EMP-1156", "Kabelo", "Molefe", "kabelo.molefe@example.com", "Safety Technician", "Operations", "Early shift", "active"],
  ["10000000-0000-4000-8000-000000000007", "EMP-1189", "Nadia", "Jacobs", "nadia.jacobs@example.com", "Payroll Analyst", "Finance", "Day shift", "on_leave"],
  ["10000000-0000-4000-8000-000000000008", "EMP-1210", "Bongani", "Maseko", "bongani.maseko@example.com", "Fleet Coordinator", "Logistics", "Late shift", "active"],
  ["10000000-0000-4000-8000-000000000009", "EMP-1234", "Zanele", "Ndlovu", "zanele.ndlovu@example.com", "Quality Inspector", "Operations", "Early shift", "active"]
];

let demoEmployees = [];

function resetDemoData() {
  demoEmployees = demoSeedEmployees.map(([id, employeeNumber, firstName, lastName, email, jobTitle, department, shift, employmentStatus]) => ({
    id,
    employeeNumber,
    firstName,
    lastName,
    fullName: `${firstName} ${lastName}`,
    email,
    jobTitle,
    siteId: demoLookups.sites[0].id,
    site: demoLookups.sites[0].name,
    departmentId: demoLookups.departments.find(item => item.name === department).id,
    department,
    shiftId: demoLookups.shifts.find(item => item.name === shift).id,
    shift,
    managerId: null,
    managerName: null,
    employmentStatus,
    hireDate: null
  }));
}
function shiftCategoryField(value = "day") {
  return `<div class="form-field"><label for="field-category">Category *</label><select id="field-category" name="category" required>${["day", "early", "late", "night"].map(category => `<option value="${category}" ${category === value ? "selected" : ""}>${shiftCategoryName(category)}</option>`).join("")}</select></div>`;
}
function checkboxField(label, name, checked = false) {
  return `<div class="form-field full"><label class="remember-option"><input id="field-${name}" name="${name}" type="checkbox" ${checked ? "checked" : ""}> ${escapeHtml(label)}</label></div>`;
}

async function apiRequest(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...options, headers, credentials: "same-origin" });
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    const validation = body?.errors ? Object.values(body.errors).flat().join(" ") : "";
    const missingApiRoute = response.status === 404 && path.startsWith("/api/");
    const message = missingApiRoute
      ? `The server at ${window.location.origin} is not serving the Workplace API. Run "dotnet run --project src/Workplace.Api" from the workplace-api folder, then open http://localhost:5080/.`
      : body?.title || body?.detail || validation || `Request failed (${response.status}).`;
    const error = new Error(message);
    error.status = response.status;
    if (response.status === 401 && api.user) showLogin("Your session has expired. Please sign in again.");
    throw error;
  }
  return body;
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  })[character]);
}

function displayStatus(status) {
  return ({ active: "Active", on_leave: "On leave", terminated: "Terminated" })[status] || status;
}
function shiftCategoryName(category) {
  return ({ day: "Day", early: "Early", late: "Late", night: "Night" })[category] || category;
}
function attendanceStatusLabel(status) {
  return attendanceStatuses[status] || status;
}

let attendance = [];
const attendanceStatuses = {
  on_time: "On time",
  late: "Late",
  absent: "Absent",
  incomplete: "Incomplete",
  approved_exception: "Approved exception"
};

const accessEvents = [
  { employee: "Lerato Nkosi", id: "EMP-1042", time: "10:42:18", direction: "Entry", identity: "Verified", breath: "Clear", metal: "Clear", decision: "Granted", initials: "LN", tone: "warm" },
  { employee: "Sipho Dlamini", id: "EMP-1087", time: "10:38:05", direction: "Exit", identity: "Verified", breath: "—", metal: "—", decision: "Granted", initials: "SD", tone: "mint" },
  { employee: "Amara Patel", id: "EMP-1103", time: "10:31:42", direction: "Entry", identity: "Verified", breath: "Clear", metal: "Clear", decision: "Granted", initials: "AP", tone: "violet" },
  { employee: "Kabelo Molefe", id: "EMP-1156", time: "10:24:09", direction: "Entry", identity: "Inconclusive", breath: "Review", metal: "Clear", decision: "Needs review", initials: "KM", tone: "mint", review: "Breathalyser result was inconclusive; human review required." },
  { employee: "Nadia Jacobs", id: "EMP-1189", time: "10:17:36", direction: "Entry", identity: "Verified", breath: "Clear", metal: "Alert", decision: "Needs review", initials: "NJ", tone: "violet", review: "Metal detector alert; awaiting secondary screening." },
  { employee: "Zanele Ndlovu", id: "EMP-1234", time: "10:09:51", direction: "Entry", identity: "Verified", breath: "Clear", metal: "Clear", decision: "Granted", initials: "ZN", tone: "" }
];

const liveActivity = [
  { employee: "Lerato Nkosi", id: "EMP-1042", department: "Operations", shift: "Day · 08:00", time: "10:42", status: "On site", initials: "LN", tone: "warm" },
  { employee: "Sipho Dlamini", id: "EMP-1087", department: "Logistics", shift: "Day · 08:00", time: "10:38", status: "Checked out", initials: "SD", tone: "mint" },
  { employee: "Amara Patel", id: "EMP-1103", department: "Finance", shift: "Day · 08:30", time: "10:31", status: "On site", initials: "AP", tone: "violet" },
  { employee: "Kabelo Molefe", id: "EMP-1156", department: "Operations", shift: "Early · 06:00", time: "10:24", status: "Review", initials: "KM", tone: "mint" },
  { employee: "Nadia Jacobs", id: "EMP-1189", department: "Finance", shift: "Day · 08:30", time: "10:17", status: "On site", initials: "NJ", tone: "violet" }
];

const pages = ["dashboard", "users", "employees", "attendance", "my-attendance", "reports", "audit-logs", "shifts", "access"];
let currentRole = "";
let pendingPasswordChangeUser = null;
let reviewFilterOnly = false;
let shiftPatterns = [];
let shiftAssignments = [];

function initialsAvatar(name, initials, tone = "") {
  return `<span class="mini-avatar ${escapeHtml(tone)}">${escapeHtml(initials || name.split(" ").map(part => part[0]).slice(0, 2).join(""))}</span>`;
}
function employeeCell(row) {
  return `<div class="employee-cell">${initialsAvatar(row.name || row.employee, row.initials, row.tone)}<span><strong>${row.name || row.employee}</strong><small>${row.id}</small></span></div>`;
}
function badge(status) {
  const classes = {
    "On time": "badge-green", "On site": "badge-green", "Granted": "badge-green", "Active": "badge-green", "Clear": "check-ok",
    "Late": "badge-amber", "Needs review": "badge-red", "Absent": "badge-red", "Terminated": "badge-red", "Offline": "badge-amber", "On leave": "badge-gray",
    "Checked out": "badge-gray", "Incomplete": "badge-blue", "Approved exception": "badge-blue", "Denied": "badge-red"
  };
  return `<span class="badge ${classes[status] || "badge-gray"}">${escapeHtml(status)}</span>`;
}
function renderLive() {
  const canReviewAccess = currentRole === "System Administrator" || currentRole === "Security Officer";
  const visibleActivity = liveActivity.filter(row => canReviewAccess || row.status !== "Review");
  document.querySelector("#live-rows").innerHTML = visibleActivity.map(row => `<tr><td>${employeeCell(row)}</td><td class="dept-cell">${row.department}</td><td class="shift-cell">${row.shift}</td><td class="time-cell">${row.time}</td><td>${badge(row.status === "Review" ? "Needs review" : row.status)}</td></tr>`).join("");
}

function getDemoEmployeeResult(query, departmentId, status) {
  const search = query.toLocaleLowerCase();
  const filtered = demoEmployees
    .filter(row => status ? row.employmentStatus === status : row.employmentStatus !== "terminated")
    .filter(row => !departmentId || row.departmentId === departmentId)
    .filter(row => !search || `${row.fullName} ${row.employeeNumber} ${row.email} ${row.department} ${row.jobTitle}`.toLocaleLowerCase().includes(search))
    .sort((left, right) => {
      if (api.sort === "number") return left.employeeNumber.localeCompare(right.employeeNumber);
      const byName = left.fullName.localeCompare(right.fullName);
      return api.sort === "-name" ? -byName : byName;
    });
  const start = (api.page - 1) * api.pageSize;
  return {
    items: filtered.slice(start, start + api.pageSize),
    total: filtered.length,
    page: api.page,
    pageSize: api.pageSize
  };
}

function renderEmployees() {
  const requestId = ++api.requestId;
  const query = document.querySelector("#employee-search").value.trim();
  const departmentId = document.querySelector("#department-filter").value;
  const status = document.querySelector("#employee-status-filter").value;
  const params = new URLSearchParams({ page: String(api.page), pageSize: String(api.pageSize), sort: api.sort });
  if (query) params.set("search", query);
  if (departmentId) params.set("departmentId", departmentId);
  if (status) params.set("status", status);
  document.querySelector("#employee-rows").innerHTML = `<tr><td colspan="7" class="loading-row">Loading employee records…</td></tr>`;

  const resultPromise = api.demoMode
    ? Promise.resolve(getDemoEmployeeResult(query, departmentId, status))
    : apiRequest(`/api/employees?${params}`);
  resultPromise
    .then(result => {
      if (requestId !== api.requestId) return;
      api.employees = result.items;
      api.total = result.total;
      const canManage = api.user?.roles.some(role => role === "System Administrator" || role === "HR Officer");
      document.querySelector("#employee-rows").innerHTML = result.items.length
        ? result.items.map(row => {
          const initials = row.fullName.split(/\s+/).slice(0, 2).map(part => part[0]).join("").toUpperCase();
          const name = escapeHtml(row.fullName);
          const employeeNumber = escapeHtml(row.employeeNumber);
          const statusLabel = displayStatus(row.employmentStatus);
          return `<tr>
            <td><div class="employee-cell">${initialsAvatar(name, initials)}<span><strong>${name}</strong><small>${employeeNumber}</small></span></div></td>
            <td class="time-cell">${employeeNumber}</td>
            <td class="dept-cell">${escapeHtml(row.department)}</td>
            <td>${escapeHtml(row.jobTitle)}</td>
            <td>${escapeHtml(row.shift || "—")}</td>
            <td>${badge(statusLabel)}</td>
            <td>${canManage ? `<button class="row-action employee-view" data-id="${escapeHtml(row.id)}" aria-label="Edit ${name}">···</button>` : ""}</td>
          </tr>`;
        }).join("")
        : `<tr><td colspan="7" class="empty-state">No employees match your search. Try another name or filter.</td></tr>`;
      const first = api.total ? (api.page - 1) * api.pageSize + 1 : 0;
      const last = Math.min(api.page * api.pageSize, api.total);
      document.querySelector("#employee-result-count").textContent = `Showing ${first}–${last} of ${api.total} employees`;
      renderEmployeePagination();
    })
    .catch(error => {
      if (requestId !== api.requestId) return;
      document.querySelector("#employee-rows").innerHTML = `<tr><td colspan="7" class="empty-state">Could not load employee records.</td></tr>`;
      document.querySelector("#employee-result-count").textContent = "Employee records unavailable";
      notify(error.message, "error");
    });
}

function renderEmployeePagination() {
  const pageCount = Math.max(1, Math.ceil(api.total / api.pageSize));
  const container = document.querySelector("#employee-pagination");
  if (!container) return;
  const buttons = [`<button class="page-button" data-page="${api.page - 1}" aria-label="Previous page" ${api.page <= 1 ? "disabled" : ""}>‹</button>`];
  const firstPage = Math.max(1, Math.min(api.page - 2, pageCount - 4));
  const lastPage = Math.min(pageCount, firstPage + 4);
  for (let page = firstPage; page <= lastPage; page += 1) {
    buttons.push(`<button class="page-button ${page === api.page ? "selected" : ""}" data-page="${page}" ${page === api.page ? 'aria-current="page"' : ""}>${page}</button>`);
  }
  buttons.push(`<button class="page-button" data-page="${api.page + 1}" aria-label="Next page" ${api.page >= pageCount ? "disabled" : ""}>›</button>`);
  container.innerHTML = buttons.join("");
}

async function loadEmployeeSummary() {
  const summary = api.demoMode
    ? {
      total: demoEmployees.filter(row => row.employmentStatus !== "terminated").length,
      active: demoEmployees.filter(row => row.employmentStatus === "active").length,
      onLeave: demoEmployees.filter(row => row.employmentStatus === "on_leave").length
    }
    : await apiRequest("/api/employees/summary");
  document.querySelector("#employee-summary-count").textContent = summary.total;
  document.querySelector(".nav-count").textContent = summary.total;
  document.querySelector("#employee-active-count").textContent = `${summary.active} active`;
  document.querySelector("#employee-leave-count").textContent = `${summary.onLeave} on leave`;
  const adminCount = document.querySelector("#admin-employee-count");
  if (adminCount) adminCount.textContent = summary.total;
  if (api.user?.roles.includes("Line Manager")) {
    document.querySelector("#manager-team-count").textContent = summary.total;
    document.querySelector("#manager-team-caption").textContent =
      `${summary.active} active · ${summary.onLeave} on leave`;
  }
}

async function loadLookups() {
  api.lookups = api.demoMode ? demoLookups : await apiRequest("/api/lookups");
  const departmentOptions = api.lookups.departments.map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join("");
  document.querySelector("#department-filter").innerHTML = `<option value="">All departments</option>${departmentOptions}`;
  document.querySelector("#attendance-department-filter").innerHTML = `<option value="">All departments</option>${departmentOptions}`;
  document.querySelector("#report-department-filter").innerHTML = `<option value="">All departments</option>${departmentOptions}`;
  document.querySelector("#assignment-department-filter").innerHTML = `<option value="">All departments</option>${departmentOptions}`;
}
function renderAttendance() {
  const rows = attendance.map(row => {
    const workedMinutes = row.workedMinutes;
    const hours = workedMinutes == null
      ? "—"
      : `${String(Math.floor(workedMinutes / 60)).padStart(2, "0")}h ${String(workedMinutes % 60).padStart(2, "0")}m`;
    return `<tr><td>${employeeCell({ name: row.employeeName, id: row.employeeNumber })}</td><td>${escapeHtml(row.department)}</td><td class="time-cell">${escapeHtml(row.scheduledStart.slice(0, 5))}</td><td class="time-cell">${row.clockIn ? escapeHtml(row.clockIn.slice(0, 5)) : "—"}</td><td>${row.clockOut ? escapeHtml(row.clockOut.slice(0, 5)) : "—"}</td><td>${hours}</td><td>${badge(attendanceStatusLabel(row.status))}</td><td>${currentRole === "Auditor" ? "" : `<button class="row-action attendance-edit" data-id="${row.id}" aria-label="Review ${escapeHtml(row.employeeName)} attendance">···</button>`}</td></tr>`;
  });
  document.querySelector("#attendance-rows").innerHTML = rows.length
    ? rows.join("")
    : `<tr><td colspan="8" class="empty-state">No attendance records match these filters.</td></tr>`;
}
function renderManagerAttendanceSummary(response) {
  if (!api.user?.roles.includes("Line Manager")) return;
  const summary = response.summary;
  document.querySelector("#manager-attendance-count").textContent = summary.total;
  document.querySelector("#manager-attendance-caption").textContent = `For ${response.date}`;
  document.querySelector("#manager-attention-count").textContent =
    summary.late + summary.absent + summary.incomplete;
  document.querySelector("#manager-attention-caption").textContent =
    `${summary.late} late · ${summary.absent} absent · ${summary.incomplete} incomplete`;
}
function renderAttendancePagination(response) {
  const pageCount = Math.max(1, Math.ceil(response.total / response.pageSize));
  const visiblePages = new Set([1, pageCount, response.page - 1, response.page, response.page + 1]
    .filter(page => page >= 1 && page <= pageCount));
  const buttons = [];
  let previousPage = 0;
  for (const page of [...visiblePages].sort((first, second) => first - second)) {
    if (previousPage && page - previousPage > 1) buttons.push("<span>…</span>");
    buttons.push(`<button class="page-button ${page === response.page ? "selected" : ""}" data-attendance-page="${page}">${page}</button>`);
    previousPage = page;
  }
  document.querySelector("#attendance-pagination").innerHTML = [
    `<button class="page-button" data-attendance-page="${response.page - 1}" ${response.page <= 1 ? "disabled" : ""}>‹</button>`,
    ...buttons,
    `<button class="page-button" data-attendance-page="${response.page + 1}" ${response.page >= pageCount ? "disabled" : ""}>›</button>`
  ].join("");
  const first = response.total ? (response.page - 1) * response.pageSize + 1 : 0;
  const last = Math.min(response.page * response.pageSize, response.total);
  document.querySelector("#attendance-result-count").textContent = response.total
    ? `Showing ${first}–${last} of ${response.total} records`
    : "No records found";
}
async function loadAttendance() {
  const date = document.querySelector("#attendance-date").value;
  const params = new URLSearchParams({
    page: String(api.attendancePage),
    pageSize: String(api.attendancePageSize)
  });
  const search = document.querySelector("#attendance-search").value.trim();
  const departmentId = document.querySelector("#attendance-department-filter").value;
  const status = document.querySelector("#attendance-status-filter").value;
  if (date) params.set("date", date);
  if (search) params.set("search", search);
  if (departmentId) params.set("departmentId", departmentId);
  if (status) params.set("status", status);
  const response = await apiRequest(`/api/attendance?${params}`);
  attendance = response.items;
  if (!date) document.querySelector("#attendance-date").value = response.date;
  const summary = response.summary;
  [
    [".attendance-count-total", summary.total],
    [".attendance-count-ontime", summary.onTime],
    [".attendance-count-late", summary.late],
    [".attendance-count-absent", summary.absent],
    [".attendance-count-incomplete", summary.incomplete]
  ].forEach(([selector, value]) => { document.querySelector(selector).textContent = value; });
  const adminAttendanceCount = document.querySelector("#admin-attendance-count");
  if (adminAttendanceCount) adminAttendanceCount.textContent = summary.total;
  const adminAttendanceCaption = document.querySelector("#admin-attendance-caption");
  if (adminAttendanceCaption) adminAttendanceCaption.textContent = `Live records for ${response.date}`;
  const attentionCount = summary.late + summary.absent + summary.incomplete;
  const adminAttentionCount = document.querySelector("#admin-attendance-attention");
  if (adminAttentionCount) adminAttentionCount.textContent = attentionCount;
  const adminAttentionCaption = document.querySelector("#admin-attendance-attention-caption");
  if (adminAttentionCaption) adminAttentionCaption.textContent = `${summary.late} late · ${summary.absent} absent · ${summary.incomplete} incomplete`;
  renderManagerAttendanceSummary(response);
  renderAttendance();
  renderAttendancePagination(response);
}
function reportDateValue(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}
function initializeReportDates() {
  const to = new Date();
  const from = new Date(to);
  from.setDate(from.getDate() - 29);
  if (!document.querySelector("#report-from-date").value)
    document.querySelector("#report-from-date").value = reportDateValue(from);
  if (!document.querySelector("#report-to-date").value)
    document.querySelector("#report-to-date").value = reportDateValue(to);
}
function reportDisplayDate(value) {
  return new Date(`${value}T00:00:00`).toLocaleDateString();
}
function renderAttendanceReport(report) {
  api.attendanceReport = report;
  document.querySelector("#report-range-label").textContent =
    `${report.fromDate} to ${report.toDate} · ${report.employees.length} employees`;
  document.querySelector("#report-total").textContent = report.summary.total;
  document.querySelector("#report-on-time").textContent = report.summary.onTime;
  document.querySelector("#report-late").textContent = report.summary.late;
  document.querySelector("#report-absent").textContent = report.summary.absent;
  document.querySelector("#report-incomplete").textContent = report.summary.incomplete;
  document.querySelector("#attendance-report-export").disabled = false;
  document.querySelector("#report-trend-rows").innerHTML = report.dailyTrend.length
    ? report.dailyTrend.map(day => `<tr>
        <td>${escapeHtml(reportDisplayDate(day.date))}</td><td>${day.summary.total}</td>
        <td>${day.summary.onTime}</td><td>${day.summary.late}</td>
        <td>${day.summary.absent}</td><td>${day.summary.incomplete}</td>
        <td>${day.summary.approvedException}</td>
      </tr>`).join("")
    : `<tr><td colspan="7" class="empty-state">No dates in this report.</td></tr>`;
  document.querySelector("#report-employee-rows").innerHTML = report.employees.length
    ? report.employees.map(employee => `<tr>
        <td><div class="employee-cell"><span><strong>${escapeHtml(employee.employeeName)}</strong><small>${escapeHtml(employee.employeeNumber)}</small></span></div></td>
        <td>${escapeHtml(employee.department)}</td><td>${employee.total}</td>
        <td>${employee.onTime}</td><td>${employee.late}</td><td>${employee.absent}</td>
        <td>${employee.incomplete}</td><td>${employee.approvedException}</td>
      </tr>`).join("")
    : `<tr><td colspan="8" class="empty-state">No attendance records were found for this range.</td></tr>`;
}
async function loadAttendanceReport() {
  initializeReportDates();
  const params = new URLSearchParams({
    fromDate: document.querySelector("#report-from-date").value,
    toDate: document.querySelector("#report-to-date").value
  });
  const departmentId = document.querySelector("#report-department-filter").value;
  if (departmentId) params.set("departmentId", departmentId);
  const button = document.querySelector("#generate-attendance-report");
  button.disabled = true;
  try {
    const report = await apiRequest(`/api/attendance/reports?${params}`);
    renderAttendanceReport(report);
  } finally {
    button.disabled = false;
  }
}
function renderShiftPatterns() {
  document.querySelector("#shift-rows").innerHTML = shiftPatterns.length
    ? shiftPatterns.map(shift => `<tr><td><strong>${escapeHtml(shift.name)}</strong></td><td>${escapeHtml(shiftCategoryName(shift.category))}</td><td class="time-cell">${escapeHtml(shift.startTime.slice(0, 5))}–${escapeHtml(shift.endTime.slice(0, 5))}${shift.crossesMidnight ? " <small>+1 day</small>" : ""}</td><td>${shift.breakMinutes} min</td><td>${shift.graceMinutes} min</td><td>${shift.assignedEmployeeCount}</td><td>${badge(shift.isActive ? "Active" : "Inactive")}</td><td><button class="text-action edit-shift" data-id="${shift.id}">Edit</button><button class="text-action toggle-shift" data-id="${shift.id}" data-active="${shift.isActive}">${shift.isActive ? "Deactivate" : "Activate"}</button></td></tr>`).join("")
    : `<tr><td colspan="8" class="empty-state">No shift patterns have been created.</td></tr>`;
}
function renderShiftAssignments() {
  document.querySelector("#assignment-rows").innerHTML = shiftAssignments.length
    ? shiftAssignments.map(assignment => `<tr><td>${employeeCell({ name: assignment.employeeName, id: assignment.employeeNumber })}</td><td>${escapeHtml(assignment.department)}</td><td>${assignment.shiftName ? escapeHtml(assignment.shiftName) : badge("Unassigned")}</td><td>${assignment.effectiveFrom ? escapeHtml(assignment.effectiveFrom) : "—"}</td><td><button class="text-action assign-shift" data-id="${assignment.employeeId}">${assignment.shiftId ? "Change shift" : "Assign shift"} →</button></td></tr>`).join("")
    : `<tr><td colspan="5" class="empty-state">No active employees match this filter.</td></tr>`;
  document.querySelector("#assignment-result-count").textContent = `${shiftAssignments.length} active employees`;
}
async function loadSchedules() {
  const search = document.querySelector("#assignment-search").value.trim();
  const departmentId = document.querySelector("#assignment-department-filter").value;
  const params = new URLSearchParams();
  if (search) params.set("search", search);
  if (departmentId) params.set("departmentId", departmentId);
  const allAssignmentsPromise = apiRequest("/api/shifts/assignments");
  const assignmentUrl = params.size ? `/api/shifts/assignments?${params}` : null;
  const [shifts, allAssignments, assignments] = await Promise.all([
    apiRequest("/api/shifts"),
    allAssignmentsPromise,
    assignmentUrl ? apiRequest(assignmentUrl) : allAssignmentsPromise
  ]);
  shiftPatterns = shifts;
  shiftAssignments = assignments;
  document.querySelector("#shift-active-count").textContent = shifts.filter(shift => shift.isActive).length;
  const assignedCount = allAssignments.filter(assignment => assignment.shiftId).length;
  document.querySelector("#shift-assigned-count").textContent = assignedCount;
  document.querySelector("#shift-unassigned-count").textContent = allAssignments.length - assignedCount;
  renderShiftPatterns();
  renderShiftAssignments();
}
function openShiftForm(shift = null) {
  openModal({
    title: shift ? "Edit shift pattern" : "Create shift pattern",
    eyebrow: "SHIFTS & SCHEDULES",
    fields: `${textField("Shift name", "name", shift?.name || "")}${shiftCategoryField(shift?.category || "day")}${textField("Start time", "startTime", shift?.startTime.slice(0, 5) || "08:00", true, "time")}${textField("End time", "endTime", shift?.endTime.slice(0, 5) || "16:30", true, "time")}${textField("Unpaid break (minutes)", "breakMinutes", shift?.breakMinutes ?? 30, true, "number")}${textField("Late grace period (minutes)", "graceMinutes", shift?.graceMinutes ?? 5, true, "number")}${checkboxField("Overnight shift (ends the next day)", "crossesMidnight", shift?.crossesMidnight || false)}${shift ? checkboxField("Shift pattern is active", "isActive", shift.isActive) : ""}`,
    submitText: shift ? "Save shift" : "Create shift",
    onSubmit: async data => {
      const request = {
        name: data.name.trim(),
        category: data.category,
        startTime: data.startTime,
        endTime: data.endTime,
        breakMinutes: Number(data.breakMinutes),
        graceMinutes: Number(data.graceMinutes),
        crossesMidnight: data.crossesMidnight === "on",
        isActive: shift ? data.isActive === "on" : true
      };
      await apiRequest(shift ? `/api/shifts/${shift.id}` : "/api/shifts", {
        method: shift ? "PUT" : "POST",
        body: JSON.stringify(request)
      });
      notify(shift ? "Shift pattern updated." : "Shift pattern created.");
      await loadSchedules();
    }
  });
}
function openShiftAssignment(employeeId) {
  const employee = shiftAssignments.find(item => item.employeeId === employeeId);
  if (!employee) return;
  const activeShifts = shiftPatterns.filter(shift => shift.isActive);
  if (!activeShifts.length) {
    notify("Create or activate a shift pattern before assigning employees.", "error");
    return;
  }
  const options = activeShifts.map(shift => `<option value="${shift.id}" ${shift.id === employee.shiftId ? "selected" : ""}>${escapeHtml(shift.name)} · ${escapeHtml(shift.startTime.slice(0, 5))}–${escapeHtml(shift.endTime.slice(0, 5))}</option>`).join("");
  openModal({
    title: `Assign shift · ${employee.employeeName}`,
    eyebrow: `${employee.employeeNumber} · ${employee.department}`,
    fields: `<div class="form-field full"><label for="field-shiftId">Recurring shift *</label><select id="field-shiftId" name="shiftId" required>${options}</select></div><p class="modal-copy form-field full">This recurring assignment starts today. The employee's previous assignment will be retained in the history.</p>`,
    submitText: "Save assignment",
    onSubmit: async data => {
      await apiRequest(`/api/shifts/assignments/${employee.employeeId}`, {
        method: "PUT",
        body: JSON.stringify({ shiftId: data.shiftId })
      });
      notify("Recurring shift assignment saved.");
      await loadSchedules();
    }
  });
}
async function toggleShiftActive(shiftId, active) {
  const shift = shiftPatterns.find(item => item.id === shiftId);
  if (!shift) return;
  if (!active) {
    await apiRequest(`/api/shifts/${shiftId}`, { method: "DELETE" });
    notify("Shift pattern deactivated.");
  } else {
    await apiRequest(`/api/shifts/${shiftId}`, {
      method: "PUT",
      body: JSON.stringify({
        name: shift.name,
        category: shift.category,
        startTime: shift.startTime.slice(0, 5),
        endTime: shift.endTime.slice(0, 5),
        breakMinutes: shift.breakMinutes,
        graceMinutes: shift.graceMinutes,
        crossesMidnight: shift.crossesMidnight,
        isActive: true
      })
    });
    notify("Shift pattern activated.");
  }
  await loadSchedules();
}
function renderAccess() {
  const query = document.querySelector("#access-search").value.trim().toLowerCase();
  const status = document.querySelector("#access-status-filter").value;
  const direction = document.querySelector("#access-direction-filter").value;
  const filtered = accessEvents.filter(row => (!query || `${row.employee} ${row.id}`.toLowerCase().includes(query)) && (!status || row.decision === status) && (!direction || row.direction === direction) && (!reviewFilterOnly || row.decision === "Needs review"));
  document.querySelector("#access-rows").innerHTML = filtered.length ? filtered.map(row => `<tr><td>${employeeCell(row)}</td><td class="time-cell">${row.time}</td><td>${row.direction === "Entry" ? "↘ Entry" : "↗ Exit"}</td><td><span class="access-method ${row.identity === "Verified" ? "check-ok" : "check-inconclusive"}">${row.identity === "Verified" ? "✓ " : "◷ "}${row.identity}</span></td><td><span class="${row.breath === "Clear" ? "check-ok" : row.breath === "Review" ? "check-inconclusive" : ""}">${row.breath === "Clear" ? "✓ " : row.breath === "Review" ? "◷ " : ""}${row.breath}</span></td><td><span class="${row.metal === "Clear" ? "check-ok" : row.metal === "Alert" ? "check-inconclusive" : ""}">${row.metal === "Clear" ? "✓ " : row.metal === "Alert" ? "! " : ""}${row.metal}</span></td><td>${badge(row.decision)}</td><td>${row.decision === "Needs review" ? `<button class="text-action review-event" data-id="${row.id}">Review →</button>` : ""}</td></tr>`).join("") : `<tr><td colspan="8" class="empty-state">No access events match these filters.</td></tr>`;
  document.querySelector("#access-result-count").textContent = filtered.length ? `Showing 1–${filtered.length} of ${filtered.length} events` : "No events found";
}
function formatWorkedMinutes(minutes) {
  if (minutes === null || minutes === undefined) return "—";
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  return hours ? `${hours}h ${String(remainder).padStart(2, "0")}m` : `${remainder}m`;
}
function employeeAttendanceRow(record) {
  const date = new Date(`${record.shiftDate}T00:00:00`).toLocaleDateString();
  return `<tr><td>${escapeHtml(date)}</td><td class="time-cell">${escapeHtml(record.scheduledStart.slice(0, 5))}</td><td>${escapeHtml(record.clockIn?.slice(0, 5) || "—")}</td><td>${escapeHtml(record.clockOut?.slice(0, 5) || "—")}</td><td>${formatWorkedMinutes(record.workedMinutes)}</td><td>${badge(attendanceStatusLabel(record.status))}</td></tr>`;
}
function renderEmployeePortal(portal) {
  api.employeePortal = portal;
  const initials = portal.employeeName.split(/\s+/).slice(0, 2).map(part => part[0]).join("").toUpperCase();
  document.querySelector("#employee-portal-avatar").textContent = initials;
  document.querySelector("#employee-portal-name").textContent = `Welcome back, ${portal.employeeName}`;
  document.querySelector("#employee-portal-description").textContent =
    `${portal.employeeNumber} · ${portal.department} · ${new Date(`${portal.today}T00:00:00`).toLocaleDateString()}`;

  const shift = portal.assignedShift;
  document.querySelector("#employee-portal-shift").textContent = shift?.name || "No shift assigned";
  document.querySelector("#employee-portal-shift-time").textContent = shift
    ? `${shift.startTime.slice(0, 5)}–${shift.endTime.slice(0, 5)}${shift.crossesMidnight ? " (overnight)" : ""} · ${shift.breakMinutes} min break`
    : "Contact HR to confirm your schedule.";

  const today = portal.todayAttendance;
  const statusElement = document.querySelector("#employee-portal-status");
  statusElement.textContent = today ? attendanceStatusLabel(today.status) : "No record yet";
  statusElement.className = today?.status === "on_time" ? "summary-green" : "";
  document.querySelector("#employee-portal-times").textContent = today
    ? `Clock in ${today.clockIn?.slice(0, 5) || "—"} · Clock out ${today.clockOut?.slice(0, 5) || "—"}`
    : "Your attendance has not been recorded today.";
  document.querySelector("#employee-portal-week-hours").textContent = formatWorkedMinutes(portal.workedMinutesThisWeek);
  document.querySelector("#employee-recent-attendance").innerHTML = portal.recentAttendance.length
    ? portal.recentAttendance.map(record =>
        `<div class="personal-day"><span>${escapeHtml(new Date(`${record.shiftDate}T00:00:00`).toLocaleDateString())}</span><strong>${escapeHtml(record.clockIn?.slice(0, 5) || "—")}–${escapeHtml(record.clockOut?.slice(0, 5) || "—")}</strong><span>${formatWorkedMinutes(record.workedMinutes)}</span>${badge(attendanceStatusLabel(record.status))}</div>`
      ).join("")
    : `<div class="personal-day"><span>No attendance records yet.</span></div>`;
}
async function loadEmployeePortal() {
  const portal = await apiRequest("/api/me/portal");
  renderEmployeePortal(portal);
}
function renderEmployeeAttendancePage(response) {
  api.employeeAttendanceTotal = response.total;
  document.querySelector("#employee-attendance-rows").innerHTML = response.items.length
    ? response.items.map(employeeAttendanceRow).join("")
    : `<tr><td colspan="6" class="empty-state">You don't have any saved attendance records yet.</td></tr>`;
  const start = response.total ? (response.page - 1) * response.pageSize + 1 : 0;
  const end = Math.min(response.page * response.pageSize, response.total);
  document.querySelector("#employee-attendance-result-count").textContent =
    response.total ? `Showing ${start}–${end} of ${response.total} records` : "No attendance records";
  const pageCount = Math.max(1, Math.ceil(response.total / response.pageSize));
  document.querySelector("#employee-attendance-pagination").innerHTML = `
    <button class="page-button" data-employee-attendance-page="${response.page - 1}" aria-label="Previous page" ${response.page <= 1 ? "disabled" : ""}>‹</button>
    <span class="summary-label">Page ${response.page} of ${pageCount}</span>
    <button class="page-button" data-employee-attendance-page="${response.page + 1}" aria-label="Next page" ${response.page >= pageCount ? "disabled" : ""}>›</button>`;
}
async function loadEmployeeAttendance() {
  const params = new URLSearchParams({
    page: String(api.employeeAttendancePage),
    pageSize: String(api.employeeAttendancePageSize)
  });
  const response = await apiRequest(`/api/me/attendance?${params}`);
  renderEmployeeAttendancePage(response);
}
function initializeAuditDates() {
  const to = new Date();
  const from = new Date(to);
  from.setDate(from.getDate() - 29);
  if (!document.querySelector("#audit-from-date").value)
    document.querySelector("#audit-from-date").value = reportDateValue(from);
  if (!document.querySelector("#audit-to-date").value)
    document.querySelector("#audit-to-date").value = reportDateValue(to);
}
function renderAuditLogs(response) {
  api.auditLogs = response.items;
  document.querySelector("#audit-range-label").textContent =
    `${response.fromDate} to ${response.toDate}`;
  document.querySelector("#audit-total").textContent = `${response.total} records`;
  document.querySelector("#audit-export").disabled = response.items.length === 0;
  document.querySelector("#audit-log-rows").innerHTML = response.items.length
    ? response.items.map(log => `<tr>
        <td>${escapeHtml(new Date(log.createdAt).toLocaleString())}</td>
        <td>${escapeHtml(log.actorEmail || "System")}</td>
        <td><strong>${escapeHtml(log.action)}</strong></td>
        <td>${escapeHtml(log.entityType)}<small>${escapeHtml(log.entityId)}</small></td>
        <td>${log.changes ? `<details><summary>View changes</summary><pre>${escapeHtml(log.changes)}</pre></details>` : ""}
          ${log.reason ? `<small>${escapeHtml(log.reason)}</small>` : ""}</td>
        <td>${escapeHtml(log.ipAddress || "—")}</td>
      </tr>`).join("")
    : `<tr><td colspan="6" class="empty-state">No audit entries match these filters.</td></tr>`;
  const pageCount = Math.max(1, Math.ceil(response.total / response.pageSize));
  document.querySelector("#audit-result-count").textContent =
    response.total
      ? `Showing ${(response.page - 1) * response.pageSize + 1}–${Math.min(response.page * response.pageSize, response.total)} of ${response.total} records`
      : "No audit records found";
  document.querySelector("#audit-pagination").innerHTML = `
    <button class="page-button" data-audit-page="${response.page - 1}" aria-label="Previous page" ${response.page <= 1 ? "disabled" : ""}>‹</button>
    <span class="summary-label">Page ${response.page} of ${pageCount}</span>
    <button class="page-button" data-audit-page="${response.page + 1}" aria-label="Next page" ${response.page >= pageCount ? "disabled" : ""}>›</button>`;
}
async function loadAuditLogs() {
  initializeAuditDates();
  const params = new URLSearchParams({
    fromDate: document.querySelector("#audit-from-date").value,
    toDate: document.querySelector("#audit-to-date").value,
    page: String(api.auditPage),
    pageSize: String(api.auditPageSize)
  });
  const entityType = document.querySelector("#audit-entity-filter").value;
  const search = document.querySelector("#audit-search").value.trim();
  if (entityType) params.set("entityType", entityType);
  if (search) params.set("search", search);
  const button = document.querySelector("#generate-audit-report");
  button.disabled = true;
  try {
    const response = await apiRequest(`/api/audit-logs?${params}`);
    renderAuditLogs(response);
  } finally {
    button.disabled = false;
  }
}
function downloadAuditPage() {
  const columns = ["Timestamp", "Actor", "Action", "Record type", "Record ID", "Changes", "Reason", "Source IP"];
  const rows = api.auditLogs.map(log => [
    log.createdAt, log.actorEmail, log.action, log.entityType, log.entityId,
    log.changes, log.reason, log.ipAddress
  ]);
  const csv = [columns, ...rows].map(row => row.map(csvCell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `audit-log-page-${document.querySelector("#audit-from-date").value}-to-${document.querySelector("#audit-to-date").value}.csv`;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}
function showPage(name) {
  if (!pages.includes(name)) return;
  const accessible = [...document.querySelectorAll(".nav-item[data-page]")].find(button => button.dataset.page === name);
  if (accessible && accessible.hidden) {
    notify(`This page is not available to the ${currentRole} role.`, "error");
    return;
  }
  document.querySelectorAll(".page-section").forEach(section => section.classList.toggle("current-page", section.id === `page-${name}`));
  document.querySelectorAll(".nav-item[data-page]").forEach(button => button.classList.toggle("active", button.dataset.page === name));
  const labels = { dashboard: "Overview", users: "User management", employees: "Employees", attendance: "Attendance", "my-attendance": "My attendance", reports: "Attendance reports", "audit-logs": "Audit logs", shifts: "Shifts & schedules", access: "Access & screening" };
  document.querySelector("#breadcrumb-current").textContent = labels[name];
  document.querySelector("#sidebar").classList.remove("open");
  if (name === "attendance") loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error"));
  if (name === "my-attendance") loadEmployeeAttendance().catch(error => notify(`Could not load your attendance: ${error.message}`, "error"));
  if (name === "reports") loadAttendanceReport().catch(error => notify(`Could not load attendance report: ${error.message}`, "error"));
  if (name === "audit-logs") loadAuditLogs().catch(error => notify(`Could not load audit logs: ${error.message}`, "error"));
  if (name === "shifts") loadSchedules().catch(error => notify(`Could not load shifts and schedules: ${error.message}`, "error"));
  if (name === "users") loadAdminUsers().catch(error => notify(`Could not load user accounts: ${error.message}`, "error"));
}
function notify(message, type = "success") {
  const toast = document.createElement("div");
  toast.className = `toast ${type}`;
  toast.textContent = message;
  document.querySelector("#toast-region").appendChild(toast);
  window.setTimeout(() => toast.remove(), 3200);
}
function openModal({ title, eyebrow = "EMPLOYEE RECORD", fields, submitText = "Save changes", onSubmit }) {
  document.querySelector("#modal-title").textContent = title;
  document.querySelector("#modal-eyebrow").textContent = eyebrow;
  const form = document.querySelector("#modal-form");
  form.innerHTML = `<div class="form-grid">${fields}</div><div class="modal-actions"><button type="button" class="button button-outline" id="form-cancel">Cancel</button><button type="submit" class="button button-primary">${submitText}</button></div>`;
  document.querySelector("#modal-backdrop").classList.remove("hidden");
  document.querySelector("#form-cancel").addEventListener("click", closeModal);
  form.onsubmit = async event => {
    event.preventDefault();
    const data = Object.fromEntries(new FormData(form).entries());
    data.roles = new FormData(form).getAll("roles");
    const submitButton = form.querySelector('button[type="submit"]');
    submitButton.disabled = true;
    try {
      if (await onSubmit(data) !== false) closeModal();
    } catch (error) {
      notify(error.message, "error");
    } finally {
      submitButton.disabled = false;
    }
  };
  const firstInput = form.querySelector("input,select,textarea");
  if (firstInput) firstInput.focus();
}
function closeModal() {
  document.querySelector("#modal-backdrop").classList.add("hidden");
}
function textField(label, name, value = "", required = true, type = "text") {
  return `<div class="form-field"><label for="field-${name}">${escapeHtml(label)}${required ? " *" : ""}</label><input id="field-${name}" name="${name}" type="${type}" value="${escapeHtml(value)}" ${required ? "required" : ""}></div>`;
}
function passwordField(label, name, required) {
  return `<div class="form-field"><label for="field-${name}">${escapeHtml(label)}${required ? " *" : ""}</label><input id="field-${name}" name="${name}" type="password" autocomplete="new-password" minlength="12" ${required ? "required" : ""}><small>At least 12 characters.</small></div>`;
}
function selectField(label, name, options, value = "") {
  return `<div class="form-field"><label for="field-${name}">${escapeHtml(label)} *</label><select id="field-${name}" name="${name}" required>${options.map(option => `<option value="${escapeHtml(option)}" ${option === value ? "selected" : ""}>${escapeHtml(displayStatus(option))}</option>`).join("")}</select></div>`;
}
function roleCheckboxFields(selectedRoles = []) {
  return `<fieldset class="form-field full role-checkboxes"><legend>Assigned roles *</legend>${api.adminRoles.map(role => `<label class="remember-option"><input type="checkbox" name="roles" value="${escapeHtml(role)}" ${selectedRoles.includes(role) ? "checked" : ""}> ${escapeHtml(role)}</label>`).join("")}</fieldset>`;
}
async function loadAdminUsers() {
  const [users, roles, employees] = await Promise.all([
    apiRequest("/api/admin/users"),
    apiRequest("/api/admin/roles"),
    apiRequest("/api/admin/employees")
  ]);
  api.adminUsers = users;
  api.adminRoles = roles;
  api.adminEmployees = employees;
  renderAdminUsers();
}
function renderAdminUsers() {
  const query = document.querySelector("#admin-user-search").value.trim().toLocaleLowerCase();
  const filtered = api.adminUsers.filter(user =>
    `${user.email} ${user.employeeName || ""} ${user.roles.join(" ")}`.toLocaleLowerCase().includes(query));
  document.querySelector("#admin-user-count").textContent = `${filtered.length} of ${api.adminUsers.length} accounts`;
  document.querySelector("#admin-user-rows").innerHTML = filtered.length
    ? filtered.map(user => `<tr>
        <td><strong>${escapeHtml(user.email)}</strong></td>
        <td>${escapeHtml(user.employeeName || "—")}</td>
        <td>${user.roles.map(role => `<span class="role-pill">${escapeHtml(role)}</span>`).join(" ")}</td>
        <td>${badge(user.isActive ? "Active" : "Inactive")}</td>
        <td>${user.lastLoginAt ? escapeHtml(new Date(user.lastLoginAt).toLocaleString()) : "Never"}</td>
        <td><button class="text-action edit-admin-user" data-id="${escapeHtml(user.id)}">Edit access</button><button class="text-action reset-admin-password" data-id="${escapeHtml(user.id)}" ${user.isActive ? "" : "disabled"}>Reset password</button></td>
      </tr>`).join("")
    : `<tr><td colspan="6" class="empty-state">No accounts match your search.</td></tr>`;
}
async function openAdminUserForm(userId = null) {
  const user = userId ? api.adminUsers.find(account => account.id === userId) : null;
  if (userId && !user) throw new Error("That account could not be found.");
  const employeeOptions = `<div class="form-field full"><label for="field-employeeId">Link employee (optional)</label><select id="field-employeeId" name="employeeId"><option value="">No employee link</option>${api.adminEmployees.map(employee => `<option value="${escapeHtml(employee.id)}">${escapeHtml(employee.fullName)} · ${escapeHtml(employee.email)}</option>`).join("")}</select></div>`;
  openModal({
    title: user ? "Edit account access" : "Create user account",
    eyebrow: "SYSTEM ADMINISTRATION",
    fields: user
      ? `<div class="form-field"><label for="field-email">Email address</label><input id="field-email" type="email" value="${escapeHtml(user.email)}" disabled></div><p class="modal-copy form-field full">Email addresses are managed from the employee record when linked to an employee.</p>${roleCheckboxFields(user.roles)}<div class="form-field full"><label class="remember-option"><input type="checkbox" name="isActive" ${user.isActive ? "checked" : ""}> Account is active and may sign in</label></div>`
      : `${textField("Email address", "email", "", true, "email")}${employeeOptions}${passwordField("Temporary password", "temporaryPassword", true)}${passwordField("Confirm temporary password", "confirmPassword", true)}<p class="modal-copy form-field full">The user must change this password at first sign-in. Employee and Line Manager roles require a linked employee record.</p>${roleCheckboxFields()}`,
    submitText: user ? "Save account access" : "Create account",
    onSubmit: async data => {
      if (!data.roles.length) throw new Error("Assign at least one role.");
      if (user) {
        await apiRequest(`/api/admin/users/${encodeURIComponent(user.id)}`, {
          method: "PUT",
          body: JSON.stringify({ roles: data.roles, isActive: data.isActive === "on" })
        });
        notify("Account access updated.");
      } else {
        if (data.temporaryPassword !== data.confirmPassword)
          throw new Error("The temporary passwords do not match.");
        await apiRequest("/api/admin/users", {
          method: "POST",
          body: JSON.stringify({
            email: data.email,
            employeeId: data.employeeId || null,
            temporaryPassword: data.temporaryPassword,
            roles: data.roles
          })
        });
        notify("Account created. Share the temporary password securely.");
      }
      await loadAdminUsers();
    }
  });
  if (!user && api.adminEmployees.length)
    document.querySelector("#field-employeeId").addEventListener("change", event => {
      const employee = api.adminEmployees.find(item => item.id === event.target.value);
      if (employee && !document.querySelector("#field-email").value)
        document.querySelector("#field-email").value = employee.email;
    });
}
function openAdminPasswordReset(userId) {
  const user = api.adminUsers.find(account => account.id === userId);
  if (!user) throw new Error("That account could not be found.");
  openModal({
    title: "Set a temporary password",
    eyebrow: user.email,
    fields: `${passwordField("Temporary password", "temporaryPassword", true)}${passwordField("Confirm temporary password", "confirmPassword", true)}<p class="modal-copy form-field full">The account holder must choose a new password at their next sign-in.</p>`,
    submitText: "Set temporary password",
    onSubmit: async data => {
      if (data.temporaryPassword !== data.confirmPassword)
        throw new Error("The temporary passwords do not match.");
      await apiRequest(`/api/admin/users/${encodeURIComponent(user.id)}/temporary-password`, {
        method: "POST",
        body: JSON.stringify({ temporaryPassword: data.temporaryPassword })
      });
      notify("Temporary password set. Share it securely with the account holder.");
    }
  });
}
function lookupField(label, name, items, selectedId = "", optional = false) {
  const emptyOption = optional ? `<option value="">No manager</option>` : "";
  return `<div class="form-field"><label for="field-${name}">${escapeHtml(label)}${optional ? "" : " *"}</label><select id="field-${name}" name="${name}" ${optional ? "" : "required"}>${emptyOption}${items.map(item => `<option value="${escapeHtml(item.id)}" ${item.id === selectedId ? "selected" : ""}>${escapeHtml(item.name)}</option>`).join("")}</select></div>`;
}
async function openEmployeeForm(employeeId = null) {
  const employee = employeeId
    ? api.demoMode
      ? demoEmployees.find(item => item.id === employeeId)
      : await apiRequest(`/api/employees/${encodeURIComponent(employeeId)}`)
    : null;
  if (employeeId && !employee) throw new Error("That employee could not be found.");
  openModal({
    title: employee ? "Edit employee" : "Add employee",
    fields: `${textField("First name", "firstName", employee?.firstName || "")}${textField("Last name", "lastName", employee?.lastName || "")}${textField("Employee number", "employeeNumber", employee?.employeeNumber || "")}${textField("Email address", "email", employee?.email || "", true, "email")}${textField("Job title", "jobTitle", employee?.jobTitle || "")}${lookupField("Site", "siteId", api.lookups.sites, employee?.siteId || "")}${lookupField("Department", "departmentId", api.lookups.departments, employee?.departmentId || "")}${lookupField("Assigned shift", "shiftId", api.lookups.shifts, employee?.shiftId || "")}${selectField("Employment status", "employmentStatus", api.lookups.employmentStatuses, employee?.employmentStatus || "active")}${textField("Hire date", "hireDate", employee?.hireDate || "", false, "date")}${api.demoMode ? "" : `<p class="modal-copy form-field full">${employee ? "Set a temporary login password for this employee. Leave both password fields blank to keep their current login unchanged." : "Create a login account with a temporary password. Give it to the employee securely; they must change it when they first sign in."}</p>`}${api.demoMode ? "" : passwordField("Temporary password", "initialPassword", !employee)}${api.demoMode ? "" : passwordField(employee ? "Confirm temporary password" : "Confirm temporary password", "confirmPassword", !employee)}`,
    submitText: employee ? "Save employee" : "Add employee",
    onSubmit: async data => {
      if (!api.demoMode && (data.initialPassword || data.confirmPassword) &&
          (!data.initialPassword || data.initialPassword !== data.confirmPassword)) {
        throw new Error("Enter and confirm the same temporary password.");
      }
      const request = {
        employeeNumber: data.employeeNumber,
        firstName: data.firstName,
        lastName: data.lastName,
        email: data.email,
        jobTitle: data.jobTitle,
        siteId: data.siteId,
        departmentId: data.departmentId,
        shiftId: data.shiftId,
        managerId: employee?.managerId || null,
        employmentStatus: data.employmentStatus,
        hireDate: data.hireDate || null
      };
      if (api.demoMode) {
        const duplicate = demoEmployees.some(item =>
          item.id !== employee?.id &&
          (item.employeeNumber.toLocaleLowerCase() === request.employeeNumber.toLocaleLowerCase() ||
            item.email.toLocaleLowerCase() === request.email.toLocaleLowerCase()));
        if (duplicate) throw new Error("Employee number and email must be unique in the demo directory.");
        const department = api.lookups.departments.find(item => item.id === request.departmentId);
        const site = api.lookups.sites.find(item => item.id === request.siteId);
        const shift = api.lookups.shifts.find(item => item.id === request.shiftId);
        const updated = {
          ...employee,
          ...request,
          id: employee?.id || `demo-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
          fullName: `${request.firstName} ${request.lastName}`,
          department: department.name,
          site: site.name,
          shift: shift.name
        };
        if (employee) {
          demoEmployees = demoEmployees.map(item => item.id === employee.id ? updated : item);
        } else {
          demoEmployees.unshift(updated);
        }
        api.page = employee ? api.page : 1;
        renderEmployees();
        await loadEmployeeSummary();
        notify(employee ? "Demo employee updated in this browser only." : "Demo employee added in this browser only.");
        return;
      }
      await apiRequest(employee ? `/api/employees/${encodeURIComponent(employee.id)}` : "/api/employees", {
        method: employee ? "PUT" : "POST",
        body: JSON.stringify(employee
          ? request
          : { ...request, initialPassword: data.initialPassword })
      });
      if (employee && data.initialPassword) {
        await apiRequest(`/api/employees/${encodeURIComponent(employee.id)}/account`, {
          method: "POST",
          body: JSON.stringify({ temporaryPassword: data.initialPassword })
        });
      }
      api.page = employee ? api.page : 1;
      renderEmployees();
      loadEmployeeSummary().catch(error => notify(`Employee saved, but totals could not refresh: ${error.message}`, "error"));
      notify(employee
        ? data.initialPassword ? "Employee details saved and temporary login password set." : "Employee details updated."
        : "Employee and login account created. Share the temporary password securely.");
    }
  });
}
function openAttendanceEdit(row) {
  openModal({
    title: `Review attendance · ${row.employeeName}`,
    eyebrow: `${row.employeeNumber} · ${document.querySelector("#attendance-date").value}`,
    fields: `<p class="modal-copy form-field full">Any manual adjustment is recorded with your name and reason.</p>${textField("Scheduled start", "scheduled", row.scheduledStart.slice(0, 5), true, "time")}${textField("Clock-in time", "in", row.clockIn?.slice(0, 5) || "", false, "time")}${textField("Clock-out time", "out", row.clockOut?.slice(0, 5) || "", false, "time")}<div class="form-field"><label for="field-status">Attendance status *</label><select id="field-status" name="status" required>${Object.entries(attendanceStatuses).map(([value, label]) => `<option value="${value}" ${row.status === value ? "selected" : ""}>${label}</option>`).join("")}</select></div><div class="form-field full"><label for="field-reason">Reason for adjustment *</label><textarea id="field-reason" name="reason" required minlength="8" maxlength="500" placeholder="Explain why this record needs to be changed..."></textarea></div>`,
    submitText: "Save with audit record",
    onSubmit: async data => {
      if (data.reason.trim().length < 8) {
        notify("Please provide a reason of at least 8 characters.", "error");
        return false;
      }
      await apiRequest(`/api/attendance/${row.id}`, {
        method: "PUT",
        body: JSON.stringify({
          scheduledStart: data.scheduled,
          clockIn: data.in || null,
          clockOut: data.out || null,
          status: data.status,
          reason: data.reason.trim()
        })
      });
      notify("Attendance updated and recorded in the audit trail.");
      await loadAttendance().catch(error => notify(`Attendance saved, but the list could not refresh: ${error.message}`, "error"));
    }
  });
}
function openAccessReview(row) {
  openModal({
    title: `Review access event · ${row.employee}`,
    eyebrow: `${row.id} · ${row.time} · ${row.direction.toUpperCase()}`,
    fields: `<p class="modal-copy form-field full">${row.review} An inconclusive result is not a confirmed violation. Record the human review outcome below.</p>${selectField("Review outcome", "outcome", ["Access granted after review", "Access denied after review", "Escalate for further review"])}<div class="form-field full"><label for="field-review-note">Review notes *</label><textarea id="field-review-note" name="notes" required minlength="8" placeholder="Record the checks performed and reason for your decision..."></textarea></div>`,
    submitText: "Record review outcome",
    onSubmit: data => {
      if (data.notes.trim().length < 8) {
        notify("Please add review notes of at least 8 characters.", "error");
        return false;
      }
      row.decision = data.outcome.startsWith("Access granted") ? "Granted" : data.outcome.startsWith("Access denied") ? "Denied" : "Needs review";
      row.review = `${data.outcome}: ${data.notes}`;
      renderAccess();
      notify(api.demoMode ? "Demo review updated in this browser only." : "Review outcome recorded for this event.");
    }
  });
}

function setLoginMessage(message = "", isSuccess = false) {
  const element = document.querySelector("#login-message");
  element.textContent = message;
  element.classList.toggle("success", isSuccess);
}

function showPasswordSetup(user) {
  pendingPasswordChangeUser = user;
  api.user = user;
  api.demoMode = false;
  document.querySelector("#app-shell").hidden = true;
  document.querySelector("#login-screen").hidden = true;
  document.querySelector("#password-setup-screen").hidden = false;
  document.querySelector("#password-setup-copy").textContent =
    `Your administrator provided a temporary password for ${user.email}. Set a new password to continue.`;
  document.querySelector("#password-setup-message").textContent = "";
  document.querySelector("#temporary-password").focus();
}

async function routeAuthenticatedUser(user) {
  if (user.mustChangePassword) {
    showPasswordSetup(user);
    return;
  }
  pendingPasswordChangeUser = null;
  document.querySelector("#password-setup-screen").hidden = true;
  await showAuthenticatedApp(user);
}

async function showAuthenticatedApp(user) {
  api.user = user;
  document.querySelector("#demo-banner").classList.toggle("hidden", !api.demoMode);
  currentRole = user.roles.includes("System Administrator")
    ? "System Administrator"
    : user.roles[0] || "";
  const displayName = user.fullName || user.email;
  const initials = displayName.split(/\s+/).slice(0, 2).map(part => part[0]).join("").toUpperCase();
  document.querySelector("#login-screen").hidden = true;
  document.querySelector("#password-setup-screen").hidden = true;
  document.querySelector("#app-shell").hidden = false;
  document.querySelector("#header-user-name").textContent = displayName;
  document.querySelector("#sidebar-user-name").textContent = displayName;
  document.querySelector("#sidebar-avatar").textContent = initials;
  document.querySelector("#account-menu-name").textContent = displayName;
  document.querySelector("#header-avatar").textContent = initials;
  document.querySelector("#header-role").textContent = currentRole;
  document.querySelector("#sidebar-role").textContent = currentRole;
  document.querySelector("#account-menu-role").textContent = currentRole;
  document.querySelector("#today-label").innerHTML = `<span class="today-dot"></span>${new Intl.DateTimeFormat(undefined, { weekday: "long", day: "numeric", month: "long", year: "numeric" }).format(new Date())}`;
  document.querySelectorAll(".nav-item[data-roles]").forEach(button => {
    button.hidden = !user.roles.some(role => button.dataset.roles.split(",").includes(role));
  });

  const canViewEmployees = user.roles.some(role => ["System Administrator", "HR Officer", "Line Manager"].includes(role));
  const canManageEmployees = user.roles.some(role => ["System Administrator", "HR Officer"].includes(role));
  const isAdministrator = user.roles.includes("System Administrator");
  const isManagerPortal = user.roles.includes("Line Manager") && !canManageEmployees;
  const isEmployeePortal = user.roles.includes("Employee") && !canViewEmployees;
  const isAuditorPortal = user.roles.includes("Auditor") && !canViewEmployees;
  document.querySelector("#admin-dashboard").classList.toggle("hidden", !isAdministrator);
  document.querySelector("#employee-dashboard").classList.toggle("hidden", !isEmployeePortal);
  document.querySelector("#auditor-dashboard").classList.toggle("hidden", !isAuditorPortal);
  document.querySelector("#manager-dashboard").classList.toggle("hidden", !isManagerPortal);
  document.querySelector("#standard-dashboard").classList.toggle("hidden", isAdministrator || isEmployeePortal || isAuditorPortal || isManagerPortal);
  document.querySelector("#dashboard-main-actions").classList.toggle("hidden", isEmployeePortal || isAuditorPortal || isManagerPortal);
  document.querySelector("#dashboard-title").textContent = isEmployeePortal
    ? "Your workplace"
    : isAuditorPortal ? "Audit and compliance"
      : isManagerPortal ? "Your team" : "Workplace overview";
  document.querySelector("#dashboard-eyebrow").textContent = isEmployeePortal
    ? "EMPLOYEE WORKSPACE"
    : isAuditorPortal ? "AUDITOR WORKSPACE"
      : isManagerPortal ? "LINE MANAGER WORKSPACE" : "WORKPLACE OVERVIEW · PREVIEW";
  document.querySelector("#dashboard-description").textContent = isEmployeePortal
    ? "Your assigned shift and personal attendance."
    : isAuditorPortal ? "Review records and recorded system changes without modifying them."
      : isManagerPortal ? "Attendance and follow-up for your direct reports."
      : "A preview of planned attendance and operations features.";
  if (isManagerPortal)
    document.querySelector("#page-reports .page-heading p").textContent =
      "Review attendance trends and exceptions for your direct reports.";
  document.querySelector("#add-employee").hidden = !canManageEmployees;
  document.querySelector("#employee-export").textContent = "↓  Export page";
  document.querySelector("#management-caption").hidden = isEmployeePortal;
  document.querySelector("#management-nav").hidden = isEmployeePortal;
  document.querySelector("#settings-link").hidden = isEmployeePortal || !isAdministrator;
  document.querySelector("#employee-access-notice").classList.toggle("current-page", !canViewEmployees && !isEmployeePortal && !isAuditorPortal);
  if (!canViewEmployees && !isEmployeePortal && !isAuditorPortal) {
    document.querySelectorAll(".page-section").forEach(section => {
      if (section.id !== "employee-access-notice") section.classList.remove("current-page");
    });
    document.querySelector("#breadcrumb-current").textContent = "Account";
    return;
  }

  document.querySelector("#employee-access-notice").classList.remove("current-page");
  if (isEmployeePortal) {
    showPage("dashboard");
    try { await loadEmployeePortal(); }
    catch (error) { notify(`Could not load your employee information: ${error.message}`, "error"); }
    return;
  }
  if (isAuditorPortal) {
    showPage("dashboard");
    try { await loadAuditLogs(); }
    catch (error) { notify(`Could not load audit logs: ${error.message}`, "error"); }
    return;
  }

  showPage(api.demoMode || isAdministrator || isManagerPortal ? "dashboard" : "employees");
  renderLive();
  renderAccess();
  try { await loadLookups(); }
  catch (error) { notify(`Could not load directory filters: ${error.message}`, "error"); }
  try { await loadEmployeeSummary(); }
  catch (error) { notify(`Could not load employee totals: ${error.message}`, "error"); }
  if (user.roles.some(role => ["System Administrator", "HR Officer", "Line Manager"].includes(role))) {
    try { await loadAttendance(); }
    catch (error) { notify(`Could not load attendance totals: ${error.message}`, "error"); }
  }
  if (isAdministrator) {
    try { await loadAdminUsers(); }
    catch (error) { notify(`Could not load user accounts: ${error.message}`, "error"); }
  }
  renderEmployees();
}

function showLogin(message = "") {
  api.user = null;
  api.demoMode = false;
  pendingPasswordChangeUser = null;
  document.querySelector("#demo-banner").classList.add("hidden");
  document.querySelector("#app-shell").hidden = true;
  document.querySelector("#password-setup-screen").hidden = true;
  document.querySelector("#login-screen").hidden = false;
  setLoginMessage(message);
}

function csvCell(value) {
  const text = String(value ?? "");
  const safeText = /^[=+\-@]/.test(text) ? `'${text}` : text;
  return `"${safeText.replaceAll('"', '""')}"`;
}

function downloadEmployeePage() {
  const columns = ["Employee number", "Name", "Email", "Department", "Site", "Job title", "Shift", "Status"];
  const rows = api.employees.map(row => [
    row.employeeNumber, row.fullName, row.email, row.department, row.site, row.jobTitle, row.shift || "", displayStatus(row.employmentStatus)
  ]);
  const csv = [columns, ...rows].map(row => row.map(csvCell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = "employee-directory-page.csv";
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

function downloadAttendanceReport() {
  if (!api.attendanceReport) {
    notify("Generate an attendance report before exporting.", "error");
    return;
  }
  const report = api.attendanceReport;
  const rows = [
    ["Attendance report", report.fromDate, report.toDate],
    [],
    ["Daily trend"],
    ["Date", "Total", "On time", "Late", "Absent", "Incomplete", "Approved exception"],
    ...report.dailyTrend.map(day => [
      day.date, day.summary.total, day.summary.onTime, day.summary.late,
      day.summary.absent, day.summary.incomplete, day.summary.approvedException
    ]),
    [],
    ["Employee summary"],
    ["Employee number", "Name", "Department", "Records", "On time", "Late", "Absent", "Incomplete", "Approved exception"],
    ...report.employees.map(employee => [
      employee.employeeNumber, employee.employeeName, employee.department, employee.total,
      employee.onTime, employee.late, employee.absent, employee.incomplete, employee.approvedException
    ])
  ];
  const csv = rows.map(row => row.map(csvCell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `attendance-report-${report.fromDate}-to-${report.toDate}.csv`;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

document.querySelectorAll(".nav-item[data-page], [data-go]").forEach(button => button.addEventListener("click", () => showPage(button.dataset.page || button.dataset.go)));
document.querySelector(".brand").addEventListener("click", event => { event.preventDefault(); showPage("dashboard"); });
document.querySelector("#employee-search").addEventListener("input", () => {
  window.clearTimeout(searchTimer);
  api.page = 1;
  searchTimer = window.setTimeout(renderEmployees, 250);
});
document.querySelector("#department-filter").addEventListener("change", () => { api.page = 1; renderEmployees(); });
document.querySelector("#employee-status-filter").addEventListener("change", () => { api.page = 1; renderEmployees(); });
document.querySelector("#clear-employee-filters").addEventListener("click", () => {
  document.querySelector("#employee-search").value = "";
  document.querySelector("#department-filter").value = "";
  document.querySelector("#employee-status-filter").value = "";
  api.page = 1;
  renderEmployees();
});
document.querySelector("#employee-page-size").addEventListener("change", event => {
  api.pageSize = Number(event.target.value);
  api.page = 1;
  renderEmployees();
});
document.querySelector("#employee-pagination").addEventListener("click", event => {
  const button = event.target.closest("button[data-page]");
  if (!button || button.disabled) return;
  api.page = Number(button.dataset.page);
  renderEmployees();
});
document.querySelector(".sort-button[data-sort='name']").addEventListener("click", () => {
  api.sort = api.sort === "name" ? "-name" : "name";
  api.page = 1;
  renderEmployees();
});
document.querySelector("#attendance-search").addEventListener("input", () => {
  window.clearTimeout(attendanceSearchTimer);
  api.attendancePage = 1;
  attendanceSearchTimer = window.setTimeout(() => loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error")), 250);
});
document.querySelector("#attendance-department-filter").addEventListener("change", () => {
  api.attendancePage = 1;
  loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error"));
});
document.querySelector("#attendance-status-filter").addEventListener("change", () => {
  api.attendancePage = 1;
  loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error"));
});
document.querySelector("#attendance-date").addEventListener("change", () => {
  api.attendancePage = 1;
  loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error"));
});
document.querySelector("#generate-attendance-report").addEventListener("click", () => {
  loadAttendanceReport().catch(error => notify(`Could not load attendance report: ${error.message}`, "error"));
});
document.querySelector("#attendance-report-export").addEventListener("click", downloadAttendanceReport);
document.querySelector("#generate-audit-report").addEventListener("click", () => {
  api.auditPage = 1;
  loadAuditLogs().catch(error => notify(`Could not load audit logs: ${error.message}`, "error"));
});
document.querySelector("#audit-search").addEventListener("input", () => {
  window.clearTimeout(auditSearchTimer);
  api.auditPage = 1;
  auditSearchTimer = window.setTimeout(
    () => loadAuditLogs().catch(error => notify(`Could not search audit logs: ${error.message}`, "error")),
    250);
});
document.querySelector("#audit-entity-filter").addEventListener("change", () => {
  api.auditPage = 1;
  loadAuditLogs().catch(error => notify(`Could not filter audit logs: ${error.message}`, "error"));
});
document.querySelector("#audit-page-size").addEventListener("change", event => {
  api.auditPageSize = Number(event.target.value);
  api.auditPage = 1;
  loadAuditLogs().catch(error => notify(`Could not load audit logs: ${error.message}`, "error"));
});
document.querySelector("#audit-pagination").addEventListener("click", event => {
  const button = event.target.closest("button[data-audit-page]");
  if (!button || button.disabled) return;
  api.auditPage = Number(button.dataset.auditPage);
  loadAuditLogs().catch(error => notify(`Could not load audit logs: ${error.message}`, "error"));
});
document.querySelector("#audit-export").addEventListener("click", downloadAuditPage);
document.querySelector("#attendance-page-size").addEventListener("change", event => {
  api.attendancePageSize = Number(event.target.value);
  api.attendancePage = 1;
  loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error"));
});
document.querySelector("#attendance-pagination").addEventListener("click", event => {
  const button = event.target.closest("button[data-attendance-page]");
  if (!button || button.disabled) return;
  api.attendancePage = Number(button.dataset.attendancePage);
  loadAttendance().catch(error => notify(`Could not load attendance: ${error.message}`, "error"));
});
document.querySelector("#employee-attendance-page-size").addEventListener("change", event => {
  api.employeeAttendancePageSize = Number(event.target.value);
  api.employeeAttendancePage = 1;
  loadEmployeeAttendance().catch(error => notify(`Could not load your attendance: ${error.message}`, "error"));
});
document.querySelector("#employee-attendance-pagination").addEventListener("click", event => {
  const button = event.target.closest("button[data-employee-attendance-page]");
  if (!button || button.disabled) return;
  api.employeeAttendancePage = Number(button.dataset.employeeAttendancePage);
  loadEmployeeAttendance().catch(error => notify(`Could not load your attendance: ${error.message}`, "error"));
});
document.querySelector("#assignment-search").addEventListener("input", () => {
  window.clearTimeout(scheduleSearchTimer);
  scheduleSearchTimer = window.setTimeout(() => loadSchedules().catch(error => notify(`Could not load assignments: ${error.message}`, "error")), 250);
});
document.querySelector("#assignment-department-filter").addEventListener("change", () => {
  loadSchedules().catch(error => notify(`Could not load assignments: ${error.message}`, "error"));
});
document.querySelector("#access-search").addEventListener("input", renderAccess);
document.querySelector("#access-status-filter").addEventListener("change", () => { reviewFilterOnly = false; renderAccess(); });
document.querySelector("#access-direction-filter").addEventListener("change", renderAccess);
document.querySelectorAll("[data-access-filter]").forEach(button => button.addEventListener("click", () => {
  document.querySelector("#access-status-filter").value = "Needs review";
  reviewFilterOnly = true;
  renderAccess();
  document.querySelector("#page-access").scrollIntoView({ behavior: "smooth", block: "start" });
}));
document.querySelector("#employee-rows").addEventListener("click", event => {
  const button = event.target.closest(".employee-view");
  if (!button) return;
  openEmployeeForm(button.dataset.id).catch(error => notify(error.message, "error"));
});
document.querySelector("#attendance-rows").addEventListener("click", event => {
  const button = event.target.closest(".attendance-edit");
  if (!button) return;
  const row = attendance.find(item => item.id === button.dataset.id);
  if (row) openAttendanceEdit(row);
});
document.querySelector("#add-shift").addEventListener("click", () => openShiftForm());
document.querySelector("#shift-rows").addEventListener("click", event => {
  const editButton = event.target.closest(".edit-shift");
  if (editButton) {
    const shift = shiftPatterns.find(item => item.id === editButton.dataset.id);
    if (shift) openShiftForm(shift);
    return;
  }
  const toggleButton = event.target.closest(".toggle-shift");
  if (toggleButton) {
    toggleShiftActive(toggleButton.dataset.id, toggleButton.dataset.active !== "true")
      .catch(error => notify(error.message, "error"));
  }
});
document.querySelector("#assignment-rows").addEventListener("click", event => {
  const button = event.target.closest(".assign-shift");
  if (button) openShiftAssignment(button.dataset.id);
});
document.querySelector("#access-rows").addEventListener("click", event => {
  const button = event.target.closest(".review-event");
  if (!button) return;
  const row = accessEvents.find(item => item.id === button.dataset.id);
  if (row) openAccessReview(row);
});
document.querySelector("#add-employee").addEventListener("click", () => openEmployeeForm().catch(error => notify(error.message, "error")));
document.querySelector("#add-admin-user").addEventListener("click", () => openAdminUserForm().catch(error => notify(error.message, "error")));
document.querySelector("#admin-user-search").addEventListener("input", renderAdminUsers);
document.querySelector("#admin-user-rows").addEventListener("click", event => {
  const editButton = event.target.closest(".edit-admin-user");
  if (editButton) {
    openAdminUserForm(editButton.dataset.id).catch(error => notify(error.message, "error"));
    return;
  }
  const resetButton = event.target.closest(".reset-admin-password");
  if (resetButton) openAdminPasswordReset(resetButton.dataset.id);
});
document.querySelector("#modal-close").addEventListener("click", closeModal);
document.querySelector("#modal-backdrop").addEventListener("click", event => { if (event.target.id === "modal-backdrop") closeModal(); });
document.addEventListener("keydown", event => { if (event.key === "Escape") closeModal(); });
document.querySelector("#profile-button").addEventListener("click", () => document.querySelector("#role-menu").classList.toggle("hidden"));
document.querySelector("#logout-button").addEventListener("click", async () => {
  if (api.demoMode) {
    document.querySelector("#role-menu").classList.add("hidden");
    showLogin("Demo session ended.");
    return;
  }
  try {
    await apiRequest("/api/auth/logout", { method: "POST" });
    document.querySelector("#role-menu").classList.add("hidden");
    showLogin("You have signed out.", true);
  } catch (error) {
    notify(error.message, "error");
  }
});
document.querySelector("#mobile-menu").addEventListener("click", () => document.querySelector("#sidebar").classList.toggle("open"));
document.querySelector("#attendance-export").addEventListener("click", () => {
  const columns = ["Employee number", "Name", "Department", "Scheduled", "Clock in", "Clock out", "Worked minutes", "Status"];
  const rows = attendance.map(row => [
    row.employeeNumber, row.employeeName, row.department, row.scheduledStart,
    row.clockIn || "", row.clockOut || "", row.workedMinutes ?? "", attendanceStatusLabel(row.status)
  ]);
  const csv = [columns, ...rows].map(row => row.map(csvCell).join(",")).join("\r\n");
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `attendance-${document.querySelector("#attendance-date").value || "today"}.csv`;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
});
document.querySelector("#employee-export").addEventListener("click", downloadEmployeePage);
document.querySelector("#access-refresh").addEventListener("click", () => notify("Events refreshed. Sample events are simulated for this preview."));
document.querySelector("#dashboard-date").addEventListener("click", () => notify("Showing today: Tuesday, 27 October 2026."));
document.querySelector("#help-link").addEventListener("click", () => notify("Help centre is not connected in this preview."));
document.querySelector("#login-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (window.location.protocol === "file:") {
    setLoginMessage("This page was opened as a local file. Run the Workplace API and open http://localhost:5080/ instead.");
    return;
  }
  const form = event.currentTarget;
  const button = document.querySelector("#login-submit");
  const data = Object.fromEntries(new FormData(form).entries());
  button.disabled = true;
  setLoginMessage("");
  try {
    const user = await apiRequest("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ email: data.email, password: data.password, rememberMe: data.rememberMe === "on" })
    });
    form.reset();
    await routeAuthenticatedUser(user);
  } catch (error) {
    setLoginMessage(error.message || "Unable to sign in.");
  } finally {
    button.disabled = false;
  }
});

document.querySelector("#password-setup-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = Object.fromEntries(new FormData(form).entries());
  const message = document.querySelector("#password-setup-message");
  const button = document.querySelector("#password-setup-submit");
  if (data.newPassword !== data.confirmPassword) {
    message.textContent = "The new passwords do not match.";
    return;
  }
  button.disabled = true;
  message.textContent = "";
  try {
    await apiRequest("/api/auth/change-password", {
      method: "POST",
      body: JSON.stringify({
        currentPassword: data.currentPassword,
        newPassword: data.newPassword
      })
    });
    const user = pendingPasswordChangeUser;
    form.reset();
    if (!user) throw new Error("Your sign-in session expired. Please sign in again.");
    await routeAuthenticatedUser({ ...user, mustChangePassword: false });
  } catch (error) {
    message.textContent = error.message || "Unable to update your password.";
  } finally {
    button.disabled = false;
  }
});

document.querySelector("#employee-access-notice").classList.remove("current-page");
if (window.location.protocol === "file:") {
  document.querySelector("#login-submit").disabled = true;
  showLogin("This page was opened as a local file. Run the Workplace API and open http://localhost:5080/ instead.");
} else {
  apiRequest("/api/auth/me")
    .then(routeAuthenticatedUser)
    .catch(error => showLogin(error.status === 401
      ? ""
      : error.status === 404
        ? "The Workplace API is not connected at this address. You can still explore Demo mode below."
        : `Could not connect to the workplace service. Check that the API is running, then open http://localhost:5080/. (${error.message})`));
}

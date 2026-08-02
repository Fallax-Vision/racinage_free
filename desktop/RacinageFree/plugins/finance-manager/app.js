(() => {
  "use strict";

  const app = document.getElementById("financeApp");
  const view = document.getElementById("view");
  const feedback = document.getElementById("feedback");
  const dialog = document.getElementById("recordDialog");
  const form = document.getElementById("recordForm");
  const fieldsMount = document.getElementById("dialogFields");
  const importDialog = document.getElementById("importDialog");
  const importForm = document.getElementById("importForm");
  const pending = new Map();
  let sequence = 0;
  let state = { records: [], attachments: [], currencies: [{ code: "USD", name: "US Dollar", rate: 1 }], display_currency: "USD" };
  let topTab = "home";
  let workspaceId = "";
  let panel = "overview";
  let balancesHidden = localStorage.getItem("financeBalancesHidden") === "1";
  let activeEditor = null;
  let importRows = [];
  const workspaceGroups = {
    overview: ["overview"],
    transactions: ["transactions", "recurring", "taxonomy"],
    planning: ["budgets", "goals", "forecast"],
    assets: ["accounts", "debts", "investments"],
    reports: ["reports"],
    people: ["circles"],
    settings: ["settings"]
  };
  const workspaceGroupLabels = { overview: "Overview", transactions: "Transactions", planning: "Planning", assets: "Assets & Debt", reports: "Reports", people: "People", settings: "Settings" };
  const panelLabels = { transactions: "Activity", recurring: "Recurring & subscriptions", taxonomy: "Categories & tags", budgets: "Budgets", goals: "Goals", forecast: "Forecast", accounts: "Accounts", debts: "Debts", investments: "Investments", circles: "Circles" };

  const esc = value => String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
  const id = prefix => `${prefix}_${crypto.getRandomValues(new Uint32Array(4)).join("")}`;
  const today = () => new Date().toISOString().slice(0, 10);
  const dateLabel = value => {
    const parts = String(value || "").split("-");
    return parts.length === 3 ? `${parts[2]}/${parts[1]}/${parts[0]}` : "";
  };
  const workspaceKind = row => ["personal", "family", "group"].includes(data(row).workspace_kind) ? data(row).workspace_kind : "personal";
  const periodPreference = () => {
    const fallback = { preset: "this_month", start: "", end: "" };
    try { return { ...fallback, ...JSON.parse(localStorage.getItem(`financePeriod:${workspaceId}`) || "{}") }; } catch (_) { return fallback; }
  };
  const periodBounds = () => {
    const preference = periodPreference(), now = new Date(), end = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate())), start = new Date(end);
    if (preference.preset === "last_month") { start.setUTCDate(1); start.setUTCMonth(start.getUTCMonth() - 1); end.setUTCDate(0); }
    else if (["last_3_months", "last_6_months", "last_12_months"].includes(preference.preset)) { start.setUTCDate(1); start.setUTCMonth(start.getUTCMonth() - ({ last_3_months: 2, last_6_months: 5, last_12_months: 11 }[preference.preset])); }
    else if (preference.preset === "this_year") { start.setUTCMonth(0, 1); }
    else if (preference.preset === "custom" && /^\d{4}-\d{2}-\d{2}$/.test(preference.start) && /^\d{4}-\d{2}-\d{2}$/.test(preference.end)) return { start: preference.start, end: preference.end, preference };
    else start.setUTCDate(1);
    return { start: start.toISOString().slice(0, 10), end: end.toISOString().slice(0, 10), preference };
  };
  const periodControl = () => {
    const { preference, start, end } = periodBounds(), options = [
      { value: "this_month", label: "This month" }, { value: "last_month", label: "Last month" }, { value: "last_3_months", label: "Last 3 months" },
      { value: "last_6_months", label: "Last 6 months" }, { value: "last_12_months", label: "Last 12 months" }, { value: "this_year", label: "This year" }, { value: "custom", label: "Custom" }
    ];
    return `<div class="period-control"><label>Period<select data-period-preset>${optionHtml(options, preference.preset)}</select></label><label class="custom-period"${preference.preset === "custom" ? "" : " hidden"}>From<input type="date" data-period-start value="${esc(preference.start || start)}"></label><label class="custom-period"${preference.preset === "custom" ? "" : " hidden"}>To<input type="date" data-period-end value="${esc(preference.end || end)}"></label></div>`;
  };
  const inSelectedPeriod = row => { const value = String(data(row).transaction_date || ""), bounds = periodBounds(); return value >= bounds.start && value <= bounds.end; };
  const cents = value => Math.round(Number(value || 0) * 100);
  const amount = value => (Number(value || 0) / 100).toFixed(2);
  const byType = (type, workspace = workspaceId) => state.records.filter(row => row.record_type === type && row.status === "active" && (!workspace || row.workspace_long_id === workspace));
  const find = (type, longId) => state.records.find(row => row.record_type === type && row.long_id === longId && row.status === "active");
  const data = row => row?.data || {};
  const currency = code => state.currencies.find(item => item.code === code) || state.currencies[0];
  const snapshot = (nativeCents, code) => {
    const rate = Number(currency(code)?.rate || 1);
    return { native_cents: Number(nativeCents), usd_cents: Math.round(Number(nativeCents) / rate), fx_rate: rate, native_currency_code: code };
  };
  const money = usdCents => {
    if (balancesHidden) return "••••••";
    const display = currency(state.display_currency);
    const native = Number(usdCents || 0) * Number(display?.rate || 1) / 100;
    try {
      return `${display.code} ${new Intl.NumberFormat("en-GB", { style: "currency", currency: display.code }).format(native)}`;
    } catch (_) {
      return `${display.code} ${native.toFixed(2)}`;
    }
  };
  const notify = (message, ok = true) => {
    feedback.textContent = message;
    feedback.classList.toggle("error", !ok);
    feedback.hidden = false;
  };

  const bridge = (action, payload = {}) => new Promise((resolve, reject) => {
    const requestId = `finance_${Date.now()}_${++sequence}`;
    pending.set(requestId, { resolve, reject });
    parent.postMessage({ financeBridge: true, bridgeToken: window.FINANCE_BRIDGE_TOKEN, slug: "finance-manager", requestId, action, payload }, "*");
    setTimeout(() => {
      if (!pending.has(requestId)) return;
      pending.delete(requestId);
      reject(new Error("The local finance service did not respond."));
    }, 30000);
  });

  window.addEventListener("message", event => {
    const message = event.data;
    if (event.source !== parent || !message?.financeBridgeResponse || message.bridgeToken !== window.FINANCE_BRIDGE_TOKEN) return;
    const waiter = pending.get(message.requestId);
    if (!waiter) return;
    pending.delete(message.requestId);
    message.ok ? waiter.resolve(message.result) : waiter.reject(new Error(message.message || "Finance request failed."));
  });

  const reload = async () => {
    state = await bridge("bootstrap");
    state.records = state.records || [];
    state.attachments = state.attachments || [];
    state.currencies = state.currencies?.length ? state.currencies : [{ code: "USD", name: "US Dollar", rate: 1 }];
    if (!currency(state.display_currency)) state.display_currency = "USD";
  };

  const save = async (recordType, recordData, options = {}) => {
    const current = options.long_id ? find(recordType, options.long_id) : null;
    const result = await bridge("save", {
      record_type: recordType,
      long_id: options.long_id || "",
      workspace_long_id: options.workspace_long_id ?? workspaceId,
      version: current?.version || 0,
      data: recordData
    });
    await reload();
    return result;
  };
  const saveBatch = async rows => {
    const result = await bridge("batch_save", { records: rows });
    await reload();
    return result;
  };
  const remove = async (recordType, longId) => {
    const current = find(recordType, longId);
    await bridge("delete", { record_type: recordType, long_id: longId, version: current?.version || 0 });
    await reload();
  };

  const transactionDirection = type => ["income", "transfer_in"].includes(type) ? 1 : -1;
  const accountBalanceUsd = accountRow => {
    const account = data(accountRow);
    const opening = Number(account.opening_usd_cents || 0);
    return opening + byType("transactions").filter(row => data(row).account === accountRow.long_id)
      .reduce((sum, row) => sum + transactionDirection(data(row).transaction_type) * Number(data(row).amount_usd_cents || 0), 0);
  };
  const workspaceNetUsd = longId => {
    const accounts = byType("accounts", longId);
    return accounts.reduce((sum, row) => {
      const value = accountBalanceUsd(row);
      return sum + (["credit_card", "loan", "other_liability"].includes(data(row).account_type) ? -Math.abs(value) : value);
    }, 0);
  };

  const field = (name, label, type = "text", extra = {}) => ({ name, label, type, ...extra });
  const normalizeTagNames = value => [...new Map(String(value || "").split(",").map(name => name.trim()).filter(Boolean).slice(0, 30).map(name => [name.toLocaleLowerCase(), name.slice(0, 80)])).values()].join(", ");
  const ensureTags = async value => {
    for (const name of normalizeTagNames(value).split(",").map(item => item.trim()).filter(Boolean)) {
      const existing = byType("tags").find(row => data(row).name.toLocaleLowerCase() === name.toLocaleLowerCase());
      if (!existing) await save("tags", { name, normalized_name: name.toLocaleLowerCase(), archived: false });
      else if (data(existing).archived) await save("tags", { ...data(existing), archived: false }, { long_id: existing.long_id });
    }
  };
  const accountOptions = () => byType("accounts").filter(row => !data(row).external).map(row => ({ value: row.long_id, label: `${data(row).name} (${data(row).native_currency_code})` }));
  const categoryOptions = (blank = true) => byType("categories").filter(row => !data(row).archived).map(row => ({ value: row.long_id, label: `${data(row).name} - ${data(row).category_type}` }));
  const recordConfigs = {
    accounts: {
      title: "Account",
      fields: [
        field("name", "Name", "text", { required: true, maxlength: 160 }),
        field("account_type", "Type", "select", { options: ["cash", "checking", "savings", "mobile_money", "credit_card", "loan", "investment", "other_asset", "other_liability"] }),
        field("native_currency_code", "Currency", "currency"),
        field("opening", "Opening balance", "money", { value: "0" })
      ]
    },
    transactions: {
      title: "Transaction",
      fields: [
        field("transaction_type", "Type", "select", { options: ["income", "expense", "transfer"] }),
        field("account", "Account", "dynamic", { options: accountOptions }),
        field("destination_account", "Destination account", "dynamic", { options: accountOptions }),
        field("value", "Amount", "money", { required: true, min: .01 }),
        field("transaction_date", "Date", "date", { required: true, value: today() }),
        field("payee", "Payee", "text", { maxlength: 190 }),
        field("state", "Status", "select", { options: ["pending", "cleared", "reconciled"] }),
        field("category", "Category", "dynamic", { options: categoryOptions, blank: "Uncategorized" }),
        field("tags", "Tags", "text", { maxlength: 500 }),
        field("note", "Note", "textarea", { wide: true, maxlength: 2000 })
      ]
    },
    recurring_rules: {
      title: "Recurring rule",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("recurring_kind", "Kind", "select", { options: ["income", "bill", "subscription"] }),
        field("transaction_type", "Type", "select", { options: ["income", "expense"] }),
        field("account", "Account", "dynamic", { options: accountOptions }),
        field("value", "Amount", "money", { required: true, min: .01 }),
        field("frequency", "Frequency", "select", { options: ["weekly", "monthly", "quarterly", "yearly"] }),
        field("next_date", "Next date", "date", { required: true, value: today() }),
        field("payee", "Payee", "text"),
        field("category", "Category", "dynamic", { options: categoryOptions, blank: "Uncategorized" }),
        field("tags", "Tags", "text", { maxlength: 500 }),
        field("note", "Note", "textarea", { wide: true })
      ]
    },
    budgets: {
      title: "Budget",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("period_type", "Period", "select", { options: ["monthly", "quarterly", "yearly"] }),
        field("start_date", "Start date", "date", { required: true, value: today().slice(0, 8) + "01" }),
        field("value", "Planned amount", "money", { required: true, min: 0 }),
        field("native_currency_code", "Currency", "currency"),
        field("category", "Category", "dynamic", { options: categoryOptions, blank: "All categories" }),
        field("goal", "Linked goal", "dynamic", { options: () => byType("goals").map(row => ({ value: row.long_id, label: data(row).name })), blank: "None" }),
        field("rollover", "Rollover", "checkbox")
      ]
    },
    goals: {
      title: "Goal",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("goal_type", "Type", "select", { options: ["savings", "investment", "debt_repayment", "custom"] }),
        field("target", "Target", "money", { required: true, min: 0 }),
        field("current", "Progress", "money", { required: true, min: 0, value: 0 }),
        field("native_currency_code", "Currency", "currency"),
        field("target_date", "Target date", "date")
      ]
    },
    debts: {
      title: "Debt",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("balance", "Balance", "money", { required: true, min: 0 }),
        field("original", "Original amount", "money", { required: true, min: 0 }),
        field("native_currency_code", "Currency", "currency"),
        field("apr", "APR %", "number", { required: true, min: 0, step: .01 }),
        field("rate_type", "Rate", "select", { options: ["fixed", "variable"] }),
        field("payment_frequency", "Frequency", "select", { options: ["weekly", "fortnightly", "monthly", "quarterly", "yearly"] }),
        field("minimum_payment", "Minimum payment", "money", { required: true, min: 0 }),
        field("extra_payment", "Extra payment", "money", { min: 0, value: 0 }),
        field("next_due_date", "Next due date", "date")
      ]
    },
    debt_payments: {
      title: "Debt payment",
      fields: [
        field("debt", "Debt", "dynamic", { options: () => byType("debts").map(row => ({ value: row.long_id, label: data(row).name })) }),
        field("value", "Amount", "money", { required: true, min: .01 }),
        field("payment_date", "Date", "date", { required: true, value: today() })
      ]
    },
    investments: {
      title: "Investment holding",
      fields: [
        field("account", "Investment account", "dynamic", { options: () => byType("accounts").filter(row => data(row).account_type === "investment").map(row => ({ value: row.long_id, label: data(row).name })) }),
        field("name", "Name", "text", { required: true }),
        field("symbol", "Symbol", "text"),
        field("units", "Units", "number", { min: 0, step: .00000001 }),
        field("cost_basis", "Cost basis", "money", { required: true, min: 0 }),
        field("current_value", "Current value", "money", { required: true, min: 0 }),
        field("native_currency_code", "Currency", "currency"),
        field("valuation_date", "Valuation date", "date", { value: today() })
      ]
    },
    scenarios: {
      title: "What-if scenario",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("months", "Months", "number", { required: true, min: 1, max: 120, value: 12 }),
        field("monthly_adjustment", "Monthly adjustment", "money", { required: true, value: 0 }),
        field("native_currency_code", "Currency", "currency")
      ]
    },
    circles: {
      title: "Circle",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("circle_type", "Type", "select", { options: ["contributions", "loans"] }),
        field("native_currency_code", "Currency", "currency")
      ]
    },
    circle_members: {
      title: "Circle person",
      fields: [
        field("circle", "Circle", "dynamic", { options: () => byType("circles").map(row => ({ value: row.long_id, label: data(row).name })) }),
        field("name", "Name", "text", { required: true }),
        field("contact", "Email or note", "text")
      ]
    },
    circle_entries: {
      title: "Circle entry",
      fields: [
        field("circle", "Circle", "dynamic", { options: () => byType("circles").map(row => ({ value: row.long_id, label: data(row).name })) }),
        field("member", "Person", "dynamic", { options: () => byType("circle_members").map(row => ({ value: row.long_id, label: data(row).name })) }),
        field("entry_type", "Type", "select", { options: ["contribution", "loan", "repayment", "withdrawal"] }),
        field("value", "Amount", "money", { required: true, min: .01 }),
        field("entry_date", "Date", "date", { required: true, value: today() }),
        field("note", "Note", "textarea", { wide: true })
      ]
    },
    categories: {
      title: "Category",
      fields: [
        field("name", "Name", "text", { required: true, maxlength: 120 }),
        field("category_type", "Type", "select", { options: ["expense", "income", "transfer"] }),
        field("parent", "Parent category", "dynamic", { options: categoryOptions, blank: "None" })
      ]
    },
    tags: {
      title: "Tag",
      fields: [field("name", "Name", "text", { required: true, maxlength: 80 })]
    }
  };

  const optionHtml = (options, selected = "", blank = "") => `${blank ? `<option value="">${esc(blank)}</option>` : ""}${options.map(option => {
    const value = typeof option === "object" ? option.value : option;
    const label = typeof option === "object" ? option.label : String(option).replaceAll("_", " ").replace(/\b\w/g, char => char.toUpperCase());
    return `<option value="${esc(value)}"${String(value) === String(selected) ? " selected" : ""}>${esc(label)}</option>`;
  }).join("")}`;
  const inputHtml = (spec, value = "") => {
    const attrs = `${spec.required ? " required" : ""}${spec.maxlength ? ` maxlength="${spec.maxlength}"` : ""}${spec.min !== undefined ? ` min="${spec.min}"` : ""}${spec.max !== undefined ? ` max="${spec.max}"` : ""}${spec.step ? ` step="${spec.step}"` : ""}${spec.placeholder ? ` placeholder="${esc(spec.placeholder)}"` : ""}`;
    const current = value !== "" && value !== null && value !== undefined ? value : (spec.value ?? "");
    if (spec.type === "select") return `<select name="${esc(spec.name)}">${optionHtml(spec.options, current)}</select>`;
    if (spec.type === "dynamic") return `<select name="${esc(spec.name)}"${attrs}>${optionHtml(spec.options(), current, spec.blank)}</select>`;
    if (spec.type === "currency") return `<select name="${esc(spec.name)}">${optionHtml(state.currencies.map(item => ({ value: item.code, label: `${item.code} - ${item.name}` })), current || state.display_currency)}</select>`;
    if (spec.type === "checkbox") return `<span class="check"><input type="checkbox" name="${esc(spec.name)}" value="1"${current ? " checked" : ""}><span>${esc(spec.label)}</span></span>`;
    if (spec.type === "textarea") return `<textarea name="${esc(spec.name)}"${attrs}>${esc(current)}</textarea>`;
    const type = spec.type === "money" ? "number" : spec.type;
    return `<input type="${esc(type)}" name="${esc(spec.name)}" value="${esc(current)}"${spec.type === "money" ? ' step="0.01"' : ""}${attrs}>`;
  };

  const openEditor = (recordType, row = null, preset = {}) => {
    const config = recordConfigs[recordType];
    if (!config) return;
    const values = { ...data(row), ...preset };
    document.getElementById("dialogTitle").textContent = `${row ? "Edit" : "Add"} ${config.title.toLowerCase()}`;
    document.getElementById("dialogKicker").textContent = panel === "settings" ? "Finance Manager" : panel;
    fieldsMount.innerHTML = config.fields.map(spec => `<label class="${spec.wide ? "wide" : ""}">${spec.type === "checkbox" ? "" : `<span>${esc(spec.label)}</span>`}${inputHtml(spec, values[spec.name])}</label>`).join("");
    if (recordType === "transactions" && !["transfer_in", "transfer_out"].includes(values.transaction_type)) {
      fieldsMount.insertAdjacentHTML("beforeend", `<section class="split-editor wide" data-split-editor><header><div><strong>Split categories</strong><small>Use lines only when a transaction belongs to multiple categories.</small></div><button class="ghost" type="button" data-add-split>Add split</button></header><div data-split-lines>${(values.splits || []).map(split => splitLineHtml(split)).join("")}</div><p>Remaining <strong data-split-remaining>0.00</strong></p></section>`);
      syncPortableSplits();
    }
    activeEditor = { recordType, row };
    dialog.showModal();
  };

  const splitLineHtml = (split = {}) => `<div class="split-line"><label><span>Category</span><select data-split-category>${optionHtml(categoryOptions(false), split.category || "")}</select></label><label><span>Amount</span><input type="number" min="0.01" step="0.01" data-split-amount value="${esc(split.amount_cents ? amount(split.amount_cents) : "")}"></label><label><span>Note</span><input maxlength="500" data-split-note value="${esc(split.note || "")}"></label><button class="danger" type="button" data-remove-split>Remove</button></div>`;
  const syncPortableSplits = () => {
    const editor = fieldsMount.querySelector("[data-split-editor]");
    if (!editor) return;
    const total = cents(form.elements.value?.value), allocated = [...editor.querySelectorAll("[data-split-amount]")].reduce((sum, input) => sum + cents(input.value), 0);
    editor.querySelector("[data-split-remaining]").textContent = amount(Math.max(0, total - allocated));
    const primary = form.elements.category?.closest("label");
    if (primary) { primary.hidden = Boolean(editor.querySelector(".split-line")); form.elements.category.disabled = primary.hidden; }
  };
  const editorValues = () => {
    const values = Object.fromEntries(new FormData(form).entries()), lines = [...fieldsMount.querySelectorAll(".split-line")];
    values.splits = lines.map(line => ({ category: line.querySelector("[data-split-category]").value, amount_cents: cents(line.querySelector("[data-split-amount]").value), note: line.querySelector("[data-split-note]").value.trim() }));
    return values;
  };
  const validateSplits = (splits, totalCents) => {
    if (!splits.length) return [];
    if (splits.some(split => !split.category || split.amount_cents <= 0) || splits.reduce((sum, split) => sum + split.amount_cents, 0) !== totalCents) throw new Error("Split amounts must be positive and equal the transaction amount.");
    return splits.map(split => ({ ...split, category_name: data(find("categories", split.category)).name || "" }));
  };
  const normalizeRecord = (recordType, values, existing) => {
    if (recordType === "accounts") {
      const snap = snapshot(cents(values.opening), values.native_currency_code);
      return { ...values, opening_cents: snap.native_cents, opening_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate };
    }
    if (recordType === "transactions") {
      const account = find("accounts", values.account);
      if (!account) throw new Error("Choose an account.");
      const snap = snapshot(cents(values.value), data(account).native_currency_code);
      const splits = values.splits.length ? validateSplits(values.splits, snap.native_cents) : (values.category ? [{ category: values.category, category_name: data(find("categories", values.category)).name || "", amount_cents: snap.native_cents, note: "" }] : []);
      return { ...values, tags: normalizeTagNames(values.tags), amount_cents: snap.native_cents, amount_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, native_currency_code: data(account).native_currency_code, splits };
    }
    if (recordType === "recurring_rules") {
      const account = find("accounts", values.account);
      if (!account) throw new Error("Choose an account.");
      const snap = snapshot(cents(values.value), data(account).native_currency_code);
      const recurringKind = ["income", "bill", "subscription"].includes(values.recurring_kind) ? values.recurring_kind : (values.transaction_type === "income" ? "income" : "bill");
      if ((values.transaction_type === "income") !== (recurringKind === "income")) throw new Error("Income rules must use the Income kind; expense rules must use Bill or Subscription.");
      return { ...values, recurring_kind: recurringKind, tags: normalizeTagNames(values.tags), amount_cents: snap.native_cents, amount_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, native_currency_code: data(account).native_currency_code };
    }
    if (recordType === "budgets") {
      const snap = snapshot(cents(values.value), values.native_currency_code);
      return { ...values, planned_cents: snap.native_cents, planned_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, rollover: values.rollover === "1" };
    }
    if (recordType === "goals") {
      const target = snapshot(cents(values.target), values.native_currency_code), current = snapshot(cents(values.current), values.native_currency_code);
      return { ...values, target_cents: target.native_cents, target_usd_cents: target.usd_cents, current_cents: current.native_cents, current_usd_cents: current.usd_cents, fx_rate: target.fx_rate };
    }
    if (recordType === "debts") {
      const balance = snapshot(cents(values.balance), values.native_currency_code), original = snapshot(cents(values.original), values.native_currency_code);
      return { ...values, balance_cents: balance.native_cents, balance_usd_cents: balance.usd_cents, original_cents: original.native_cents, original_usd_cents: original.usd_cents, apr_bps: Math.round(Number(values.apr || 0) * 100), minimum_payment_cents: cents(values.minimum_payment), extra_payment_cents: cents(values.extra_payment), fx_rate: balance.fx_rate };
    }
    if (recordType === "debt_payments") {
      const debt = find("debts", values.debt);
      if (!debt) throw new Error("Choose a debt.");
      const amountCents = cents(values.value), debtData = data(debt);
      const interest = Math.round(Number(debtData.balance_cents || 0) * Number(debtData.apr_bps || 0) / 10000 / 12);
      const principal = Math.max(0, Math.min(Number(debtData.balance_cents || 0), amountCents - interest));
      if (principal <= 0) throw new Error("The payment must cover estimated monthly interest.");
      const snap = snapshot(amountCents, debtData.native_currency_code);
      return { ...values, amount_cents: amountCents, amount_usd_cents: snap.usd_cents, principal_cents: principal, principal_usd_cents: snapshot(principal, debtData.native_currency_code).usd_cents, interest_cents: interest, interest_usd_cents: snapshot(interest, debtData.native_currency_code).usd_cents, fx_rate: snap.fx_rate };
    }
    if (recordType === "investments") {
      const cost = snapshot(cents(values.cost_basis), values.native_currency_code), current = snapshot(cents(values.current_value), values.native_currency_code);
      return { ...values, units: Number(values.units || 0), cost_basis_cents: cost.native_cents, cost_basis_usd_cents: cost.usd_cents, current_value_cents: current.native_cents, current_value_usd_cents: current.usd_cents, fx_rate: cost.fx_rate };
    }
    if (recordType === "scenarios") {
      const snap = snapshot(cents(values.monthly_adjustment), values.native_currency_code);
      return { ...values, months: Number(values.months || 12), monthly_adjustment_cents: snap.native_cents, monthly_adjustment_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate };
    }
    if (recordType === "circle_entries") {
      const circle = find("circles", values.circle);
      if (!circle) throw new Error("Choose a circle.");
      const snap = snapshot(cents(values.value), data(circle).native_currency_code);
      return { ...values, amount_cents: snap.native_cents, amount_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, native_currency_code: data(circle).native_currency_code };
    }
    if (recordType === "categories") return { ...values, archived: false };
    if (recordType === "tags") return { ...values, normalized_name: String(values.name || "").trim().toLocaleLowerCase(), archived: false };
    return { ...existing, ...values };
  };

  const table = (headers, rows) => `<div class="table-wrap"><table><thead><tr>${headers.map(header => `<th>${esc(header)}</th>`).join("")}</tr></thead><tbody>${rows.length ? rows.join("") : `<tr><td colspan="${headers.length}">No records yet</td></tr>`}</tbody></table></div>`;
  const rowActions = (type, row, extra = "") => `<div class="row-actions"><button class="link-button" type="button" data-edit="${type}" data-id="${esc(row.long_id)}">Edit</button>${extra}<button class="danger" type="button" data-delete="${type}" data-id="${esc(row.long_id)}">Delete</button></div>`;

  const topNavigation = () => {
    document.querySelectorAll("[data-top]").forEach(button => button.classList.toggle("active", button.dataset.top === topTab));
    const mobile = document.getElementById("mobileTopNav");
    mobile.innerHTML = optionHtml(["home", "workspaces", "help"], topTab);
    document.getElementById("displayCurrency").innerHTML = optionHtml(state.currencies.map(item => ({ value: item.code, label: `${item.code} - ${item.name}` })), state.display_currency);
    document.getElementById("toggleBalances").textContent = balancesHidden ? "Show balances" : "Hide balances";
  };
  const workspaceTabs = () => {
    const workspace = find("workspaces", workspaceId), kind = workspaceKind(workspace), hasCircles = byType("circles").length > 0;
    const groups = Object.entries(workspaceGroups).filter(([group]) => group !== "people" || kind !== "personal" || hasCircles);
    const currentGroup = groups.find(([, children]) => children.includes(panel))?.[0] || "overview";
    const children = workspaceGroups[currentGroup];
    return `<label class="workspace-mobile-nav">Workspace section<select data-workspace-mobile>${optionHtml(groups.map(([value]) => ({ value: workspaceGroups[value][0], label: workspaceGroupLabels[value] })), workspaceGroups[currentGroup][0])}</select></label><nav class="workspace-tabs" aria-label="Finance workspace">${groups.map(([group, items]) => `<button type="button" data-panel="${items[0]}" class="${group === currentGroup ? "active" : ""}">${esc(workspaceGroupLabels[group])}</button>`).join("")}</nav>${children.length > 1 ? `<nav class="workspace-subtabs" aria-label="${esc(workspaceGroupLabels[currentGroup])}">${children.map(item => `<button type="button" data-panel="${item}" class="${item === panel ? "active" : ""}">${esc(panelLabels[item] || item)}</button>`).join("")}</nav>` : ""}`;
  };

  const renderHome = () => {
    const workspaces = byType("workspaces", "");
    const included = workspaces.filter(row => data(row).include_home !== false);
    const total = included.reduce((sum, row) => sum + workspaceNetUsd(row.long_id), 0);
    const recent = state.records.filter(row => row.record_type === "transactions" && row.status === "active" && included.some(space => space.long_id === row.workspace_long_id))
      .sort((a, b) => String(data(b).transaction_date).localeCompare(String(data(a).transaction_date))).slice(0, 8);
    view.innerHTML = `<section class="metrics"><article class="metric"><span>Net worth</span><strong>${esc(money(total))}</strong></article><article class="metric"><span>Workspaces</span><strong>${workspaces.length}</strong></article><article class="metric"><span>Accounts</span><strong>${workspaces.reduce((sum, row) => sum + byType("accounts", row.long_id).length, 0)}</strong></article></section>
      <section class="panel"><header class="panel-head"><div><p class="kicker">Local workspaces</p><h2>Finances for every part of life</h2></div><button class="primary" type="button" data-new="workspaces">Create workspace</button></header>
      ${workspaces.length ? `<div class="workspace-grid">${workspaces.map(row => `<article class="workspace-card"><header><span class="workspace-mark">${esc(data(row).name?.[0]?.toUpperCase() || "F")}</span><span class="type-badge is-${workspaceKind(row)}">${esc(workspaceKind(row))}</span></header><h3>${esc(data(row).name)}</h3><strong>${esc(money(workspaceNetUsd(row.long_id)))}</strong><button class="link-button" type="button" data-open-workspace="${esc(row.long_id)}">Open →</button></article>`).join("")}</div>` : `<div class="empty"><h3>Create a Personal, Family, or Group workspace</h3><p>You can create multiple workspaces of every type. All records remain offline.</p><div class="panel-actions"><button class="primary" type="button" data-new="workspaces">Create workspace</button><button class="ghost" type="button" data-sample>Add sample workspace</button></div></div>`}</section>
      <section class="panel"><header class="panel-head"><h2>Recent activity</h2></header>${transactionTable(recent, false)}</section>`;
  };

  const renderWorkspaces = () => {
    const workspaces = byType("workspaces", "");
    view.innerHTML = `<section class="panel"><header class="panel-head"><div><p class="kicker">Finance Manager</p><h2>Workspaces</h2><p>Create multiple Personal, Family, and Group workspaces in any combination.</p></div><button class="primary" type="button" data-new="workspaces">New workspace</button></header>${workspaces.length ? `<div class="workspace-grid">${workspaces.map(row => `<article class="workspace-card"><header><span class="workspace-mark">${esc(data(row).name?.[0]?.toUpperCase() || "F")}</span><span class="type-badge is-${workspaceKind(row)}">${esc(workspaceKind(row))}</span></header><h3>${esc(data(row).name)}</h3><strong>${esc(money(workspaceNetUsd(row.long_id)))}</strong><div class="row-actions"><button class="link-button" type="button" data-open-workspace="${esc(row.long_id)}">Open</button><button class="link-button" type="button" data-edit="workspaces" data-id="${esc(row.long_id)}">Edit</button>${data(row).sample ? `<button class="danger" type="button" data-delete="workspaces" data-id="${esc(row.long_id)}">Remove sample</button>` : ""}</div></article>`).join("")}</div>` : '<p class="empty">No workspaces yet.</p>'}</section>`;
  };

  const renderHelp = () => {
    view.innerHTML = `<section class="help-grid"><article class="help-card"><h3>Record-only money actions</h3><p>Add funds, withdrawals, and transfers update local records. Finance Manager never moves real money.</p></article><article class="help-card"><h3>Offline and private</h3><p>Personal, Family, and Group workspaces remain single-device records in this Portable app. Online collaboration is unavailable.</p></article><article class="help-card"><h3>Multiple workspaces</h3><p>Create several workspaces of any type, subject to the local safety quotas shown by the app.</p></article><article class="help-card"><h3>Backups</h3><p>Use Reports to download CSV or a schema 2 JSON backup. Print the report using the A4 layout.</p></article></section>`;
  };

  const renderWorkspace = () => {
    const workspace = find("workspaces", workspaceId);
    if (!workspace) { workspaceId = ""; topTab = "workspaces"; render(); return; }
    if (panel === "circles" && workspaceKind(workspace) === "personal" && !byType("circles").length) panel = "overview";
    view.innerHTML = `<header class="workspace-head"><div><button class="link-button" type="button" data-back-workspaces>← Back to workspaces</button><h2>${esc(data(workspace).name)}</h2><p><span class="type-badge is-${workspaceKind(workspace)}">${esc(workspaceKind(workspace))}</span> ${esc(data(workspace).native_currency_code || "USD")} record-only workspace</p></div></header>${workspaceTabs()}<section class="panel" id="workspacePanel"></section>`;
    const mount = document.getElementById("workspacePanel");
    ({
      overview: renderOverview,
      accounts: renderAccounts,
      transactions: renderTransactions,
      recurring: renderRecurring,
      taxonomy: renderTaxonomy,
      budgets: renderBudgets,
      goals: renderGoals,
      debts: renderDebts,
      investments: renderInvestments,
      forecast: renderForecast,
      reports: renderReports,
      circles: renderCircles,
      settings: renderSettings
    }[panel] || renderOverview)(mount, workspace);
  };

  const renderOverview = mount => {
    const workspace = find("workspaces", workspaceId), kind = workspaceKind(workspace), accounts = byType("accounts"), transactions = byType("transactions").filter(inSelectedPeriod);
    const income = transactions.filter(row => data(row).transaction_type === "income").reduce((sum, row) => sum + Number(data(row).amount_usd_cents || 0), 0);
    const spending = transactions.filter(row => data(row).transaction_type === "expense").reduce((sum, row) => sum + Number(data(row).amount_usd_cents || 0), 0), cashflow = income - spending;
    const recent = [...transactions].sort((a, b) => String(data(b).transaction_date).localeCompare(String(data(a).transaction_date))).slice(0, 8);
    const categoryTotals = new Map();
    transactions.filter(row => data(row).transaction_type === "expense").forEach(row => (data(row).splits || []).forEach(split => categoryTotals.set(split.category_name || data(find("categories", split.category)).name || "Uncategorized", (categoryTotals.get(split.category_name || data(find("categories", split.category)).name || "Uncategorized") || 0) + Math.round(Number(data(row).amount_usd_cents || 0) * Number(split.amount_cents || 0) / Math.max(1, Number(data(row).amount_cents || 0))))));
    const upcoming = byType("recurring_rules").filter(row => data(row).next_date >= today()).sort((a, b) => String(data(a).next_date).localeCompare(String(data(b).next_date))).slice(0, 5);
    const description = { personal: "Your net worth, income, spending, savings, and goals.", family: "Your household balance, budget health, shared goals, and upcoming records.", group: "Your group balance, inflow, outflow, budgets, and circle activity." }[kind];
    mount.innerHTML = `<header class="panel-head"><div><h2>Overview</h2><p>${esc(description)}</p></div><button class="primary" type="button" data-new="transactions">Add transaction</button></header>${periodControl()}<section class="metrics"><article class="metric"><span>${kind === "group" ? "Group balance" : "Net worth"}</span><strong>${esc(money(workspaceNetUsd(workspaceId)))}</strong></article><article class="metric"><span>${kind === "group" ? "Inflow" : "Income"}</span><strong>${esc(money(income))}</strong></article><article class="metric"><span>${kind === "group" ? "Outflow" : "Spending"}</span><strong>${esc(money(spending))}</strong></article><article class="metric"><span>${kind === "personal" ? "Savings rate" : "Net cash flow"}</span><strong>${kind === "personal" ? `${income ? Math.round(cashflow / income * 100) : 0}%` : esc(money(cashflow))}</strong></article></section><div class="overview-grid"><section class="chart-card"><h3>Top categories</h3>${[...categoryTotals].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([name, value]) => `<p><span>${esc(name)}</span><strong>${esc(money(value))}</strong></p>`).join("") || "<p>No categorized spending in this period.</p>"}</section><section class="chart-card"><h3>Upcoming recurring records</h3>${upcoming.map(row => `<p><span>${esc(data(row).payee || data(row).name)}</span><strong>${esc(dateLabel(data(row).next_date))}</strong></p>`).join("") || "<p>No upcoming recurring records.</p>"}</section></div><h3>Recent activity</h3>${transactionTable(recent, false)}`;
  };

  const renderAccounts = mount => {
    const rows = byType("accounts");
    mount.innerHTML = `<header class="panel-head"><div><h2>Accounts</h2><p>Cash, bank, Mobile Money, investments, assets, and liabilities.</p></div><button class="primary" type="button" data-new="accounts">Add account</button></header>${table(["Name", "Type", "Balance", "Currency", ""], rows.map(row => `<tr><td><strong>${esc(data(row).name)}</strong></td><td>${esc(data(row).account_type.replaceAll("_", " "))}</td><td>${esc(money(accountBalanceUsd(row)))}</td><td>${esc(data(row).native_currency_code)}</td><td>${rowActions("accounts", row)}</td></tr>`))}`;
  };

  const transactionTable = (rows, actions = true) => table(["Date", "Payee", "Account", "Status", "Amount", ...(actions ? [""] : [])], rows.map(row => {
    const tx = data(row), account = find("accounts", tx.account), sign = transactionDirection(tx.transaction_type);
    const attachmentRows = state.attachments.filter(item => item.transaction_long_id === row.long_id && item.status === "active");
    return `<tr><td>${esc(dateLabel(tx.transaction_date))}</td><td><strong>${esc(tx.payee || tx.transaction_type.replaceAll("_", " "))}</strong><small>${esc(tx.note || "")}</small>${attachmentRows.length ? `<span class="attachment-list">${attachmentRows.map(item => `<button class="link-button" type="button" data-download-attachment="${esc(item.long_id)}">📎 ${esc(item.original_name)}</button>`).join("")}</span>` : ""}</td><td>${esc(data(account).name || "")}</td><td><span class="badge">${esc(tx.state)}</span></td><td class="${sign < 0 ? "negative" : "positive"}">${esc(money(sign * Number(tx.amount_usd_cents || 0)))}</td>${actions ? `<td>${rowActions("transactions", row, `<button class="link-button" type="button" data-attach="${esc(row.long_id)}">Attach</button>`)}</td>` : ""}</tr>`;
  }));

  const renderTransactions = mount => {
    const search = mount.dataset.search || "";
    const status = mount.dataset.status || "";
    const tag = mount.dataset.tag || "";
    const sort = mount.dataset.sort || "date_desc";
    let rows = byType("transactions").filter(inSelectedPeriod).filter(row => !status || data(row).state === status).filter(row => !tag || normalizeTagNames(data(row).tags).split(", ").some(value => value.toLocaleLowerCase() === tag.toLocaleLowerCase())).filter(row => !search || `${data(row).payee} ${data(row).note}`.toLowerCase().includes(search.toLowerCase()));
    rows.sort((a, b) => sort === "date_asc" ? String(data(a).transaction_date).localeCompare(String(data(b).transaction_date)) : sort === "amount_desc" ? Number(data(b).amount_usd_cents) - Number(data(a).amount_usd_cents) : sort === "amount_asc" ? Number(data(a).amount_usd_cents) - Number(data(b).amount_usd_cents) : sort === "payee" ? String(data(a).payee).localeCompare(String(data(b).payee)) : String(data(b).transaction_date).localeCompare(String(data(a).transaction_date)));
    mount.innerHTML = `<header class="panel-head"><div><h2>Activity</h2><p>Income, expenses, withdrawals, deposits, and balanced transfers.</p></div><div class="panel-actions"><button class="ghost" type="button" data-import>Import</button><button class="ghost" type="button" data-reconcile>Reconcile</button><button class="primary" type="button" data-new="transactions">Add transaction</button></div></header>${periodControl()}<div class="filters"><input type="search" data-filter-search placeholder="Search" value="${esc(search)}"><select data-filter-status>${optionHtml([{ value: "", label: "All statuses" }, "pending", "cleared", "reconciled"], status)}</select><select data-filter-tag>${optionHtml([{ value: "", label: "All tags" }, ...byType("tags").filter(row => !data(row).archived).map(row => ({ value: data(row).name, label: data(row).name }))], tag)}</select><select data-filter-sort>${optionHtml([{ value: "date_desc", label: "Newest first" }, { value: "date_asc", label: "Oldest first" }, { value: "amount_desc", label: "Amount high to low" }, { value: "amount_asc", label: "Amount low to high" }, { value: "payee", label: "Payee A-Z" }], sort)}</select></div>${transactionTable(rows)}`;
    mount.dataset.search = search; mount.dataset.status = status; mount.dataset.tag = tag; mount.dataset.sort = sort;
  };

  const renderGeneric = (type, mount = document.getElementById("workspacePanel")) => {
    const config = recordConfigs[type], rows = byType(type);
    const headers = config.fields.filter(spec => !["textarea", "checkbox"].includes(spec.type)).slice(0, 6).map(spec => spec.label);
    mount.innerHTML = `<header class="panel-head"><div><h2>${esc(config.title.replace("What-if scenario", "Forecast"))}${type.endsWith("s") ? "" : "s"}</h2></div><button class="primary" type="button" data-new="${type}">Add ${esc(config.title.toLowerCase())}</button></header>${table([...headers, ""], rows.map(row => `<tr>${config.fields.filter(spec => !["textarea", "checkbox"].includes(spec.type)).slice(0, 6).map(spec => {
      const value = data(row)[spec.name];
      if (spec.type === "money") return `<td>${esc(value)}</td>`;
      if (spec.type === "date") return `<td>${esc(dateLabel(value))}</td>`;
      if (spec.type === "dynamic") return `<td>${esc(data(find(spec.name === "account" ? "accounts" : spec.name === "goal" ? "goals" : spec.name === "circle" ? "circles" : spec.name === "member" ? "circle_members" : spec.name, value)).name || value)}</td>`;
      return `<td>${esc(value ?? "")}</td>`;
    }).join("")}<td>${rowActions(type, row)}</td></tr>`))}`;
  };

  const budgetActual = budget => {
    const item = data(budget), start = new Date(`${item.start_date}T00:00:00Z`), end = new Date(start);
    end.setUTCMonth(end.getUTCMonth() + ({ monthly: 1, quarterly: 3, yearly: 12 }[item.period_type] || 1));
    return byType("transactions").filter(row => data(row).transaction_type === "expense" && data(row).transaction_date >= item.start_date && data(row).transaction_date < end.toISOString().slice(0, 10))
      .filter(row => !item.category || (data(row).splits || []).some(split => split.category.toLowerCase() === item.category.toLowerCase()))
      .reduce((sum, row) => {
        if (!item.category) return sum + Number(data(row).amount_usd_cents || 0);
        const tx = data(row), split = (tx.splits || []).find(value => value.category.toLowerCase() === item.category.toLowerCase());
        return sum + Math.round(Number(tx.amount_usd_cents || 0) * Number(split?.amount_cents || 0) / Number(tx.amount_cents || 1));
      }, 0);
  };
  const renderBudgets = mount => {
    renderGeneric("budgets", mount);
    const rows = byType("budgets");
    if (!rows.length) return;
    mount.insertAdjacentHTML("beforeend", `<section class="report-grid">${rows.map(row => {
      const planned = Number(data(row).planned_usd_cents || 0), actual = budgetActual(row), remaining = planned - actual, percent = planned ? Math.min(100, Math.round(actual / planned * 100)) : 0;
      const status = percent >= 100 ? "Exceeded" : percent >= 80 ? "Watch" : "On track";
      return `<article class="chart-card"><header><h3>${esc(data(row).name)}</h3><span class="health is-${status.toLowerCase().replaceAll(" ", "-")}">${status}</span></header><p>Planned ${esc(money(planned))}<br>Actual ${esc(money(actual))}<br>Remaining ${esc(money(remaining))}</p><div class="bar"><span style="width:${percent}%"></span></div></article>`;
    }).join("")}</section>`);
  };

  const renderGoals = mount => {
    renderGeneric("goals", mount);
    const rows = byType("goals");
    if (!rows.length) return;
    const dueSoon = new Date(); dueSoon.setUTCDate(dueSoon.getUTCDate() + 30);
    mount.insertAdjacentHTML("beforeend", `<section class="report-grid">${rows.map(row => {
      const item = data(row), target = Math.max(1, Number(item.target_usd_cents || 0)), current = Math.max(0, Number(item.current_usd_cents || 0)), percent = Math.min(100, Math.round(current / target * 100));
      const status = current >= target ? "Completed" : item.target_date && item.target_date < today() ? "Overdue" : item.target_date && item.target_date <= dueSoon.toISOString().slice(0, 10) ? "Due soon" : "In progress";
      return `<article class="chart-card"><header><h3>${esc(item.name)}</h3><span class="health is-${status.toLowerCase().replaceAll(" ", "-")}">${status}</span></header><strong>${percent}%</strong><div class="bar"><span style="width:${percent}%"></span></div><p>Remaining ${esc(money(Math.max(0, target - current)))}${item.target_date ? `<br>Target ${esc(dateLabel(item.target_date))}` : ""}</p></article>`;
    }).join("")}</section>`);
  };

  const recurringSuggestions = () => {
    const cutoff = new Date(); cutoff.setUTCFullYear(cutoff.getUTCFullYear() - 1); const cutoffDate = cutoff.toISOString().slice(0, 10), groups = new Map();
    byType("transactions").filter(row => data(row).transaction_date >= cutoffDate && ["income", "expense"].includes(data(row).transaction_type) && data(row).payee && ["", "manual", "import"].includes(data(row).source || "")).forEach(row => {
      const item = data(row), key = `${item.account}|${item.transaction_type}|${item.native_currency_code}|${item.payee.trim().toLocaleLowerCase()}`;
      if (!groups.has(key)) groups.set(key, []); groups.get(key).push(row);
    });
    const ignored = new Set(JSON.parse(localStorage.getItem(`financeIgnoredRecurring:${workspaceId}`) || "[]")), output = [];
    groups.forEach((rows, key) => {
      if (rows.length < 2 || ignored.has(key)) return;
      rows.sort((a, b) => String(data(a).transaction_date).localeCompare(String(data(b).transaction_date)));
      const intervals = rows.slice(1).map((row, index) => Math.round((new Date(`${data(row).transaction_date}T00:00:00Z`) - new Date(`${data(rows[index]).transaction_date}T00:00:00Z`)) / 86400000)), average = intervals.reduce((sum, value) => sum + value, 0) / intervals.length;
      const frequency = average >= 6 && average <= 8 ? "weekly" : average >= 26 && average <= 35 ? "monthly" : average >= 80 && average <= 100 ? "quarterly" : average >= 350 && average <= 380 ? "yearly" : "";
      if (!frequency) return;
      const amounts = rows.map(row => Number(data(row).amount_cents || 0)).sort((a, b) => a - b), median = amounts[Math.floor((amounts.length - 1) / 2)];
      if (!median || (amounts.at(-1) - amounts[0]) / median > .2) return;
      const last = data(rows.at(-1)), next = new Date(`${last.transaction_date}T00:00:00Z`);
      if (frequency === "weekly") next.setUTCDate(next.getUTCDate() + 7); else if (frequency === "quarterly") next.setUTCMonth(next.getUTCMonth() + 3); else if (frequency === "yearly") next.setUTCFullYear(next.getUTCFullYear() + 1); else next.setUTCMonth(next.getUTCMonth() + 1);
      output.push({ key, name: last.payee, payee: last.payee, account: last.account, transaction_type: last.transaction_type, recurring_kind: last.transaction_type === "income" ? "income" : "bill", value: amount(median), frequency, next_date: next.toISOString().slice(0, 10), category: last.splits?.[0]?.category || "", tags: last.tags || "", occurrences: rows.length });
    });
    return output;
  };

  const renderRecurring = mount => {
    const rules = byType("recurring_rules"), suggestions = recurringSuggestions();
    mount.innerHTML = `<header class="panel-head"><div><h2>Recurring & subscriptions</h2><p>Review repeating records. No rule is created silently.</p></div><div class="panel-actions"><button class="ghost" type="button" data-generate-recurring>Review due</button><button class="primary" type="button" data-new="recurring_rules">Add rule</button></div></header>${suggestions.length ? `<section class="suggestions"><header><h3>Suggested recurring records</h3><p>Confirm or ignore patterns detected from the last 12 months.</p></header>${suggestions.map(item => `<article><div><strong>${esc(item.payee)}</strong><small>${item.occurrences} records, ${esc(item.frequency)}</small></div><div class="row-actions"><button class="primary" type="button" data-accept-suggestion="${esc(encodeURIComponent(JSON.stringify(item)))}">Review rule</button><button class="ghost" type="button" data-ignore-suggestion="${esc(encodeURIComponent(item.key))}">Ignore</button></div></article>`).join("")}</section>` : ""}${table(["Name", "Kind", "Account", "Frequency", "Next date", "Amount", ""], rules.map(row => `<tr><td>${esc(data(row).payee || data(row).name)}</td><td>${esc(data(row).recurring_kind || data(row).transaction_type)}</td><td>${esc(data(find("accounts", data(row).account)).name || "")}</td><td>${esc(data(row).frequency)}</td><td>${esc(dateLabel(data(row).next_date))}</td><td>${esc(money(data(row).amount_usd_cents))}</td><td>${rowActions("recurring_rules", row)}</td></tr>`))}`;
  };

  const renderTaxonomy = mount => {
    const categories = byType("categories"), tags = byType("tags");
    mount.innerHTML = `<header class="panel-head"><div><h2>Categories & tags</h2><p>Manage consistent labels for transactions, budgets, reports, and search.</p></div></header><div class="taxonomy-grid"><section><header><h3>Categories</h3><button class="primary" type="button" data-new="categories">Add category</button></header>${table(["Name", "Type", "Status", ""], categories.map(row => `<tr><td>${esc(data(row).name)}</td><td>${esc(data(row).category_type)}</td><td>${data(row).archived ? "Archived" : "Active"}</td><td>${data(row).archived ? `<button class="link-button" type="button" data-restore-taxonomy="categories" data-id="${esc(row.long_id)}">Restore</button>` : rowActions("categories", row)}</td></tr>`))}</section><section><header><h3>Tags</h3><button class="primary" type="button" data-new="tags">Add tag</button></header>${table(["Name", "Status", ""], tags.map(row => `<tr><td>${esc(data(row).name)}</td><td>${data(row).archived ? "Archived" : "Active"}</td><td>${data(row).archived ? `<button class="link-button" type="button" data-restore-taxonomy="tags" data-id="${esc(row.long_id)}">Restore</button>` : rowActions("tags", row)}</td></tr>`))}</section></div>`;
  };

  const debtBalance = debt => Math.max(0, Number(data(debt).balance_usd_cents || 0) - byType("debt_payments").filter(row => data(row).debt === debt.long_id).reduce((sum, row) => sum + Number(data(row).principal_usd_cents || 0), 0));
  const payoffMonths = debt => {
    let balance = Number(data(debt).balance_cents || 0), months = 0;
    const monthlyRate = Number(data(debt).apr_bps || 0) / 10000 / 12, payment = Number(data(debt).minimum_payment_cents || 0) + Number(data(debt).extra_payment_cents || 0);
    while (balance > 0 && months < 1200) {
      const interest = Math.round(balance * monthlyRate);
      if (payment <= interest) return "Payment too low";
      balance -= Math.min(balance, payment - interest); months++;
    }
    return `${months} months`;
  };
  const renderDebts = mount => {
    const rows = byType("debts");
    mount.innerHTML = `<header class="panel-head"><div><h2>Debts</h2><p>Track balances and compare payoff strategies.</p></div><div class="panel-actions">${rows.length ? '<button class="ghost" type="button" data-new="debt_payments">Record payment</button>' : ""}<button class="primary" type="button" data-new="debts">Add debt</button></div></header>${table(["Name", "Balance", "APR", "Payment", "Payoff", ""], rows.map(row => `<tr><td>${esc(data(row).name)}</td><td>${esc(money(debtBalance(row)))}</td><td>${(Number(data(row).apr_bps || 0) / 100).toFixed(2)}%</td><td>${esc(amount(Number(data(row).minimum_payment_cents || 0) + Number(data(row).extra_payment_cents || 0)))}</td><td>${esc(payoffMonths(row))}</td><td>${rowActions("debts", row)}</td></tr>`))}<section class="report-grid"><article class="chart-card"><h3>Snowball</h3><p>${esc([...rows].sort((a, b) => debtBalance(a) - debtBalance(b)).map(row => data(row).name).join(" → ") || "No debts")}</p></article><article class="chart-card"><h3>Avalanche</h3><p>${esc([...rows].sort((a, b) => Number(data(b).apr_bps) - Number(data(a).apr_bps)).map(row => data(row).name).join(" → ") || "No debts")}</p></article></section>`;
  };

  const renderInvestments = mount => {
    const rows = byType("investments");
    const total = rows.reduce((sum, row) => sum + Number(data(row).current_value_usd_cents || 0), 0);
    mount.innerHTML = `<header class="panel-head"><div><h2>Investments</h2><p>Manual holdings, units, valuations, gains, and allocation.</p></div><button class="primary" type="button" data-new="investments">Add holding</button></header><section class="metrics"><article class="metric"><span>Portfolio value</span><strong>${esc(money(total))}</strong></article><article class="metric"><span>Total gain</span><strong>${esc(money(rows.reduce((sum, row) => sum + Number(data(row).current_value_usd_cents || 0) - Number(data(row).cost_basis_usd_cents || 0), 0)))}</strong></article></section>${table(["Holding", "Units", "Cost basis", "Value", "Gain", ""], rows.map(row => `<tr><td><strong>${esc(data(row).name)}</strong><small>${esc(data(row).symbol || "")}</small></td><td>${esc(data(row).units)}</td><td>${esc(money(data(row).cost_basis_usd_cents))}</td><td>${esc(money(data(row).current_value_usd_cents))}</td><td>${esc(money(Number(data(row).current_value_usd_cents) - Number(data(row).cost_basis_usd_cents)))}</td><td>${rowActions("investments", row)}</td></tr>`))}<section class="chart-card"><h3>Allocation</h3><div class="chart-list">${rows.map(row => `<div class="chart-row"><span>${esc(data(row).name)}</span><div class="bar"><span style="width:${total ? Math.round(Number(data(row).current_value_usd_cents) / total * 100) : 0}%"></span></div><strong>${total ? Math.round(Number(data(row).current_value_usd_cents) / total * 100) : 0}%</strong></div>`).join("")}</div></section>`;
  };

  const recurringMonthlyUsd = rule => Number(data(rule).amount_usd_cents || 0) * ({ weekly: 52 / 12, monthly: 1, quarterly: 1 / 3, yearly: 1 / 12 }[data(rule).frequency] || 1) * (data(rule).transaction_type === "income" ? 1 : -1);
  const renderForecast = mount => {
    const base = byType("recurring_rules").reduce((sum, row) => sum + recurringMonthlyUsd(row), 0);
    const scenarios = [{ long_id: "", data: { name: "Baseline", months: 12, monthly_adjustment_usd_cents: 0 } }, ...byType("scenarios")];
    const net = workspaceNetUsd(workspaceId);
    mount.innerHTML = `<header class="panel-head"><div><h2>Forecast</h2><p>Recurring baseline plus saved what-if scenarios.</p></div><button class="primary" type="button" data-new="scenarios">Add scenario</button></header><section class="report-grid">${scenarios.map(row => {
      const item = data(row), months = Math.max(1, Math.min(120, Number(item.months || 12))), monthly = base + Number(item.monthly_adjustment_usd_cents || 0), projected = net + monthly * months;
      return `<article class="chart-card"><h3>${esc(item.name)}</h3><p>Monthly cash flow ${esc(money(monthly))}<br>${months}-month net worth ${esc(money(projected))}</p><div class="bar"><span style="width:${Math.min(100, Math.max(2, Math.abs(projected) / Math.max(1, Math.abs(net) + Math.abs(monthly * months)) * 100))}%"></span></div>${row.long_id ? rowActions("scenarios", row) : ""}</article>`;
    }).join("")}</section>`;
  };

  const renderReports = mount => {
    const tx = byType("transactions").filter(inSelectedPeriod), accounts = byType("accounts"), budgets = byType("budgets"), goals = byType("goals"), debts = byType("debts"), investments = byType("investments"), circles = byType("circle_entries");
    const months = {};
    tx.filter(row => ["income", "expense"].includes(data(row).transaction_type)).forEach(row => {
      const key = String(data(row).transaction_date).slice(0, 7);
      months[key] ||= { income: 0, expense: 0 };
      months[key][data(row).transaction_type] += Number(data(row).amount_usd_cents || 0);
    });
    const income = tx.filter(row => data(row).transaction_type === "income").reduce((sum, row) => sum + Number(data(row).amount_usd_cents || 0), 0), spending = tx.filter(row => data(row).transaction_type === "expense").reduce((sum, row) => sum + Number(data(row).amount_usd_cents || 0), 0);
    mount.innerHTML = `<header class="panel-head"><div><h2>Reports</h2><p>Cash flow, net worth, budgets, goals, debts, investments, currency exposure, circles, and comparisons.</p></div><div class="panel-actions"><button class="ghost" type="button" data-export-csv>Export CSV</button><button class="ghost" type="button" data-export-json>JSON backup</button><button class="primary" type="button" data-print>Print A4 report</button></div></header>${periodControl()}<section class="metrics"><article class="metric"><span>Income</span><strong>${esc(money(income))}</strong></article><article class="metric"><span>Spending</span><strong>${esc(money(spending))}</strong></article><article class="metric"><span>Net cash flow</span><strong>${esc(money(income - spending))}</strong></article><article class="metric"><span>Transactions</span><strong>${tx.length}</strong></article></section>
      <h3>Cash-flow comparison</h3>${table(["Period", "Income", "Expenses", "Net"], Object.entries(months).sort(([a], [b]) => b.localeCompare(a)).map(([period, item]) => `<tr><td>${esc(period)}</td><td>${esc(money(item.income))}</td><td>${esc(money(item.expense))}</td><td>${esc(money(item.income - item.expense))}</td></tr>`))}
      <section class="report-grid"><article class="chart-card"><h3>Budget variance</h3>${budgets.map(row => `<p>${esc(data(row).name)}: ${esc(money(Number(data(row).planned_usd_cents) - budgetActual(row)))}</p>`).join("") || "<p>No budgets</p>"}</article><article class="chart-card"><h3>Currency exposure</h3>${Object.entries(accounts.reduce((out, row) => { out[data(row).native_currency_code] = (out[data(row).native_currency_code] || 0) + accountBalanceUsd(row); return out; }, {})).map(([code, value]) => `<p>${esc(code)}: ${esc(money(value))}</p>`).join("") || "<p>No accounts</p>"}</article><article class="chart-card"><h3>Circle ledger</h3><p>${circles.length} entries recorded separately from net worth.</p></article></section>`;
  };

  const circlePosition = member => byType("circle_entries").filter(row => data(row).member === member.long_id).reduce((sum, row) => sum + (["contribution", "repayment"].includes(data(row).entry_type) ? 1 : -1) * Number(data(row).amount_usd_cents || 0), 0);
  const renderCircles = mount => {
    const workspace = find("workspaces", workspaceId), kind = workspaceKind(workspace), circles = byType("circles"), members = byType("circle_members"), entries = byType("circle_entries"), canCreate = kind !== "personal";
    mount.innerHTML = `<header class="panel-head"><div><h2>Circles</h2><p>Offline contribution and loan ledgers. They never affect accounts or net worth.</p>${kind === "personal" ? "<small>Existing circles remain visible. Change this workspace to Family or Group to create another.</small>" : ""}</div><div class="panel-actions">${canCreate ? '<button class="ghost" type="button" data-new="circles">Add circle</button>' : ""}${circles.length ? '<button class="ghost" type="button" data-new="circle_members">Add person</button>' : ""}${members.length ? '<button class="primary" type="button" data-new="circle_entries">Record entry</button>' : ""}</div></header>${table(["Circle", "Person", "Position"], members.map(row => `<tr><td>${esc(data(find("circles", data(row).circle)).name || "")}</td><td>${esc(data(row).name)}</td><td>${esc(money(circlePosition(row)))}</td></tr>`))}<h3>Circle ledger entries</h3>${table(["Date", "Circle", "Person", "Type", "Amount", ""], entries.map(row => `<tr><td>${esc(dateLabel(data(row).entry_date))}</td><td>${esc(data(find("circles", data(row).circle)).name || "")}</td><td>${esc(data(find("circle_members", data(row).member)).name || "")}</td><td>${esc(data(row).entry_type)}</td><td>${esc(money(data(row).amount_usd_cents))}</td><td>${rowActions("circle_entries", row)}</td></tr>`))}`;
  };
  const renderSettings = (mount, workspace) => {
    const ignoredCount = JSON.parse(localStorage.getItem(`financeIgnoredRecurring:${workspaceId}`) || "[]").length;
    mount.innerHTML = `<header class="panel-head"><div><h2>Settings</h2><p>Workspace identity, type, display, and private local data.</p></div><div class="panel-actions"><button class="ghost" type="button" data-restore-json>Restore JSON backup</button><button class="ghost" type="button" data-edit="workspaces" data-id="${esc(workspace.long_id)}">Edit workspace</button></div></header><section class="help-grid"><article class="help-card"><h3>Workspace type</h3><p><span class="type-badge is-${workspaceKind(workspace)}">${esc(workspaceKind(workspace))}</span> Type changes preserve every local financial record.</p></article><article class="help-card"><h3>Home totals</h3><p>${data(workspace).include_home === false ? "Excluded" : "Included"} in Home totals. Balances are ${data(workspace).hide_balances ? "hidden" : "visible"} by default.</p></article><article class="help-card"><h3>Portable collaboration</h3><p>Family and Group organization is offline and single-device. Invitations, accounts, and hosted collaboration are not included.</p></article><article class="help-card"><h3>Ignored suggestions</h3><p>${ignoredCount} recurring pattern${ignoredCount === 1 ? "" : "s"} ignored.</p>${ignoredCount ? '<button class="ghost" type="button" data-restore-suggestions>Restore suggestions</button>' : ""}</article><article class="help-card"><h3>Local storage</h3><p>Hiding Finance Manager keeps every record and attachment on this device.</p></article></section>`;
  };

  const render = () => {
    topNavigation();
    if (workspaceId) renderWorkspace();
    else if (topTab === "workspaces") renderWorkspaces();
    else if (topTab === "help") renderHelp();
    else renderHome();
  };

  const workspaceConfig = {
    title: "Workspace",
    fields: [
      field("name", "Name", "text", { required: true, maxlength: 160 }),
      field("workspace_kind", "Workspace type", "select", { options: ["personal", "family", "group"] }),
      field("native_currency_code", "Currency", "currency"),
      field("include_home", "Include in Home totals", "checkbox", { value: true }),
      field("hide_balances", "Hide balances by default", "checkbox"),
      field("sample", "Sample workspace", "checkbox")
    ]
  };
  recordConfigs.workspaces = workspaceConfig;

  const formRecordSave = async event => {
    event.preventDefault();
    if (!activeEditor) return;
    const values = editorValues(), { recordType, row } = activeEditor;
    try {
      if (recordType === "workspaces") {
        const recordData = { ...data(row), ...values, workspace_kind: ["personal", "family", "group"].includes(values.workspace_kind) ? values.workspace_kind : "personal", include_home: values.include_home === "1", hide_balances: values.hide_balances === "1", sample: values.sample === "1" };
        await save("workspaces", recordData, { long_id: row?.long_id || "", workspace_long_id: "" });
      } else if (recordType === "transactions" && values.transaction_type === "transfer" && !row) {
        if (!values.account || !values.destination_account || values.account === values.destination_account) throw new Error("Choose two different accounts.");
        const from = find("accounts", values.account), to = find("accounts", values.destination_account), amountCents = cents(values.value);
        if (amountCents <= 0) throw new Error("Amount must be positive.");
        const group = id("transfer"), fromSnap = snapshot(amountCents, data(from).native_currency_code);
        const toNative = Math.round(fromSnap.usd_cents * Number(currency(data(to).native_currency_code).rate || 1)), toSnap = snapshot(toNative, data(to).native_currency_code);
        const common = { transaction_date: values.transaction_date, payee: values.payee, tags: values.tags, note: values.note, state: values.state, transfer_group: group };
        await saveBatch([
          { record_type: "transactions", workspace_long_id: workspaceId, data: { ...common, transaction_type: "transfer_out", account: from.long_id, destination_account: to.long_id, amount_cents: amountCents, amount_usd_cents: fromSnap.usd_cents, fx_rate: fromSnap.fx_rate, native_currency_code: data(from).native_currency_code, splits: [] } },
          { record_type: "transactions", workspace_long_id: workspaceId, data: { ...common, transaction_type: "transfer_in", account: to.long_id, destination_account: from.long_id, amount_cents: toNative, amount_usd_cents: toSnap.usd_cents, fx_rate: toSnap.fx_rate, native_currency_code: data(to).native_currency_code, splits: [] } }
        ]);
      } else {
        if (recordType === "circles" && workspaceKind(find("workspaces", workspaceId)) === "personal") throw new Error("Change this workspace to Family or Group before creating a circle.");
        const normalized = normalizeRecord(recordType, values, data(row));
        if (row && ["transfer_in", "transfer_out"].includes(data(row).transaction_type)) {
          normalized.account = data(row).account;
          normalized.destination_account = data(row).destination_account;
          normalized.amount_cents = data(row).amount_cents;
          normalized.amount_usd_cents = data(row).amount_usd_cents;
          normalized.fx_rate = data(row).fx_rate;
          normalized.native_currency_code = data(row).native_currency_code;
          normalized.transaction_type = data(row).transaction_type;
          normalized.transfer_group = data(row).transfer_group;
        }
        await save(recordType, normalized, { long_id: row?.long_id || "" });
        if (["transactions", "recurring_rules"].includes(recordType)) await ensureTags(normalized.tags);
      }
      dialog.close();
      notify("Saved locally.");
      render();
    } catch (error) {
      notify(error.message, false);
    }
  };

  const sampleWorkspace = async () => {
    try {
      const workspace = { record_type: "workspaces", workspace_long_id: "", data: { name: "Sample household", workspace_kind: "family", native_currency_code: "USD", include_home: true, hide_balances: false, sample: true } };
      const result = await bridge("batch_save", { records: [workspace], sample: true });
      await reload();
      workspaceId = result.workspace_long_id || byType("workspaces", "")[0]?.long_id || "";
      notify("Sample workspace added.");
      render();
    } catch (error) { notify(error.message, false); }
  };

  const generateRecurring = async () => {
    const due = byType("recurring_rules").filter(row => data(row).next_date <= today());
    if (!due.length) { notify("No recurring entries are due."); return; }
    const rows = due.map(rule => {
      const item = data(rule);
      return { record_type: "transactions", workspace_long_id: workspaceId, data: { transaction_type: item.transaction_type, account: item.account, amount_cents: item.amount_cents, amount_usd_cents: item.amount_usd_cents, fx_rate: item.fx_rate, native_currency_code: item.native_currency_code, transaction_date: item.next_date, payee: item.payee || item.name, note: item.note, tags: item.tags || "", state: "pending", recurring_rule: rule.long_id, fingerprint: `recurring|${rule.long_id}|${item.next_date}`, splits: item.category ? [{ category: item.category, category_name: data(find("categories", item.category)).name || "", amount_cents: item.amount_cents, note: "" }] : [] } };
    });
    try {
      await saveBatch(rows);
      for (const rule of due) {
        const next = new Date(`${data(rule).next_date}T00:00:00Z`);
        const frequency = data(rule).frequency;
        if (frequency === "weekly") next.setUTCDate(next.getUTCDate() + 7);
        else if (frequency === "quarterly") next.setUTCMonth(next.getUTCMonth() + 3);
        else if (frequency === "yearly") next.setUTCFullYear(next.getUTCFullYear() + 1);
        else next.setUTCMonth(next.getUTCMonth() + 1);
        await save("recurring_rules", { ...data(rule), next_date: next.toISOString().slice(0, 10) }, { long_id: rule.long_id });
      }
      notify(`${due.length} pending transaction${due.length === 1 ? "" : "s"} created for review.`);
      render();
    } catch (error) { notify(error.message, false); }
  };

  const reconcile = async () => {
    const accounts = accountOptions();
    if (!accounts.length) { notify("Add an account first.", false); return; }
    const accountId = prompt(`Account ID:\n${accounts.map(item => `${item.label}: ${item.value}`).join("\n")}`, accounts[0].value);
    if (!accountId) return;
    const statementDate = prompt("Statement date (YYYY-MM-DD)", today());
    if (!/^\d{4}-\d{2}-\d{2}$/.test(statementDate || "")) return;
    try {
      const rows = byType("transactions").filter(row => data(row).account === accountId && data(row).state === "cleared" && data(row).transaction_date <= statementDate);
      await saveBatch(rows.map(row => ({ record_type: "transactions", long_id: row.long_id, workspace_long_id: workspaceId, version: row.version, data: { ...data(row), state: "reconciled" } })));
      notify(`${rows.length} transaction${rows.length === 1 ? "" : "s"} reconciled.`);
      render();
    } catch (error) { notify(error.message, false); }
  };

  const download = (name, content, type) => {
    const url = URL.createObjectURL(new Blob([content], { type }));
    const link = document.createElement("a");
    link.href = url; link.download = name; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  const exportCsv = () => {
    const header = ["date", "type", "account", "amount_cents", "native_currency", "usd_cents", "state", "payee", "tags", "note"];
    const lines = byType("transactions").map(row => {
      const item = data(row);
      return [item.transaction_date, item.transaction_type, data(find("accounts", item.account)).name, item.amount_cents, item.native_currency_code, item.amount_usd_cents, item.state, item.payee, item.tags, item.note].map(value => `"${String(value ?? "").replaceAll('"', '""')}"`).join(",");
    });
    download(`finance-transactions-${today()}.csv`, [header.join(","), ...lines].join("\r\n"), "text/csv");
  };
  const exportJson = async () => {
    const attachments = [];
    for (const item of state.attachments.filter(value => value.workspace_long_id === workspaceId && value.status === "active")) {
      const file = await bridge("attachment_get", { long_id: item.long_id });
      attachments.push({ transaction_long_id: item.transaction_long_id, original_name: file.original_name || item.original_name, mime_type: file.mime_type || item.mime_type, content_base64: file.content_base64 });
    }
    download(`finance-backup-${today()}.json`, JSON.stringify({ schema: 2, plugin: "finance-manager", exported_at: new Date().toISOString(), workspace: find("workspaces", workspaceId), records: state.records.filter(row => row.workspace_long_id === workspaceId), attachments, period_preference: periodPreference() }, null, 2), "application/json");
  };
  const restoreJsonBackup = async file => {
    const backup = JSON.parse(await file.text());
    if (backup?.plugin !== "finance-manager" || ![1, 2].includes(Number(backup.schema)) || !backup.workspace || !Array.isArray(backup.records)) throw new Error("Choose a Finance Manager schema 1 or schema 2 JSON backup.");
    const sourceWorkspace = data(backup.workspace), restoredWorkspace = { ...sourceWorkspace, name: `${sourceWorkspace.name || "Restored workspace"} - restored`, workspace_kind: ["personal", "family", "group"].includes(sourceWorkspace.workspace_kind) ? sourceWorkspace.workspace_kind : "personal", sample: false };
    const workspaceResult = await save("workspaces", restoredWorkspace, { workspace_long_id: "" }), newWorkspace = workspaceResult.long_id, idMap = new Map(), categoryMap = new Map();
    workspaceId = newWorkspace; panel = "overview";
    const records = backup.records.filter(row => row && typeof row === "object"), order = ["categories", "tags", "accounts", "goals", "budgets", "debts", "investments", "scenarios", "circles", "circle_members", "recurring_rules", "transactions", "debt_payments", "circle_entries"];
    const mapReference = value => idMap.get(String(value || "")) || String(value || "");
    const ensureLegacyCategory = async (value, type) => {
      const source = String(value || ""); if (!source) return ""; if (idMap.has(source)) return idMap.get(source);
      const key = `${type}|${source.toLocaleLowerCase()}`; if (categoryMap.has(key)) return categoryMap.get(key);
      const result = await save("categories", { name: source.slice(0, 120), category_type: type, parent: "", archived: false }, { workspace_long_id: newWorkspace }); categoryMap.set(key, result.long_id); return result.long_id;
    };
    for (const type of order) for (const row of records.filter(item => item.record_type === type)) {
      const item = { ...data(row) };
      for (const field of ["account", "destination_account", "goal", "debt", "circle", "member"]) if (item[field]) item[field] = mapReference(item[field]);
      if (type === "categories") item.parent = idMap.get(String(item.parent || "")) || "";
      if (type === "transactions") {
        item.splits = await Promise.all((item.splits || []).map(async split => { const category = await ensureLegacyCategory(split.category, item.transaction_type === "income" ? "income" : "expense"); return { ...split, category, category_name: data(find("categories", category)).name || split.category_name || "" }; }));
        item.tags = normalizeTagNames(item.tags);
      }
      if (["budgets", "recurring_rules"].includes(type) && item.category) item.category = await ensureLegacyCategory(item.category, item.transaction_type === "income" ? "income" : "expense");
      if (type === "recurring_rules") item.recurring_kind = ["income", "bill", "subscription"].includes(item.recurring_kind) ? item.recurring_kind : item.transaction_type === "income" ? "income" : "bill";
      const result = await save(type, item, { workspace_long_id: newWorkspace }); if (row.long_id) idMap.set(row.long_id, result.long_id);
      if (["transactions", "recurring_rules"].includes(type)) await ensureTags(item.tags);
    }
    for (const row of records.filter(item => item.record_type === "categories" && data(item).parent && idMap.has(item.long_id) && idMap.has(data(item).parent))) {
      const restored = find("categories", idMap.get(row.long_id)); await save("categories", { ...data(restored), parent: idMap.get(data(row).parent) }, { long_id: restored.long_id });
    }
    for (const attachment of Array.isArray(backup.attachments) ? backup.attachments : []) {
      const transaction = idMap.get(String(attachment.transaction_long_id || ""));
      if (!transaction || !attachment.original_name || !attachment.content_base64) continue;
      await bridge("attachment_upload", { workspace_long_id: newWorkspace, transaction_long_id: transaction, original_name: attachment.original_name, mime_type: attachment.mime_type || "application/octet-stream", content_base64: attachment.content_base64 });
    }
    if (backup.period_preference) localStorage.setItem(`financePeriod:${newWorkspace}`, JSON.stringify(backup.period_preference));
    notify(`Backup restored into ${restoredWorkspace.name}.`); render();
  };

  const splitCsv = line => {
    const cells = []; let value = "", quoted = false;
    for (let index = 0; index < line.length; index++) {
      const char = line[index];
      if (char === '"' && quoted && line[index + 1] === '"') { value += '"'; index++; }
      else if (char === '"') quoted = !quoted;
      else if (char === "," && !quoted) { cells.push(value); value = ""; }
      else value += char;
    }
    cells.push(value);
    return cells;
  };
  const normalizeDate = value => {
    const text = String(value || "").trim();
    let match;
    if ((match = text.match(/^(\d{4})[-/]?(\d{2})[-/]?(\d{2})/))) return `${match[1]}-${match[2]}-${match[3]}`;
    if ((match = text.match(/^(\d{1,2})[/-](\d{1,2})[/-](\d{4})/))) return `${match[3]}-${match[2].padStart(2, "0")}-${match[1].padStart(2, "0")}`;
    return "";
  };
  const parseOfx = text => [...text.matchAll(/<STMTTRN>(.*?)(?:<\/STMTTRN>|<STMTTRN>|$)/gis)].map(match => {
    const tag = name => match[1].match(new RegExp(`<${name}>([^<\\r\\n]+)`, "i"))?.[1]?.trim() || "";
    const value = Number(tag("TRNAMT"));
    return { transaction_date: normalizeDate(tag("DTPOSTED").slice(0, 8)), value: Math.abs(value), transaction_type: value < 0 ? "expense" : "income", payee: tag("NAME"), note: tag("MEMO") };
  });
  const parseQif = text => text.trim().split(/\r?\n\^\s*/).map(block => {
    const entries = Object.fromEntries(block.split(/\r?\n/).filter(Boolean).map(line => [line[0], line.slice(1)]));
    const value = Number(String(entries.T || 0).replaceAll(",", ""));
    return { transaction_date: normalizeDate(entries.D), value: Math.abs(value), transaction_type: value < 0 ? "expense" : "income", payee: entries.P || "", note: entries.M || "" };
  });
  const importFingerprint = item => `${item.transaction_date}|${item.transaction_type}|${cents(item.value)}|${String(item.payee).trim().toLowerCase()}`;

  const previewImport = async () => {
    const file = importForm.elements.file.files[0];
    if (!file || file.size > 10 * 1024 * 1024) throw new Error("Choose a CSV, OFX, or QIF file up to 10 MB.");
    const extension = file.name.split(".").pop().toLowerCase(), text = await file.text();
    if (!["csv", "ofx", "qif"].includes(extension)) throw new Error("Only CSV, OFX, and QIF are supported.");
    if (extension === "ofx") importRows = parseOfx(text);
    else if (extension === "qif") importRows = parseQif(text);
    else {
      const lines = text.split(/\r?\n/).filter(Boolean), headers = splitCsv(lines.shift()).map(item => item.trim());
      const selected = name => importForm.elements[name].value;
      importRows = lines.map(line => {
        const row = Object.fromEntries(headers.map((header, index) => [header, splitCsv(line)[index] || ""]));
        const numeric = Number(String(row[selected("amountColumn")] || 0).replace(/[,\s]/g, ""));
        const stated = String(row[selected("typeColumn")] || "").toLowerCase();
        return { transaction_date: normalizeDate(row[selected("dateColumn")]), value: Math.abs(numeric), transaction_type: ["income", "deposit"].includes(stated) ? "income" : ["expense", "withdrawal"].includes(stated) ? "expense" : numeric < 0 ? "expense" : "income", payee: row[selected("payeeColumn")] || "", note: row[selected("noteColumn")] || "" };
      });
    }
    const existing = new Set(byType("transactions").map(row => data(row).fingerprint).filter(Boolean));
    importRows = importRows.map(item => ({ ...item, valid: /^\d{4}-\d{2}-\d{2}$/.test(item.transaction_date) && Number(item.value) > 0, fingerprint: importFingerprint(item) })).map(item => ({ ...item, duplicate: existing.has(item.fingerprint) }));
    const valid = importRows.filter(item => item.valid && !item.duplicate).length, duplicates = importRows.filter(item => item.duplicate).length;
    const preview = document.getElementById("importPreview");
    preview.hidden = false;
    preview.innerHTML = `<strong>${valid} ready, ${duplicates} duplicates, ${importRows.length - valid - duplicates} invalid</strong>${table(["Date", "Payee", "Amount", "Result"], importRows.slice(0, 10).map(item => `<tr><td>${esc(dateLabel(item.transaction_date))}</td><td>${esc(item.payee)}</td><td>${esc(item.value)}</td><td>${item.duplicate ? "Duplicate" : item.valid ? "Ready" : "Invalid"}</td></tr>`))}`;
    document.getElementById("previewImport").textContent = "Import valid rows";
    document.getElementById("previewImport").dataset.confirm = "1";
  };
  const applyImport = async () => {
    const account = find("accounts", importForm.elements.account.value);
    if (!account) throw new Error("Choose an account.");
    const records = importRows.filter(item => item.valid && !item.duplicate).map(item => {
      const snap = snapshot(cents(item.value), data(account).native_currency_code);
      return { record_type: "transactions", workspace_long_id: workspaceId, data: { ...item, account: account.long_id, amount_cents: snap.native_cents, amount_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, native_currency_code: data(account).native_currency_code, state: "cleared", source: "import", splits: [] } };
    });
    const result = await saveBatch(records);
    importDialog.close();
    notify(`${result.added || 0} imported, ${result.duplicates || 0} duplicates, ${result.invalid || 0} invalid, ${result.quota || 0} above the Lite limit.`);
    render();
  };

  const attach = async transactionId => {
    const picker = document.createElement("input");
    picker.type = "file"; picker.multiple = true; picker.accept = ".jpg,.jpeg,.png,.webp,.pdf";
    picker.addEventListener("change", async () => {
      try {
        for (const file of [...picker.files]) {
          if (file.size > 25 * 1024 * 1024) throw new Error(`${file.name} exceeds 25 MB.`);
          const base64 = await new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(String(reader.result).split(",")[1]);
            reader.onerror = () => reject(new Error(`Could not read ${file.name}.`));
            reader.readAsDataURL(file);
          });
          await bridge("attachment_upload", { workspace_long_id: workspaceId, transaction_long_id: transactionId, original_name: file.name, mime_type: file.type, content_base64: base64 });
        }
        await reload(); notify("Attachment saved privately."); render();
      } catch (error) { notify(error.message, false); }
    });
    picker.click();
  };
  const downloadAttachment = async longId => {
    try {
      const result = await bridge("attachment_get", { long_id: longId });
      const bytes = Uint8Array.from(atob(result.content_base64), char => char.charCodeAt(0));
      download(result.original_name, bytes, result.mime_type);
    } catch (error) { notify(error.message, false); }
  };

  document.addEventListener("click", async event => {
    if (event.target.closest("[data-add-split]")) { fieldsMount.querySelector("[data-split-lines]")?.insertAdjacentHTML("beforeend", splitLineHtml()); syncPortableSplits(); return; }
    const removeSplit = event.target.closest("[data-remove-split]");
    if (removeSplit) { removeSplit.closest(".split-line")?.remove(); syncPortableSplits(); return; }
    const acceptSuggestion = event.target.closest("[data-accept-suggestion]");
    if (acceptSuggestion) { openEditor("recurring_rules", null, JSON.parse(decodeURIComponent(acceptSuggestion.dataset.acceptSuggestion))); return; }
    const ignoreSuggestion = event.target.closest("[data-ignore-suggestion]");
    if (ignoreSuggestion) {
      const storageKey = `financeIgnoredRecurring:${workspaceId}`, ignored = new Set(JSON.parse(localStorage.getItem(storageKey) || "[]"));
      ignored.add(decodeURIComponent(ignoreSuggestion.dataset.ignoreSuggestion)); localStorage.setItem(storageKey, JSON.stringify([...ignored])); notify("Suggestion ignored. You can clear ignored suggestions from workspace Settings."); render(); return;
    }
    const restoreTaxonomy = event.target.closest("[data-restore-taxonomy]");
    if (restoreTaxonomy) {
      const row = find(restoreTaxonomy.dataset.restoreTaxonomy, restoreTaxonomy.dataset.id);
      try { await save(restoreTaxonomy.dataset.restoreTaxonomy, { ...data(row), archived: false }, { long_id: row.long_id }); notify("Restored locally."); render(); } catch (error) { notify(error.message, false); }
      return;
    }
    if (event.target.closest("[data-restore-suggestions]")) { localStorage.removeItem(`financeIgnoredRecurring:${workspaceId}`); notify("Recurring suggestions restored."); render(); return; }
    if (event.target.closest("[data-restore-json]")) {
      const picker = document.createElement("input"); picker.type = "file"; picker.accept = ".json,application/json";
      picker.addEventListener("change", async () => { try { if (picker.files[0]) await restoreJsonBackup(picker.files[0]); } catch (error) { notify(error.message, false); } }); picker.click(); return;
    }
    const top = event.target.closest("[data-top]");
    if (top) { topTab = top.dataset.top; workspaceId = ""; render(); return; }
    const open = event.target.closest("[data-open-workspace]");
    if (open) { workspaceId = open.dataset.openWorkspace; panel = "overview"; topTab = "workspaces"; render(); return; }
    if (event.target.closest("[data-back-workspaces]")) { workspaceId = ""; topTab = "workspaces"; render(); return; }
    const panelButton = event.target.closest("[data-panel]");
    if (panelButton) { panel = panelButton.dataset.panel; render(); return; }
    const newButton = event.target.closest("[data-new]");
    if (newButton) { openEditor(newButton.dataset.new); return; }
    const editButton = event.target.closest("[data-edit]");
    if (editButton) { openEditor(editButton.dataset.edit, find(editButton.dataset.edit, editButton.dataset.id)); return; }
    const deleteButton = event.target.closest("[data-delete]");
    if (deleteButton) {
      if (!confirm(`Delete this ${deleteButton.dataset.delete.replaceAll("_", " ")} record? Its data remains recoverable in local storage.`)) return;
      try {
        const row = find(deleteButton.dataset.delete, deleteButton.dataset.id);
        if (deleteButton.dataset.delete === "transactions" && data(row).transfer_group) {
          const pair = byType("transactions").filter(item => data(item).transfer_group === data(row).transfer_group);
          for (const item of pair) await remove("transactions", item.long_id);
        } else await remove(deleteButton.dataset.delete, deleteButton.dataset.id);
        if (deleteButton.dataset.delete === "workspaces") workspaceId = "";
        notify(["categories", "tags"].includes(deleteButton.dataset.delete) ? "Archived locally. Referenced records were preserved." : "Deleted locally."); render();
      } catch (error) { notify(error.message, false); }
      return;
    }
    if (event.target.closest("[data-sample]")) { await sampleWorkspace(); return; }
    if (event.target.closest("[data-generate-recurring]")) { await generateRecurring(); return; }
    if (event.target.closest("[data-reconcile]")) { await reconcile(); return; }
    if (event.target.closest("[data-import]")) {
      importForm.reset(); importRows = []; document.getElementById("importPreview").hidden = true; document.getElementById("csvMapping").hidden = true;
      document.getElementById("previewImport").textContent = "Preview import"; delete document.getElementById("previewImport").dataset.confirm;
      importForm.elements.account.innerHTML = optionHtml(accountOptions());
      importDialog.showModal(); return;
    }
    if (event.target.closest("[data-export-csv]")) { exportCsv(); return; }
    if (event.target.closest("[data-export-json]")) { try { await exportJson(); } catch (error) { notify(error.message, false); } return; }
    if (event.target.closest("[data-print]")) { print(); return; }
    const attachButton = event.target.closest("[data-attach]");
    if (attachButton) { await attach(attachButton.dataset.attach); return; }
    const attachmentButton = event.target.closest("[data-download-attachment]");
    if (attachmentButton) await downloadAttachment(attachmentButton.dataset.downloadAttachment);
  });

  document.addEventListener("input", event => {
    if (event.target.matches("[data-split-amount], #recordForm [name='value']")) syncPortableSplits();
    const mount = document.getElementById("workspacePanel");
    if (!mount || panel !== "transactions") return;
    if (event.target.matches("[data-filter-search]")) mount.dataset.search = event.target.value;
    else return;
    renderTransactions(mount);
  });
  document.addEventListener("change", async event => {
    if (event.target.id === "mobileTopNav") { topTab = event.target.value; workspaceId = ""; render(); }
    if (event.target.matches("[data-workspace-mobile]")) { panel = event.target.value; render(); }
    if (event.target.id === "displayCurrency") {
      try {
        await bridge("settings", { display_currency: event.target.value, currencies: state.currencies });
        await reload(); notify("Display currency saved."); render();
      } catch (error) { notify(error.message, false); }
    }
    if (event.target.matches("[data-period-preset], [data-period-start], [data-period-end]")) {
      const current = periodPreference(), container = event.target.closest(".period-control"), preset = container.querySelector("[data-period-preset]").value;
      const next = { ...current, preset, start: container.querySelector("[data-period-start]")?.value || "", end: container.querySelector("[data-period-end]")?.value || "" };
      container.querySelectorAll(".custom-period").forEach(item => { item.hidden = preset !== "custom"; });
      if (preset !== "custom" || (next.start && next.end && next.start <= next.end)) { localStorage.setItem(`financePeriod:${workspaceId}`, JSON.stringify(next)); render(); }
      return;
    }
    if (event.target.matches("#recordForm [name='transaction_type']")) {
      const transfer = event.target.value === "transfer", destination = form.elements.destination_account?.closest("label"), category = form.elements.category?.closest("label"), split = fieldsMount.querySelector("[data-split-editor]");
      if (destination) destination.hidden = !transfer;
      if (category) category.hidden = transfer || Boolean(split?.querySelector(".split-line"));
      if (split) split.hidden = transfer;
    }
    if (event.target.matches("[data-filter-status], [data-filter-tag], [data-filter-sort]")) {
      const mount = document.getElementById("workspacePanel");
      if (event.target.matches("[data-filter-status]")) mount.dataset.status = event.target.value;
      else if (event.target.matches("[data-filter-tag]")) mount.dataset.tag = event.target.value;
      else mount.dataset.sort = event.target.value;
      renderTransactions(mount);
    }
    if (event.target === importForm.elements.file) {
      const file = event.target.files[0], mapping = document.getElementById("csvMapping");
      mapping.hidden = !file || !file.name.toLowerCase().endsWith(".csv");
      if (!mapping.hidden) {
        const headers = splitCsv((await file.slice(0, 8192).text()).split(/\r?\n/)[0]);
        ["dateColumn", "amountColumn", "typeColumn", "payeeColumn", "noteColumn"].forEach(name => {
          const select = importForm.elements[name], blank = ["typeColumn", "payeeColumn", "noteColumn"].includes(name) ? '<option value="">None / infer</option>' : "";
          select.innerHTML = blank + optionHtml(headers.map(header => ({ value: header, label: header })), headers.find(header => header.toLowerCase().includes(name.replace("Column", "").toLowerCase())) || "");
        });
      }
    }
  });

  document.getElementById("toggleBalances").addEventListener("click", () => {
    balancesHidden = !balancesHidden;
    localStorage.setItem("financeBalancesHidden", balancesHidden ? "1" : "0");
    render();
  });
  form.addEventListener("submit", formRecordSave);
  importForm.addEventListener("submit", async event => {
    event.preventDefault();
    try {
      if (document.getElementById("previewImport").dataset.confirm) await applyImport();
      else await previewImport();
    } catch (error) { notify(error.message, false); }
  });

  (async () => {
    try {
      await reload();
      render();
    } catch (error) {
      view.innerHTML = `<p class="empty">${esc(error.message)}</p>`;
    }
  })();
})();

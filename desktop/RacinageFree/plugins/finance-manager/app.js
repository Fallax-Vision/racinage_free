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

  const esc = value => String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
  const id = prefix => `${prefix}_${crypto.getRandomValues(new Uint32Array(4)).join("")}`;
  const today = () => new Date().toISOString().slice(0, 10);
  const dateLabel = value => {
    const parts = String(value || "").split("-");
    return parts.length === 3 ? `${parts[2]}/${parts[1]}/${parts[0]}` : "";
  };
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
  const accountOptions = () => byType("accounts").filter(row => !data(row).external).map(row => ({ value: row.long_id, label: `${data(row).name} (${data(row).native_currency_code})` }));
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
        field("tags", "Tags", "text", { maxlength: 500 }),
        field("splits", "Split categories", "text", { wide: true, placeholder: "Food:25.00, Transport:10.00" }),
        field("note", "Note", "textarea", { wide: true, maxlength: 2000 })
      ]
    },
    recurring_rules: {
      title: "Recurring rule",
      fields: [
        field("name", "Name", "text", { required: true }),
        field("transaction_type", "Type", "select", { options: ["income", "expense"] }),
        field("account", "Account", "dynamic", { options: accountOptions }),
        field("value", "Amount", "money", { required: true, min: .01 }),
        field("frequency", "Frequency", "select", { options: ["weekly", "monthly", "quarterly", "yearly"] }),
        field("next_date", "Next date", "date", { required: true, value: today() }),
        field("payee", "Payee", "text"),
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
        field("category", "Category", "text", { placeholder: "All categories" }),
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
    activeEditor = { recordType, row };
    dialog.showModal();
  };

  const editorValues = () => Object.fromEntries(new FormData(form).entries());
  const parseSplits = (text, totalCents) => {
    if (!String(text || "").trim()) return [];
    const splits = String(text).split(",").map(part => {
      const index = part.lastIndexOf(":");
      return { category: part.slice(0, index).trim(), amount_cents: cents(part.slice(index + 1)) };
    });
    if (splits.some(split => !split.category || split.amount_cents <= 0) || splits.reduce((sum, split) => sum + split.amount_cents, 0) !== totalCents) throw new Error("Split amounts must be positive and equal the transaction amount.");
    return splits;
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
      return { ...values, amount_cents: snap.native_cents, amount_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, native_currency_code: data(account).native_currency_code, splits: parseSplits(values.splits, snap.native_cents) };
    }
    if (recordType === "recurring_rules") {
      const account = find("accounts", values.account);
      if (!account) throw new Error("Choose an account.");
      const snap = snapshot(cents(values.value), data(account).native_currency_code);
      return { ...values, amount_cents: snap.native_cents, amount_usd_cents: snap.usd_cents, fx_rate: snap.fx_rate, native_currency_code: data(account).native_currency_code };
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
    const panels = ["overview", "accounts", "transactions", "budgets", "goals", "debts", "investments", "forecast", "reports", "settings"];
    return `<label class="workspace-mobile-nav">Workspace section<select data-workspace-mobile>${optionHtml(panels, panel)}</select></label><nav class="workspace-tabs" aria-label="Finance workspace">${panels.map(item => `<button type="button" data-panel="${item}" class="${item === panel ? "active" : ""}">${esc(item.replace(/\b\w/g, char => char.toUpperCase()))}</button>`).join("")}</nav>`;
  };

  const renderHome = () => {
    const workspaces = byType("workspaces", "");
    const included = workspaces.filter(row => data(row).include_home !== false);
    const total = included.reduce((sum, row) => sum + workspaceNetUsd(row.long_id), 0);
    const recent = state.records.filter(row => row.record_type === "transactions" && row.status === "active" && included.some(space => space.long_id === row.workspace_long_id))
      .sort((a, b) => String(data(b).transaction_date).localeCompare(String(data(a).transaction_date))).slice(0, 8);
    view.innerHTML = `<section class="metrics"><article class="metric"><span>Net worth</span><strong>${esc(money(total))}</strong></article><article class="metric"><span>Workspaces</span><strong>${workspaces.length}</strong></article><article class="metric"><span>Accounts</span><strong>${workspaces.reduce((sum, row) => sum + byType("accounts", row.long_id).length, 0)}</strong></article></section>
      <section class="panel"><header class="panel-head"><div><p class="kicker">Local workspaces</p><h2>Your finances</h2></div>${workspaces.length ? "" : '<button class="primary" type="button" data-new="workspaces">Create workspace</button>'}</header>
      ${workspaces.length ? `<div class="workspace-grid">${workspaces.map(row => `<article class="workspace-card"><header><span class="workspace-mark">${esc(data(row).name?.[0]?.toUpperCase() || "F")}</span><span class="badge">${data(row).sample ? "Sample" : "Private"}</span></header><h3>${esc(data(row).name)}</h3><strong>${esc(money(workspaceNetUsd(row.long_id)))}</strong><button class="link-button" type="button" data-open-workspace="${esc(row.long_id)}">Open →</button></article>`).join("")}</div>` : `<div class="empty"><h3>Create your first private workspace</h3><p>Start blank or add a removable sample.</p><div class="panel-actions"><button class="primary" type="button" data-new="workspaces">Create workspace</button><button class="ghost" type="button" data-sample>Add sample workspace</button></div></div>`}</section>
      <section class="panel"><header class="panel-head"><h2>Recent activity</h2></header>${transactionTable(recent, false)}</section>`;
  };

  const renderWorkspaces = () => {
    const workspaces = byType("workspaces", "");
    view.innerHTML = `<section class="panel"><header class="panel-head"><div><p class="kicker">Finance Manager</p><h2>Workspaces</h2></div><button class="primary" type="button" data-new="workspaces">New workspace</button></header>${workspaces.length ? `<div class="workspace-grid">${workspaces.map(row => `<article class="workspace-card"><header><span class="workspace-mark">${esc(data(row).name?.[0]?.toUpperCase() || "F")}</span><span class="badge">${data(row).sample ? "Sample" : "Private"}</span></header><h3>${esc(data(row).name)}</h3><strong>${esc(money(workspaceNetUsd(row.long_id)))}</strong><div class="row-actions"><button class="link-button" type="button" data-open-workspace="${esc(row.long_id)}">Open</button><button class="link-button" type="button" data-edit="workspaces" data-id="${esc(row.long_id)}">Edit</button>${data(row).sample ? `<button class="danger" type="button" data-delete="workspaces" data-id="${esc(row.long_id)}">Remove sample</button>` : ""}</div></article>`).join("")}</div>` : '<p class="empty">No workspaces yet.</p>'}</section>`;
  };

  const renderHelp = () => {
    view.innerHTML = `<section class="help-grid"><article class="help-card"><h3>Record-only money actions</h3><p>Add funds, withdrawals, and transfers update local records. Finance Manager never moves real money.</p></article><article class="help-card"><h3>Offline and private</h3><p>Records and attachments remain under your Windows profile and work without internet access.</p></article><article class="help-card"><h3>Lite allowances</h3><p>One workspace, 8 accounts, 2,500 transactions, 5 each budgets, goals, and debts, 25 holdings, one scenario, and generous circle limits.</p></article><article class="help-card"><h3>Backups</h3><p>Use Reports to download CSV or a complete JSON backup. Print the report using the A4 layout.</p></article></section>`;
  };

  const renderWorkspace = () => {
    const workspace = find("workspaces", workspaceId);
    if (!workspace) { workspaceId = ""; topTab = "workspaces"; render(); return; }
    view.innerHTML = `<header class="workspace-head"><div><button class="link-button" type="button" data-back-workspaces>← Back to workspaces</button><h2>${esc(data(workspace).name)}</h2><p>${esc(data(workspace).native_currency_code || "USD")} workspace</p></div></header>${workspaceTabs()}<section class="panel" id="workspacePanel"></section>`;
    const mount = document.getElementById("workspacePanel");
    ({
      overview: renderOverview,
      accounts: renderAccounts,
      transactions: renderTransactions,
      budgets: () => renderGeneric("budgets"),
      goals: () => renderGeneric("goals"),
      debts: renderDebts,
      investments: renderInvestments,
      forecast: renderForecast,
      reports: renderReports,
      settings: renderSettings
    }[panel] || renderOverview)(mount, workspace);
  };

  const renderOverview = mount => {
    const accounts = byType("accounts"), transactions = byType("transactions");
    const month = today().slice(0, 7);
    const cashflow = transactions.filter(row => String(data(row).transaction_date).startsWith(month) && ["income", "expense"].includes(data(row).transaction_type))
      .reduce((sum, row) => sum + (data(row).transaction_type === "income" ? 1 : -1) * Number(data(row).amount_usd_cents || 0), 0);
    const recent = [...transactions].sort((a, b) => String(data(b).transaction_date).localeCompare(String(data(a).transaction_date))).slice(0, 8);
    mount.innerHTML = `<header class="panel-head"><div><h2>Overview</h2><p>Central view of your personal cash finances.</p></div></header><section class="metrics"><article class="metric"><span>Net worth</span><strong>${esc(money(workspaceNetUsd(workspaceId)))}</strong></article><article class="metric"><span>This month cash flow</span><strong>${esc(money(cashflow))}</strong></article><article class="metric"><span>Accounts</span><strong>${accounts.length}</strong></article><article class="metric"><span>Transactions</span><strong>${transactions.length}</strong></article></section><h3>Recent activity</h3>${transactionTable(recent, false)}`;
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
    const sort = mount.dataset.sort || "date_desc";
    let rows = byType("transactions").filter(row => !status || data(row).state === status).filter(row => !search || `${data(row).payee} ${data(row).note}`.toLowerCase().includes(search.toLowerCase()));
    rows.sort((a, b) => sort === "date_asc" ? String(data(a).transaction_date).localeCompare(String(data(b).transaction_date)) : sort === "amount_desc" ? Number(data(b).amount_usd_cents) - Number(data(a).amount_usd_cents) : sort === "amount_asc" ? Number(data(a).amount_usd_cents) - Number(data(b).amount_usd_cents) : sort === "payee" ? String(data(a).payee).localeCompare(String(data(b).payee)) : String(data(b).transaction_date).localeCompare(String(data(a).transaction_date)));
    mount.innerHTML = `<header class="panel-head"><div><h2>Transactions</h2><p>Income, expenses, withdrawals, deposits, and balanced transfers.</p></div><div class="panel-actions"><button class="ghost" type="button" data-import>Import</button><button class="ghost" type="button" data-new="recurring_rules">Recurring</button><button class="ghost" type="button" data-generate-recurring>Review due</button><button class="ghost" type="button" data-reconcile>Reconcile</button><button class="primary" type="button" data-new="transactions">Add transaction</button></div></header><div class="filters"><input type="search" data-filter-search placeholder="Search" value="${esc(search)}"><select data-filter-status>${optionHtml([{ value: "", label: "All statuses" }, "pending", "cleared", "reconciled"], status)}</select><select data-filter-sort>${optionHtml([{ value: "date_desc", label: "Newest first" }, { value: "date_asc", label: "Oldest first" }, { value: "amount_desc", label: "Amount high to low" }, { value: "amount_asc", label: "Amount low to high" }, { value: "payee", label: "Payee A-Z" }], sort)}</select></div>${transactionTable(rows)}`;
    mount.dataset.search = search; mount.dataset.status = status; mount.dataset.sort = sort;
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
      return `<article class="chart-card"><h3>${esc(data(row).name)}</h3><p>Planned ${esc(money(planned))}<br>Actual ${esc(money(actual))}<br>Remaining ${esc(money(remaining))}</p><div class="bar"><span style="width:${percent}%"></span></div></article>`;
    }).join("")}</section>`);
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
    const tx = byType("transactions"), accounts = byType("accounts"), budgets = byType("budgets"), goals = byType("goals"), debts = byType("debts"), investments = byType("investments"), circles = byType("circle_entries");
    const months = {};
    tx.filter(row => ["income", "expense"].includes(data(row).transaction_type)).forEach(row => {
      const key = String(data(row).transaction_date).slice(0, 7);
      months[key] ||= { income: 0, expense: 0 };
      months[key][data(row).transaction_type] += Number(data(row).amount_usd_cents || 0);
    });
    mount.innerHTML = `<header class="panel-head"><div><h2>Reports</h2><p>Cash flow, net worth, budgets, goals, debts, investments, currency exposure, circles, and comparisons.</p></div><div class="panel-actions"><button class="ghost" type="button" data-export-csv>Export CSV</button><button class="ghost" type="button" data-export-json>JSON backup</button><button class="primary" type="button" data-print>Print A4 report</button></div></header><section class="metrics"><article class="metric"><span>Net worth</span><strong>${esc(money(workspaceNetUsd(workspaceId)))}</strong></article><article class="metric"><span>Goals</span><strong>${goals.length}</strong></article><article class="metric"><span>Debts</span><strong>${debts.length}</strong></article><article class="metric"><span>Investment gain</span><strong>${esc(money(investments.reduce((sum, row) => sum + Number(data(row).current_value_usd_cents || 0) - Number(data(row).cost_basis_usd_cents || 0), 0)))}</strong></article></section>
      <h3>Cash-flow comparison</h3>${table(["Period", "Income", "Expenses", "Net"], Object.entries(months).sort(([a], [b]) => b.localeCompare(a)).map(([period, item]) => `<tr><td>${esc(period)}</td><td>${esc(money(item.income))}</td><td>${esc(money(item.expense))}</td><td>${esc(money(item.income - item.expense))}</td></tr>`))}
      <section class="report-grid"><article class="chart-card"><h3>Budget variance</h3>${budgets.map(row => `<p>${esc(data(row).name)}: ${esc(money(Number(data(row).planned_usd_cents) - budgetActual(row)))}</p>`).join("") || "<p>No budgets</p>"}</article><article class="chart-card"><h3>Currency exposure</h3>${Object.entries(accounts.reduce((out, row) => { out[data(row).native_currency_code] = (out[data(row).native_currency_code] || 0) + accountBalanceUsd(row); return out; }, {})).map(([code, value]) => `<p>${esc(code)}: ${esc(money(value))}</p>`).join("") || "<p>No accounts</p>"}</article><article class="chart-card"><h3>Circle ledger</h3><p>${circles.length} entries recorded separately from net worth.</p></article></section>`;
  };

  const circlePosition = member => byType("circle_entries").filter(row => data(row).member === member.long_id).reduce((sum, row) => sum + (["contribution", "repayment"].includes(data(row).entry_type) ? 1 : -1) * Number(data(row).amount_usd_cents || 0), 0);
  const renderSettings = (mount, workspace) => {
    const circles = byType("circles"), members = byType("circle_members"), entries = byType("circle_entries");
    mount.innerHTML = `<header class="panel-head"><div><h2>Settings</h2><p>Workspace visibility, local currency rates, circles, and private data.</p></div><button class="ghost" type="button" data-edit="workspaces" data-id="${esc(workspace.long_id)}">Edit workspace</button></header>
      <section class="help-grid"><article class="help-card"><h3>Home totals</h3><p>${data(workspace).include_home === false ? "Excluded" : "Included"} in Home totals. Balances are ${data(workspace).hide_balances ? "hidden" : "visible"} by default.</p></article><article class="help-card"><h3>Local storage</h3><p>Hiding Finance Manager keeps every record and attachment on this device.</p></article></section>
      <header class="panel-head"><div><h2>Circles</h2><p>Separate contribution and loan pool ledgers. They never affect accounts or net worth.</p></div><div class="panel-actions"><button class="ghost" type="button" data-new="circles">Add circle</button>${circles.length ? '<button class="ghost" type="button" data-new="circle_members">Add person</button>' : ""}${members.length ? '<button class="primary" type="button" data-new="circle_entries">Record entry</button>' : ""}</div></header>
      ${table(["Circle", "Person", "Position"], members.map(row => `<tr><td>${esc(data(find("circles", data(row).circle)).name || "")}</td><td>${esc(data(row).name)}</td><td>${esc(money(circlePosition(row)))}</td></tr>`))}
      <h3>Circle ledger entries</h3>${table(["Date", "Circle", "Person", "Type", "Amount", ""], entries.map(row => `<tr><td>${esc(dateLabel(data(row).entry_date))}</td><td>${esc(data(find("circles", data(row).circle)).name || "")}</td><td>${esc(data(find("circle_members", data(row).member)).name || "")}</td><td>${esc(data(row).entry_type)}</td><td>${esc(money(data(row).amount_usd_cents))}</td><td>${rowActions("circle_entries", row)}</td></tr>`))}`;
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
        const recordData = { ...data(row), ...values, include_home: values.include_home === "1", hide_balances: values.hide_balances === "1", sample: values.sample === "1" };
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
      const workspace = { record_type: "workspaces", workspace_long_id: "", data: { name: "Sample household", native_currency_code: "USD", include_home: true, hide_balances: false, sample: true } };
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
      return { record_type: "transactions", workspace_long_id: workspaceId, data: { transaction_type: item.transaction_type, account: item.account, amount_cents: item.amount_cents, amount_usd_cents: item.amount_usd_cents, fx_rate: item.fx_rate, native_currency_code: item.native_currency_code, transaction_date: item.next_date, payee: item.payee || item.name, note: item.note, state: "pending", recurring_rule: rule.long_id, fingerprint: `recurring|${rule.long_id}|${item.next_date}`, splits: [] } };
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
  const exportJson = () => download(`finance-backup-${today()}.json`, JSON.stringify({ schema: 1, plugin: "finance-manager", exported_at: new Date().toISOString(), workspace: find("workspaces", workspaceId), records: state.records.filter(row => row.workspace_long_id === workspaceId), attachments: state.attachments.filter(item => item.workspace_long_id === workspaceId) }, null, 2), "application/json");

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
        notify("Deleted locally."); render();
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
    if (event.target.closest("[data-export-json]")) { exportJson(); return; }
    if (event.target.closest("[data-print]")) { print(); return; }
    const attachButton = event.target.closest("[data-attach]");
    if (attachButton) { await attach(attachButton.dataset.attach); return; }
    const attachmentButton = event.target.closest("[data-download-attachment]");
    if (attachmentButton) await downloadAttachment(attachmentButton.dataset.downloadAttachment);
  });

  document.addEventListener("input", event => {
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
    if (event.target.matches("[data-filter-status], [data-filter-sort]")) {
      const mount = document.getElementById("workspacePanel");
      if (event.target.matches("[data-filter-status]")) mount.dataset.status = event.target.value;
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

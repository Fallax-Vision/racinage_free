using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace RacinageFreeDesktop {
  internal sealed partial class LocalServer {
    private void LocalFloatyApi(HttpListenerContext context) {
      if (!IsAuthenticated(context) || context.Request.HttpMethod != "POST"
          || !store.CheckCsrf(context.Request.Headers["X-Racinage-CSRF"])) {
        WriteJson(context, "{\"ok\":false,\"message\":\"The local Floaty session is unavailable.\"}", 403);
        return;
      }
      if (context.Request.ContentLength64 < 1 || context.Request.ContentLength64 > 256 * 1024) {
        WriteJson(context, "{\"ok\":false,\"message\":\"The Floaty request is too large.\"}", 413);
        return;
      }
      try {
        string body;
        using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
        Dictionary<string, object> request = json.Deserialize<Dictionary<string, object> >(body) ?? new Dictionary<string, object>();
        string action = request.ContainsKey("action") ? Convert.ToString(request["action"], CultureInfo.InvariantCulture) : "";
        Dictionary<string, object> payload = request.ContainsKey("payload") ? request["payload"] as Dictionary<string, object> : null;
        Dictionary<string, object> data = store.LocalFloatyAction(action, payload ?? new Dictionary<string, object>());
        WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", true }, { "data", data } }));
      } catch (Exception error) {
        Program.Log("Local Floaty action failed without record payload logging: " + error.GetType().Name);
        WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", false }, { "message", error.Message } }), 400);
      }
    }
  }

  internal sealed partial class LocalStore {
    internal Dictionary<string, object> LocalFloatyAction(string action, Dictionary<string, object> payload) {
      if (action == "state") return LocalFloatyState();
      if (action == "open") return LocalFloatyOpen(payload);
      if (action == "save") return LocalFloatySave(payload);
      if (action == "update") return LocalFloatyUpdate(payload);
      if (action == "close") return LocalFloatyClose(payload);
      throw new InvalidOperationException("Unknown local Floaty action.");
    }

    private Dictionary<string, object> LocalFloatyState() {
      List<Dictionary<string, object> > windows = new List<Dictionary<string, object> >();
      using (SqliteDb db = Open()) {
        foreach (Dictionary<string, string> row in db.Query("SELECT long_id,title,scope,route_key,x_normalized,y_normalized,width_px,height_px,minimized,stack_order,revision FROM local_floaty_windows WHERE status='active' ORDER BY stack_order,long_id")) {
          Dictionary<string, object> window = new Dictionary<string, object> {
            { "id", row["long_id"] }, { "title", row["title"] }, { "scope", row["scope"] }, { "route", row["route_key"] },
            { "xNormalized", LocalFloatyDouble(row["x_normalized"]) }, { "yNormalized", LocalFloatyDouble(row["y_normalized"]) },
            { "width", LocalFloatyInt(row["width_px"]) }, { "height", LocalFloatyInt(row["height_px"]) },
            { "minimized", row["minimized"] == "1" }, { "order", LocalFloatyInt(row["stack_order"]) }, { "revision", LocalFloatyInt(row["revision"]) }
          };
          List<Dictionary<string, object> > items = new List<Dictionary<string, object> >();
          foreach (Dictionary<string, string> item in db.Query("SELECT provider,record_type,record_long_id,view_json,revision FROM local_floaty_items WHERE window_long_id=? AND status='active' ORDER BY updated_at,record_long_id", row["long_id"])) {
            Dictionary<string, object> view = json.DeserializeObject(item["view_json"]) as Dictionary<string, object> ?? new Dictionary<string, object>();
            items.Add(new Dictionary<string, object> { { "provider", item["provider"] }, { "recordType", item["record_type"] }, { "recordId", item["record_long_id"] }, { "view", view }, { "revision", LocalFloatyInt(item["revision"]) } });
          }
          window["items"] = items;
          windows.Add(window);
        }
      }
      return new Dictionary<string, object> { { "windows", windows } };
    }

    private Dictionary<string, object> LocalFloatyOpen(Dictionary<string, object> payload) {
      string provider = LocalFloatyText(payload, "provider", 80), type = LocalFloatyText(payload, "recordType", 80), record = SafeLongId(LocalFloatyText(payload, "recordId", 80));
      if (provider != "finance-manager" || type != "quick_expense" || record == "") throw new InvalidDataException("This Floaty provider is unavailable.");
      string target = SafeLongId(LocalFloatyText(payload, "windowId", 80)), scope = LocalFloatyText(payload, "scope", 20), route = LocalFloatyText(payload, "route", 200);
      if (scope != "current_page") { scope = "all_pages"; route = ""; }
      string now = Now();
      using (SqliteDb db = Open()) {
        db.Exec("BEGIN IMMEDIATE");
        try {
          Dictionary<string, object> view = LocalFloatyQuickExpenseView(db, record);
          if (target == "" || db.QueryOne("SELECT long_id FROM local_floaty_windows WHERE long_id=? AND status='active' LIMIT 1", target) == null) {
            target = "local_floaty_" + Guid.NewGuid().ToString("N");
            int order = LocalFloatyInt(db.Scalar("SELECT COALESCE(MAX(stack_order),0)+1 FROM local_floaty_windows WHERE status='active'"));
            db.Execute("INSERT INTO local_floaty_windows(long_id,title,scope,route_key,x_normalized,y_normalized,width_px,height_px,minimized,stack_order,revision,status,updated_at)VALUES(?,?,?,?,1,1,360,420,0,?,1,'active',?)", target, LocalFloatyText(view, "title", 120), scope, route, order, now);
          }
          db.Execute("INSERT INTO local_floaty_items(window_long_id,provider,record_type,record_long_id,view_json,revision,status,updated_at)VALUES(?,?,?,?,?,1,'active',?) ON CONFLICT(window_long_id,provider,record_type,record_long_id) DO UPDATE SET view_json=excluded.view_json,revision=local_floaty_items.revision+1,status='active',updated_at=excluded.updated_at", target, provider, type, record, json.Serialize(view), now);
          db.Execute("UPDATE local_floaty_windows SET minimized=0,revision=revision+1,updated_at=? WHERE long_id=?", now, target);
          db.Exec("COMMIT");
        } catch { db.Exec("ROLLBACK"); throw; }
      }
      ProtectDatabaseFile();
      return LocalFloatyState();
    }

    private Dictionary<string, object> LocalFloatySave(Dictionary<string, object> payload) {
      string id = SafeLongId(LocalFloatyText(payload, "windowId", 80)), scope = LocalFloatyText(payload, "scope", 20), route = LocalFloatyText(payload, "route", 200);
      if (id == "") throw new InvalidDataException("The Floaty window is unavailable.");
      if (scope != "current_page") { scope = "all_pages"; route = ""; }
      double x = LocalFloatyClamp(LocalFloatyNumber(payload, "xNormalized"), 0, 1), y = LocalFloatyClamp(LocalFloatyNumber(payload, "yNormalized"), 0, 1);
      int width = (int)LocalFloatyClamp(LocalFloatyNumber(payload, "width"), 260, 1800), height = (int)LocalFloatyClamp(LocalFloatyNumber(payload, "height"), 180, 1400), order = (int)LocalFloatyClamp(LocalFloatyNumber(payload, "order"), 0, 1000000);
      bool minimized = payload.ContainsKey("minimized") && Convert.ToBoolean(payload["minimized"], CultureInfo.InvariantCulture);
      using (SqliteDb db = Open()) if (db.Execute("UPDATE local_floaty_windows SET scope=?,route_key=?,x_normalized=?,y_normalized=?,width_px=?,height_px=?,minimized=?,stack_order=?,revision=revision+1,updated_at=? WHERE long_id=? AND status='active'", scope, route, x, y, width, height, minimized ? 1 : 0, order, Now(), id) != 1) throw new InvalidOperationException("The Floaty window is unavailable.");
      ProtectDatabaseFile();
      return LocalFloatyState();
    }

    private Dictionary<string, object> LocalFloatyUpdate(Dictionary<string, object> payload) {
      string provider = LocalFloatyText(payload, "provider", 80), type = LocalFloatyText(payload, "recordType", 80), record = SafeLongId(LocalFloatyText(payload, "recordId", 80));
      if (provider != "finance-manager" || type != "quick_expense" || record == "") throw new InvalidDataException("This Floaty provider is unavailable.");
      using (SqliteDb db = Open()) {
        Dictionary<string, object> view = LocalFloatyQuickExpenseView(db, record);
        db.Execute("UPDATE local_floaty_items SET view_json=?,revision=revision+1,updated_at=? WHERE provider=? AND record_type=? AND record_long_id=? AND status='active'", json.Serialize(view), Now(), provider, type, record);
      }
      ProtectDatabaseFile();
      return LocalFloatyState();
    }

    private Dictionary<string, object> LocalFloatyClose(Dictionary<string, object> payload) {
      string id = SafeLongId(LocalFloatyText(payload, "windowId", 80));
      if (id == "") throw new InvalidDataException("The Floaty window is unavailable.");
      using (SqliteDb db = Open()) {
        db.Exec("BEGIN IMMEDIATE");
        try {
          db.Execute("UPDATE local_floaty_items SET status='closed',revision=revision+1,updated_at=? WHERE window_long_id=? AND status='active'", Now(), id);
          db.Execute("UPDATE local_floaty_windows SET status='closed',revision=revision+1,updated_at=? WHERE long_id=? AND status='active'", Now(), id);
          db.Exec("COMMIT");
        } catch { db.Exec("ROLLBACK"); throw; }
      }
      ProtectDatabaseFile();
      return LocalFloatyState();
    }

    private Dictionary<string, object> LocalFloatyView(Dictionary<string, object> raw) {
      raw = raw ?? new Dictionary<string, object>();
      List<Dictionary<string, object> > entries = new List<Dictionary<string, object> >();
      object rawEntries;
      if (raw.TryGetValue("entries", out rawEntries) && rawEntries is object[]) {
        foreach (object rawEntry in ((object[])rawEntries).Take(20)) {
          Dictionary<string, object> entry = rawEntry as Dictionary<string, object>;
          if (entry != null) entries.Add(new Dictionary<string, object> { { "label", LocalFloatyText(entry, "label", 160) }, { "value", LocalFloatyText(entry, "value", 80) }, { "date", LocalFloatyText(entry, "date", 80) } });
        }
      }
      return new Dictionary<string, object> {
        { "title", LocalFloatyText(raw, "title", 120) }, { "status", LocalFloatyText(raw, "status", 40) },
        { "starting", LocalFloatyText(raw, "starting", 80) }, { "spent", LocalFloatyText(raw, "spent", 80) },
        { "remaining", LocalFloatyText(raw, "remaining", 80) }, { "progress", LocalFloatyClamp(LocalFloatyNumber(raw, "progress"), 0, 100) }, { "entries", entries }
      };
    }

    private Dictionary<string, object> LocalFloatyQuickExpenseView(SqliteDb db, string record) {
      Dictionary<string, string> row = db.QueryOne("SELECT workspace_long_id,data_json FROM local_plugin_records WHERE slug='finance-manager' AND record_type='quick_expenses' AND long_id=? AND status='active' LIMIT 1", record);
      if (row == null) throw new InvalidOperationException("The Quick Expense is unavailable.");
      Dictionary<string, object> quick = json.DeserializeObject(row["data_json"]) as Dictionary<string, object> ?? new Dictionary<string, object>();
      long starting = GetLong(quick, "starting_usd_cents"), spent = 0;
      List<Dictionary<string, object> > entries = new List<Dictionary<string, object> >();
      foreach (Dictionary<string, string> entryRow in db.Query("SELECT data_json FROM local_plugin_records WHERE slug='finance-manager' AND record_type='quick_expense_entries' AND workspace_long_id=? AND status='active' AND json_extract(data_json,'$.quick_expense')=? ORDER BY json_extract(data_json,'$.occurred_at') DESC,long_id DESC", row["workspace_long_id"], record)) {
        Dictionary<string, object> entry = json.DeserializeObject(entryRow["data_json"]) as Dictionary<string, object> ?? new Dictionary<string, object>();
        long amount = GetLong(entry, "amount_usd_cents"); spent += amount;
        if (entries.Count < 20) entries.Add(new Dictionary<string, object> {
          { "label", LocalFloatyText(entry, "label", 160) }, { "value", LocalFloatyMoney(amount) }, { "date", LocalFloatyText(entry, "occurred_at", 80) }
        });
      }
      return LocalFloatyView(new Dictionary<string, object> {
        { "title", LocalFloatyText(quick, "name", 120) }, { "status", LocalFloatyText(quick, "quick_status", 40) },
        { "starting", LocalFloatyMoney(starting) }, { "spent", LocalFloatyMoney(spent) }, { "remaining", LocalFloatyMoney(starting - spent) },
        { "progress", starting > 0 ? Math.Min(100, Math.Round((double)spent * 100 / starting)) : 0 }, { "entries", entries.ToArray() }
      });
    }

    private static string LocalFloatyMoney(long cents) { return "USD " + (cents / 100.0).ToString("N2", CultureInfo.InvariantCulture); }

    private static string LocalFloatyText(Dictionary<string, object> values, string key, int max) {
      object value; string result = values != null && values.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : "";
      return result.Length > max ? result.Substring(0, max) : result;
    }
    private static double LocalFloatyNumber(Dictionary<string, object> values, string key) { object value; double result; return values != null && values.TryGetValue(key, out value) && Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !Double.IsNaN(result) && !Double.IsInfinity(result) ? result : 0; }
    private static double LocalFloatyClamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
    private static int LocalFloatyInt(object value) { int result; return Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0; }
    private static double LocalFloatyDouble(object value) { double result; return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0; }
  }
}

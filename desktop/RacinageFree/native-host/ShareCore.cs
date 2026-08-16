using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace RacinageFreeDesktop {
  internal sealed class PortableLocalizedText { public string en; public string fr; }
  internal sealed class PortableShareAction {
    public string id;
    public string[] accepts;
    public PortableLocalizedText label;
    public PortableLocalizedText description;
    public string target_kind;
  }
  internal sealed class PortableShareActions { public int contract_version; public PortableShareAction[] actions; }

  internal static class LocalShareContract {
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 128 * 1024 };
    private static readonly string[] AcceptedKinds = { "url", "text" };
    private static readonly string[] TargetKinds = { "none", "plugin_workspace" };

    internal static string SerializeValidated(string slug, PortableShareActions contract) {
      PortableShareActions valid = Validate(slug, contract);
      return valid == null ? "" : Json.Serialize(valid);
    }

    internal static PortableShareActions ParseValidated(string slug, string encoded) {
      if (String.IsNullOrWhiteSpace(encoded) || encoded.Length > 128 * 1024) return null;
      try { return Validate(slug, Json.Deserialize<PortableShareActions>(encoded)); }
      catch { return null; }
    }

    private static PortableShareActions Validate(string slug, PortableShareActions contract) {
      if (!PluginCatalogClient.ValidSlug(slug) || contract == null || contract.contract_version != 1 || contract.actions == null || contract.actions.Length == 0 || contract.actions.Length > 12) return null;
      HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
      List<PortableShareAction> actions = new List<PortableShareAction>();
      foreach (PortableShareAction action in contract.actions) {
        if (action == null || !ValidId(action.id) || !ids.Add(action.id) || action.accepts == null || action.accepts.Length == 0 || action.accepts.Length > 2) return null;
        string[] accepts = action.accepts.Where(value => AcceptedKinds.Contains(value)).Distinct().ToArray();
        if (accepts.Length != action.accepts.Length || !TargetKinds.Contains(action.target_kind ?? "")) return null;
        if (!ValidText(action.label == null ? "" : action.label.en, 90) || !ValidText(action.label == null ? "" : action.label.fr, 90)) return null;
        if (!ValidOptionalText(action.description == null ? "" : action.description.en, 240) || !ValidOptionalText(action.description == null ? "" : action.description.fr, 240)) return null;
        actions.Add(new PortableShareAction {
          id = action.id,
          accepts = accepts,
          label = new PortableLocalizedText { en = Clean(action.label.en), fr = Clean(action.label.fr) },
          description = new PortableLocalizedText { en = Clean(action.description == null ? "" : action.description.en), fr = Clean(action.description == null ? "" : action.description.fr) },
          target_kind = action.target_kind
        });
      }
      return new PortableShareActions { contract_version = 1, actions = actions.ToArray() };
    }

    internal static string NewToken() { return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); }

    internal static string NormalizeHttpUrl(string value) {
      value = (value ?? "").Trim();
      Uri uri;
      if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.UserInfo != "" || value.Length > 2000) return "";
      UriBuilder clean = new UriBuilder(uri) { Fragment = "" };
      return clean.Uri.AbsoluteUri;
    }

    internal static string ExtractHttpUrl(string value) {
      string direct = NormalizeHttpUrl(value);
      if (direct != "") return direct;
      Match match = Regex.Match(value ?? "", @"https?://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
      if (!match.Success) return "";
      return NormalizeHttpUrl(match.Value.TrimEnd('.', ',', ';', ')', ']', '}'));
    }

    private static bool ValidId(string value) {
      if (String.IsNullOrWhiteSpace(value) || value.Length > 80) return false;
      return value.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');
    }
    private static bool ValidText(string value, int max) { value = Clean(value); return value.Length > 0 && value.Length <= max; }
    private static bool ValidOptionalText(string value, int max) { return Clean(value).Length <= max; }
    private static string Clean(string value) { return Regex.Replace((value ?? "").Replace("<", "").Replace(">", ""), @"\s+", " ").Trim(); }
  }

  internal sealed class LocalShareActionView {
    internal string PluginSlug;
    internal string PluginName;
    internal PortableShareAction Action;
    internal List<Dictionary<string, string> > Targets;
    internal Dictionary<string, string> Delivery;
  }

  internal sealed partial class LocalServer {
    private static string ShareCss() {
      return @"
.share-receive-head{display:flex;justify-content:space-between;gap:20px;margin-bottom:20px}.share-receive-head h1{margin:5px 0 8px;font-size:42px;line-height:1.08;color:var(--brand)}.share-receive-head p{margin:0;max-width:720px;color:var(--muted);line-height:1.55}.share-paste,.share-receipt-summary,.share-plugin-group,.share-empty,.share-recent{margin-top:14px;padding:18px;border:1px solid var(--line);border-radius:12px;background:#fff}.share-paste form{grid-template-columns:minmax(0,1fr) auto;align-items:end}.share-paste form>label{min-width:0}.share-paste .actions{margin:0}.share-inline-status{grid-column:1/-1;min-height:18px;margin:0;color:var(--muted);font-size:12px}.share-receipt-summary{display:flex;align-items:center;justify-content:space-between;gap:18px}.share-receipt-summary div{display:grid;gap:4px;min-width:0}.share-receipt-summary span,.share-receipt-summary time{color:var(--muted);font-size:12px}.share-receipt-summary strong{overflow-wrap:anywhere;color:var(--brand)}.share-plugin-group>header{display:flex;align-items:center;gap:11px;padding-bottom:14px;border-bottom:1px solid var(--line)}.share-plugin-group h2,.share-plugin-group h3,.share-plugin-group p{margin:0}.share-plugin-group h2,.share-plugin-group h3{color:var(--brand)}.share-plugin-group header p,.share-action-row p{margin-top:3px;color:var(--muted);font-size:13px}.share-action-list{display:grid}.share-action-row{display:grid;grid-template-columns:minmax(0,1fr) minmax(220px,300px);align-items:end;gap:18px;padding:16px 0}.share-action-row+.share-action-row{border-top:1px solid var(--line)}.share-action-row form{grid-template-columns:minmax(0,1fr) auto;align-items:end}.share-delivery{margin-top:8px!important;padding:7px 9px;border-left:3px solid var(--brand);background:var(--pale);font-weight:700}.share-delivery.is-failed{border-left-color:#b93333;background:#fff2f2}.share-core-group .actions{margin-top:16px}.share-core-group .actions form{display:block}.share-recent{display:flex;align-items:center;gap:7px;overflow:auto}.share-recent a{flex:0 0 auto;max-width:230px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;padding:8px 10px;border:1px solid var(--line);border-radius:8px;color:var(--muted)}.share-recent a.active{border-color:var(--brand);color:var(--brand);background:var(--pale)}.share-empty{text-align:center;padding:36px}.feedback{padding:10px 12px;border:1px solid #aad5c3;border-radius:8px;background:#f0faf5;color:#175c43}.button:disabled{cursor:not-allowed;opacity:.55}
@media(prefers-color-scheme:dark){.share-paste,.share-receipt-summary,.share-plugin-group,.share-empty,.share-recent{background:#11272b;border-color:#315056}.share-delivery{background:#173439}.share-delivery.is-failed{background:#3a2020}.feedback{background:#17382c;color:#bfe8d2;border-color:#39755c}}
@media(max-width:760px){.share-receive-head h1{font-size:34px}.share-paste form,.share-action-row,.share-action-row form{grid-template-columns:1fr}.share-paste .actions,.share-action-row .button{width:100%}.share-receipt-summary{align-items:flex-start;flex-direction:column}.share-plugin-group{padding:15px}.share-action-row{gap:12px}.share-core-group .actions{display:grid}.share-core-group .button{width:100%}}
";
    }

    private void Share(HttpListenerContext context) {
      store.ImportShareInbox();
      if (!IsAuthenticated(context)) { Redirect(context, "/login"); return; }
      string message = "", error = "";
      if (context.Request.HttpMethod == "POST") {
        if (context.Request.ContentLength64 < 1 || context.Request.ContentLength64 > 65536) { WriteHtml(context, Page("Share with Racinage", SharePage("", "", "The share request must be 64 KB or fewer.")), 413); return; }
        Dictionary<string, string> form = ReadForm(context);
        if (!CheckCsrf(form)) { WriteHtml(context, Page("Share with Racinage", SharePage("", "", "Your session expired. Please try again.")), 400); return; }
        string action = form.ContainsKey("action") ? form["action"] : "";
        try {
          if (action == "create_receipt") {
            string receipt = store.CreateShareReceipt(form.ContainsKey("shared_value") ? form["shared_value"] : "", "paste");
            Redirect(context, "/share?receipt=" + Uri.EscapeDataString(receipt)); return;
          }
          if (action == "dismiss") {
            store.DismissShareReceipt(form.ContainsKey("receipt_id") ? form["receipt_id"] : "");
            Redirect(context, "/share"); return;
          }
          if (action == "execute") {
            Dictionary<string, string> result = store.ExecuteShareAction(
              form.ContainsKey("receipt_id") ? form["receipt_id"] : "",
              form.ContainsKey("revision") ? form["revision"] : "",
              form.ContainsKey("plugin_slug") ? form["plugin_slug"] : "",
              form.ContainsKey("share_action_id") ? form["share_action_id"] : "",
              form.ContainsKey("target_id") ? form["target_id"] : "",
              form.ContainsKey("idempotency_key") ? form["idempotency_key"] : "");
            message = result.ContainsKey("message") ? result["message"] : "Shared item queued.";
            store.ProcessPendingKitchenImportsAsync();
          }
          if (action == "retry_import") {
            store.RetryKitchenSourceImport("kitchen-planner", new Dictionary<string, object> { { "import_id", form.ContainsKey("import_id") ? form["import_id"] : "" } });
            message = "Recipe extraction queued again.";
            store.ProcessPendingKitchenImportsAsync();
          }
        } catch (Exception failure) { error = failure.Message; }
      }
      string requested = context.Request.QueryString["receipt"] ?? "";
      WriteHtml(context, Page("Share with Racinage", SharePage(requested, message, error)));
    }

    private string SharePage(string requestedReceipt, string message, string error) {
      bool french = IsFrenchCulture();
      List<Dictionary<string, string> > receipts = store.GetShareReceipts();
      Dictionary<string, string> receipt = receipts.FirstOrDefault(row => row["receipt_id"] == requestedReceipt) ?? receipts.FirstOrDefault(row => row["status"] != "dismissed") ?? receipts.FirstOrDefault();
      StringBuilder body = new StringBuilder();
      body.Append("<section class='share-receive-head'><div><p class='kicker'>" + (french ? "Partage Windows local" : "Local Windows share") + "</p><h1>" + (french ? "Partager avec Racinage Free" : "Share with Racinage Free") + "</h1><p>" + (french ? "Choisissez une action d'extension vérifiée. Vos données restent sur cet appareil." : "Choose a verified plugin action. Your data stays on this device.") + "</p></div></section>");
      if (message != "") body.Append("<p class='feedback' role='status'>" + H(message) + "</p>");
      if (error != "") body.Append("<p class='error' role='alert'>" + H(error) + "</p>");
      body.Append("<section class='share-paste'><form method='post' action='/share'>" + CsrfInput() + "<input type='hidden' name='action' value='create_receipt'><label>" + (french ? "Lien ou texte partagé" : "Shared link or text") + "<textarea name='shared_value' rows='2' maxlength='32768' required placeholder='https://...'></textarea></label><div class='actions'><button class='button' type='submit'>" + (french ? "Ajouter" : "Add to chooser") + "</button><button class='button ghost' type='button' data-share-clipboard>" + (french ? "Coller du presse-papiers" : "Paste from clipboard") + "</button></div><p class='share-inline-status' data-share-status aria-live='polite'></p></form></section>");
      if (receipt == null) {
        body.Append("<section class='share-empty'><h2>" + (french ? "Aucun élément reçu" : "Nothing shared yet") + "</h2><p>" + (french ? "Partagez un lien depuis Windows ou collez-le ci-dessus." : "Share a link from Windows or paste it above.") + "</p></section>");
        return body + ShareJs();
      }
      string display = receipt["normalized_url"] != "" ? receipt["normalized_url"] : receipt["payload_text"];
      body.Append("<section class='share-receipt-summary'><div><span>" + (french ? "Élément reçu" : "Received item") + "</span><strong>" + H(Shorten(display, 120)) + "</strong></div><time datetime='" + A(receipt["received_at"]) + "'>" + H(FormatLocalDate(receipt["received_at"])) + "</time></section>");
      List<LocalShareActionView> actions = store.GetShareActions(receipt["receipt_id"]);
      if (actions.Count == 0) body.Append("<p class='notice'>" + (french ? "Aucune extension installée et activée ne déclare d'action compatible. Vous pouvez toujours ouvrir ou copier le lien." : "No installed and enabled plugin declares a compatible action. You can still open or copy the link.") + "</p>");
      foreach (IGrouping<string, LocalShareActionView> group in actions.GroupBy(item => item.PluginSlug)) {
        LocalShareActionView first = group.First();
        body.Append("<section class='share-plugin-group'><header><span class='plugin-mark'>" + H(first.PluginName.Substring(0, 1).ToUpperInvariant()) + "</span><div><h2>" + H(first.PluginName) + "</h2><p>" + (french ? "Actions de l'extension" : "Plugin actions") + "</p></div></header><div class='share-action-list'>");
        foreach (LocalShareActionView view in group) {
          string label = french && view.Action.label.fr != "" ? view.Action.label.fr : view.Action.label.en;
          string description = french && view.Action.description.fr != "" ? view.Action.description.fr : view.Action.description.en;
          body.Append("<article class='share-action-row'><div><h3>" + H(label) + "</h3><p>" + H(description) + "</p>");
          if (view.Delivery != null) {
            body.Append("<p class='share-delivery is-" + A(view.Delivery["status"]) + "' role='status'>" + H(view.Delivery["status"].Replace('_', ' ')) + (view.Delivery["error_message"] == "" ? "" : ": " + H(view.Delivery["error_message"])) + "</p>");
            if (view.PluginSlug == "kitchen-planner" && view.Delivery["status"] == "pending" && view.Delivery["result_id"] != "") body.Append("<form class='share-retry' method='post' action='/share'>" + CsrfInput() + "<input type='hidden' name='action' value='retry_import'><input type='hidden' name='import_id' value='" + A(view.Delivery["result_id"]) + "'><button class='button ghost' type='submit'>" + (french ? "Réessayer l'extraction" : "Retry extraction") + "</button></form>");
          }
          body.Append("</div><form method='post' action='/share'>" + CsrfInput() + "<input type='hidden' name='action' value='execute'><input type='hidden' name='receipt_id' value='" + A(receipt["receipt_id"]) + "'><input type='hidden' name='revision' value='" + A(receipt["revision"]) + "'><input type='hidden' name='plugin_slug' value='" + A(view.PluginSlug) + "'><input type='hidden' name='share_action_id' value='" + A(view.Action.id) + "'><input type='hidden' name='idempotency_key' value='" + A(LocalShareContract.NewToken()) + "'>");
          if (view.Action.target_kind == "plugin_workspace") {
            body.Append("<label>" + (french ? "Cuisine" : "Kitchen") + "<select name='target_id' required>");
            foreach (Dictionary<string, string> target in view.Targets) body.Append("<option value='" + A(target["long_id"]) + "'" + (target["selected"] == "1" ? " selected" : "") + ">" + H(target["name"]) + "</option>");
            body.Append("</select></label>");
          } else body.Append("<input type='hidden' name='target_id' value=''>");
          body.Append("<button class='button' type='submit'" + (view.Targets.Count == 0 && view.Action.target_kind == "plugin_workspace" ? " disabled" : "") + ">" + (french ? "Envoyer" : "Send") + "</button></form></article>");
          if (view.Targets.Count == 0 && view.Action.target_kind == "plugin_workspace") body.Append("<p class='notice share-target-empty'>" + (french ? "Créez d'abord une cuisine dans Kitchen Planner." : "Create a kitchen in Kitchen Planner first.") + "</p>");
        }
        body.Append("</div></section>");
      }
      body.Append("<section class='share-plugin-group share-core-group'><header><span class='plugin-mark'>R</span><div><h2>Racinage Free</h2><p>" + (french ? "Actions locales" : "Local actions") + "</p></div></header><div class='actions'>");
      if (receipt["normalized_url"] != "") body.Append("<a class='button ghost' href='" + A(receipt["normalized_url"]) + "' target='_blank' rel='noreferrer'>" + (french ? "Ouvrir le lien" : "Open link") + "</a><button class='button ghost' type='button' data-share-copy='" + A(receipt["normalized_url"]) + "'>" + (french ? "Copier le lien" : "Copy link") + "</button>");
      else body.Append("<button class='button ghost' type='button' data-share-copy='" + A(receipt["payload_text"]) + "'>" + (french ? "Copier le texte" : "Copy text") + "</button>");
      body.Append("<form method='post' action='/share'>" + CsrfInput() + "<input type='hidden' name='action' value='dismiss'><input type='hidden' name='receipt_id' value='" + A(receipt["receipt_id"]) + "'><button class='button ghost' type='submit'>" + (french ? "Ignorer" : "Dismiss") + "</button></form></div></section>");
      if (receipts.Count > 1) {
        body.Append("<nav class='share-recent' aria-label='" + (french ? "Partages récents" : "Recent shares") + "'><strong>" + (french ? "Récents" : "Recent") + "</strong>");
        foreach (Dictionary<string, string> row in receipts.Take(10)) body.Append("<a class='" + (row["receipt_id"] == receipt["receipt_id"] ? "active" : "") + "' href='/share?receipt=" + A(row["receipt_id"]) + "'>" + H(Shorten(row["normalized_url"] != "" ? row["normalized_url"] : row["payload_text"], 54)) + "</a>");
        body.Append("</nav>");
      }
      return body + ShareJs();
    }

    private static string ShareJs() {
      return "<script>(function(){document.addEventListener('click',async function(e){var paste=e.target.closest('[data-share-clipboard]'),copy=e.target.closest('[data-share-copy]'),status=document.querySelector('[data-share-status]');if(paste){try{var text=await navigator.clipboard.readText(),field=paste.closest('form').querySelector('textarea');field.value=text;field.focus();if(status)status.textContent=text?'Clipboard pasted.':'The clipboard is empty.';}catch(_){if(status)status.textContent='Clipboard access is unavailable. Paste with Ctrl+V.';}}if(copy){try{await navigator.clipboard.writeText(copy.getAttribute('data-share-copy'));if(status)status.textContent='Copied.';}catch(_){if(status)status.textContent='Copy failed. Select the text and use Ctrl+C.';}}});})();</script>";
    }

    private static string Shorten(string value, int max) { value = value ?? ""; return value.Length <= max ? value : value.Substring(0, max - 1) + "…"; }
    private static string FormatLocalDate(string value) { DateTime parsed; return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out parsed) ? parsed.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) : ""; }
  }

  internal sealed partial class LocalStore {
    private static void InitializeLocalShareSchema(SqliteDb db) {
      db.Exec("CREATE TABLE IF NOT EXISTS local_share_receipts(receipt_id TEXT PRIMARY KEY,payload_kind TEXT NOT NULL CHECK(payload_kind IN('url','text')),payload_text TEXT NOT NULL,normalized_url TEXT NOT NULL DEFAULT '',source TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN('pending','handled','dismissed','expired')),revision INTEGER NOT NULL DEFAULT 1,received_at TEXT NOT NULL,expires_at TEXT NOT NULL,handled_at TEXT NOT NULL DEFAULT '')");
      db.Exec("CREATE INDEX IF NOT EXISTS idx_local_share_receipts_state ON local_share_receipts(status,received_at)");
      db.Exec("CREATE TABLE IF NOT EXISTS local_share_deliveries(receipt_id TEXT NOT NULL,plugin_slug TEXT NOT NULL,action_id TEXT NOT NULL,target_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL CHECK(status IN('queued','completed','pending','failed')),idempotency_key TEXT NOT NULL UNIQUE,result_id TEXT NOT NULL DEFAULT '',error_message TEXT NOT NULL DEFAULT '',created_at TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(receipt_id,plugin_slug,action_id,target_id),FOREIGN KEY(receipt_id) REFERENCES local_share_receipts(receipt_id) ON DELETE CASCADE)");
      db.Exec("CREATE TABLE IF NOT EXISTS local_kitchen_imports(import_id TEXT PRIMARY KEY,plugin_slug TEXT NOT NULL,workspace_long_id TEXT NOT NULL,receipt_id TEXT NOT NULL DEFAULT '',source_url TEXT NOT NULL,source_hash TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN('queued','extracting','completed','pending','failed','ignored_non_food','cancelled')),latest_mode TEXT NOT NULL DEFAULT 'web_scrape',result_recipe_long_id TEXT NOT NULL DEFAULT '',reason TEXT NOT NULL DEFAULT '',attempts INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL,updated_at TEXT NOT NULL)");
      db.Exec("CREATE INDEX IF NOT EXISTS idx_local_kitchen_import_queue ON local_kitchen_imports(status,created_at)");
      db.Exec("CREATE TABLE IF NOT EXISTS local_kitchen_extraction_runs(run_id TEXT PRIMARY KEY,import_id TEXT NOT NULL,mode TEXT NOT NULL CHECK(mode IN('manual','web_scrape','ai')),status TEXT NOT NULL CHECK(status IN('queued','running','completed','pending','failed','ignored_non_food')),confidence TEXT NOT NULL DEFAULT 'unknown',source_language TEXT NOT NULL DEFAULT 'und',provider_kind TEXT NOT NULL DEFAULT '',model TEXT NOT NULL DEFAULT '',evidence_json TEXT NOT NULL DEFAULT '{}',normalized_output_json TEXT NOT NULL DEFAULT '{}',reason TEXT NOT NULL DEFAULT '',created_at TEXT NOT NULL,completed_at TEXT NOT NULL DEFAULT '',FOREIGN KEY(import_id) REFERENCES local_kitchen_imports(import_id) ON DELETE CASCADE)");
      db.Exec("CREATE INDEX IF NOT EXISTS idx_local_kitchen_runs_import ON local_kitchen_extraction_runs(import_id,created_at)");
    }

    internal void ImportCommandLineShare(string[] args) {
      if (args == null) return;
      for (int i = 0; i + 1 < args.Length; i++) {
        if (args[i] == "--share-url" || args[i] == "--share-text") {
          try { CreateShareReceipt(args[i + 1], "command_line"); } catch { }
          i++;
        }
      }
      ImportShareInbox();
    }

    internal void ImportShareInbox() {
      Directory.CreateDirectory(PortablePaths.ShareInboxDir);
      foreach (string file in Directory.GetFiles(PortablePaths.ShareInboxDir, "*.json").Take(20)) {
        try {
          FileInfo info = new FileInfo(file);
          if (info.Length < 2 || info.Length > 65536) { File.Delete(file); continue; }
          Dictionary<string, object> item = json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
          if (item != null) CreateShareReceipt(GetString(item, "value"), "windows_share_target");
          File.Delete(file);
        } catch (Exception error) { Program.Log("Share inbox item rejected: " + error.GetType().Name); try { File.Delete(file); } catch { } }
      }
    }

    internal string CreateShareReceipt(string value, string source) {
      value = (value ?? "").Trim();
      if (value.Length == 0 || value.Length > 32768) throw new InvalidDataException("Share a link or text of 32 KB or fewer.");
      string url = LocalShareContract.ExtractHttpUrl(value), kind = url == "" ? "text" : "url", id = "share_" + Guid.NewGuid().ToString("N"), now = Now();
      using (SqliteDb db = Open()) {
        db.Execute("UPDATE local_share_receipts SET status='expired',revision=revision+1 WHERE status IN('pending','handled') AND expires_at<?", now);
        db.Execute("INSERT INTO local_share_receipts(receipt_id,payload_kind,payload_text,normalized_url,source,status,revision,received_at,expires_at)VALUES(?,?,?,?,?,'pending',1,?,?)", id, kind, value, url, SafeShareSource(source), now, DateTime.UtcNow.AddDays(30).ToString("o", CultureInfo.InvariantCulture));
        List<Dictionary<string, string> > overflow = db.Query("SELECT receipt_id FROM local_share_receipts ORDER BY received_at DESC LIMIT -1 OFFSET 100");
        foreach (Dictionary<string, string> row in overflow) db.Execute("DELETE FROM local_share_receipts WHERE receipt_id=?", row["receipt_id"]);
      }
      ProtectDatabaseFile(); return id;
    }

    internal int PendingShareReceiptCount() { using (SqliteDb db = Open()) return ToInt(db.Scalar("SELECT COUNT(*) FROM local_share_receipts WHERE status='pending' AND expires_at>=?", Now())); }

    internal List<Dictionary<string, string> > GetShareReceipts() {
      using (SqliteDb db = Open()) return db.Query("SELECT receipt_id,payload_kind,payload_text,normalized_url,source,status,revision,received_at,expires_at,handled_at FROM local_share_receipts WHERE status!='expired' ORDER BY received_at DESC LIMIT 100");
    }

    internal void DismissShareReceipt(string receiptId) {
      receiptId = SafeShareId(receiptId);
      using (SqliteDb db = Open()) db.Execute("UPDATE local_share_receipts SET status='dismissed',revision=revision+1,handled_at=? WHERE receipt_id=?", Now(), receiptId);
      ProtectDatabaseFile();
    }

    internal List<LocalShareActionView> GetShareActions(string receiptId) {
      receiptId = SafeShareId(receiptId);
      List<LocalShareActionView> output = new List<LocalShareActionView>();
      using (SqliteDb db = Open()) {
        Dictionary<string, string> receipt = db.QueryOne("SELECT payload_kind FROM local_share_receipts WHERE receipt_id=? AND status!='expired' LIMIT 1", receiptId);
        if (receipt == null) return output;
        foreach (Dictionary<string, string> plugin in db.Query("SELECT slug,name,share_actions_json FROM plugin_installs WHERE status='enabled' AND share_actions_json<>'' ORDER BY name COLLATE NOCASE")) {
          PortableShareActions contract = LocalShareContract.ParseValidated(plugin["slug"], plugin["share_actions_json"]);
          if (contract == null) continue;
          foreach (PortableShareAction action in contract.actions) {
            if (!action.accepts.Contains(receipt["payload_kind"])) continue;
            List<Dictionary<string, string> > targets = ShareActionTargets(db, plugin["slug"], action.target_kind);
            Dictionary<string, string> delivery = db.QueryOne("SELECT status,result_id,error_message,updated_at FROM local_share_deliveries WHERE receipt_id=? AND plugin_slug=? AND action_id=? ORDER BY updated_at DESC LIMIT 1", receiptId, plugin["slug"], action.id);
            output.Add(new LocalShareActionView { PluginSlug = plugin["slug"], PluginName = plugin["name"], Action = action, Targets = targets, Delivery = delivery });
          }
        }
      }
      return output;
    }

    internal Dictionary<string, string> ExecuteShareAction(string receiptId, string revision, string pluginSlug, string actionId, string targetId, string idempotencyKey) {
      receiptId = SafeShareId(receiptId); pluginSlug = (pluginSlug ?? "").Trim(); actionId = (actionId ?? "").Trim(); targetId = SafeLongId(targetId); idempotencyKey = SafeIdempotency(idempotencyKey);
      int expected; if (!Int32.TryParse(revision, NumberStyles.Integer, CultureInfo.InvariantCulture, out expected) || expected < 1) throw new InvalidDataException("The shared item revision is invalid.");
      string url, now = Now();
      using (SqliteDb db = Open()) {
        Dictionary<string, string> receipt = db.QueryOne("SELECT payload_kind,normalized_url,revision,status,expires_at FROM local_share_receipts WHERE receipt_id=? LIMIT 1", receiptId);
        if (receipt == null || receipt["status"] == "dismissed" || receipt["status"] == "expired" || String.CompareOrdinal(receipt["expires_at"], now) < 0) throw new InvalidOperationException("The shared item is no longer available.");
        if (ToInt(receipt["revision"]) != expected) throw new InvalidOperationException("The shared item changed. Reopen it and try again.");
        Dictionary<string, string> plugin = db.QueryOne("SELECT share_actions_json FROM plugin_installs WHERE slug=? AND status='enabled' LIMIT 1", pluginSlug);
        PortableShareActions contract = plugin == null ? null : LocalShareContract.ParseValidated(pluginSlug, plugin["share_actions_json"]);
        PortableShareAction selected = contract == null ? null : contract.actions.FirstOrDefault(item => item.id == actionId && item.accepts.Contains(receipt["payload_kind"]));
        if (selected == null) throw new InvalidOperationException("This plugin action is no longer installed or authorized.");
        if (selected.target_kind == "plugin_workspace" && !ShareActionTargets(db, pluginSlug, selected.target_kind).Any(row => row["long_id"] == targetId)) throw new InvalidOperationException("Choose an available plugin workspace.");
        url = receipt["normalized_url"];
        Dictionary<string, string> replay = db.QueryOne("SELECT status,result_id,error_message FROM local_share_deliveries WHERE idempotency_key=? LIMIT 1", idempotencyKey);
        if (replay != null) return new Dictionary<string, string> { { "status", replay["status"] }, { "result_id", replay["result_id"] }, { "message", replay["error_message"] == "" ? "This action was already queued." : replay["error_message"] } };
        db.Execute("INSERT INTO local_share_deliveries(receipt_id,plugin_slug,action_id,target_id,status,idempotency_key,created_at,updated_at)VALUES(?,?,?,?,'queued',?,?,?) ON CONFLICT(receipt_id,plugin_slug,action_id,target_id) DO UPDATE SET status='queued',idempotency_key=excluded.idempotency_key,error_message='',updated_at=excluded.updated_at", receiptId, pluginSlug, actionId, targetId, idempotencyKey, now, now);
      }
      try {
        string resultId;
        if (pluginSlug == "kitchen-planner" && actionId == "import_recipe_source") {
          Dictionary<string, object> queued = (Dictionary<string, object>)QueueKitchenSourceImport(pluginSlug, new Dictionary<string, object> { { "workspace_long_id", targetId }, { "source_url", url }, { "receipt_id", receiptId } });
          resultId = GetString(queued, "import_id");
        } else throw new InvalidOperationException("The reviewed local handler for this action is unavailable.");
        using (SqliteDb db = Open()) {
          db.Execute("UPDATE local_share_deliveries SET status='queued',result_id=?,error_message='',updated_at=? WHERE idempotency_key=?", resultId, Now(), idempotencyKey);
          db.Execute("UPDATE local_share_receipts SET status='handled',handled_at=? WHERE receipt_id=?", Now(), receiptId);
          db.Execute("INSERT INTO local_plugin_settings(slug,setting_key,setting_value,updated_at)VALUES(?,'last_share_workspace',?,?) ON CONFLICT(slug,setting_key) DO UPDATE SET setting_value=excluded.setting_value,updated_at=excluded.updated_at", pluginSlug, targetId, Now());
        }
        ProtectDatabaseFile(); return new Dictionary<string, string> { { "status", "queued" }, { "result_id", resultId }, { "message", "Recipe source queued for traditional extraction." } };
      } catch (Exception error) {
        using (SqliteDb db = Open()) db.Execute("UPDATE local_share_deliveries SET status='failed',error_message=?,updated_at=? WHERE idempotency_key=?", SafeReason(error.Message), Now(), idempotencyKey);
        ProtectDatabaseFile(); throw;
      }
    }

    private static List<Dictionary<string, string> > ShareActionTargets(SqliteDb db, string slug, string targetKind) {
      List<Dictionary<string, string> > result = new List<Dictionary<string, string> >();
      if (targetKind == "none") return result;
      if (targetKind != "plugin_workspace" || slug != "kitchen-planner") return result;
      string selected = Convert.ToString(db.Scalar("SELECT setting_value FROM local_plugin_settings WHERE slug=? AND setting_key='last_share_workspace' LIMIT 1", slug), CultureInfo.InvariantCulture) ?? "";
      foreach (Dictionary<string, string> row in db.Query("SELECT long_id,data_json FROM local_plugin_records WHERE slug=? AND record_type='workspaces' AND status='active' ORDER BY updated_at DESC", slug)) {
        Dictionary<string, object> data; try { data = new JavaScriptSerializer().DeserializeObject(row["data_json"]) as Dictionary<string, object>; } catch { continue; }
        if (data == null) continue;
        result.Add(new Dictionary<string, string> { { "long_id", row["long_id"] }, { "name", GetString(data, "name") }, { "selected", row["long_id"] == selected || (selected == "" && result.Count == 0) ? "1" : "0" } });
      }
      return result;
    }

    internal object QueueKitchenSourceImport(string slug, Dictionary<string, object> payload) {
      if (slug != "kitchen-planner") throw new InvalidOperationException("The source queue is available only to Kitchen Planner.");
      string workspace = SafeLongId(GetString(payload, "workspace_long_id")), receipt = SafeShareId(GetString(payload, "receipt_id"));
      string url = LocalShareContract.NormalizeHttpUrl(GetString(payload, "source_url"));
      Uri sourceUri;
      if (url == "" || !Uri.TryCreate(url, UriKind.Absolute, out sourceUri) || sourceUri.Scheme != Uri.UriSchemeHttps || sourceUri.Port != 443) throw new InvalidDataException("Choose a public HTTPS recipe source using the standard secure port.");
      string importId = "kitchen_import_" + Guid.NewGuid().ToString("N"), now = Now();
      using (SqliteDb db = Open()) {
        RequireReference(db, slug, "", "workspaces", workspace);
        db.Execute("INSERT INTO local_kitchen_imports(import_id,plugin_slug,workspace_long_id,receipt_id,source_url,source_hash,status,latest_mode,created_at,updated_at)VALUES(?,?,?,?,?,?,'queued','web_scrape',?,?)", importId, slug, workspace, receipt, url, HashText(url.ToLowerInvariant()), now, now);
      }
      ProtectDatabaseFile(); return new Dictionary<string, object> { { "import_id", importId }, { "status", "queued" }, { "mode", "web_scrape" }, { "hosted_credits", false } };
    }

    internal object RetryKitchenSourceImport(string slug, Dictionary<string, object> payload) {
      string importId = SafeImportId(GetString(payload, "import_id"));
      using (SqliteDb db = Open()) {
        int changed = db.Execute("UPDATE local_kitchen_imports SET status='queued',reason='',updated_at=? WHERE import_id=? AND plugin_slug=? AND status IN('pending','failed')", Now(), importId, slug);
        if (changed != 1) throw new InvalidOperationException("This Kitchen import cannot be retried.");
      }
      ProtectDatabaseFile(); return new Dictionary<string, object> { { "import_id", importId }, { "status", "queued" } };
    }

    internal void ProcessPendingKitchenImportsAsync() {
      ThreadPool.QueueUserWorkItem(delegate {
        List<string> ids;
        using (SqliteDb db = Open()) ids = db.Query("SELECT import_id FROM local_kitchen_imports WHERE status='queued' ORDER BY created_at LIMIT 5").Select(row => row["import_id"]).ToList();
        foreach (string id in ids) ProcessKitchenImport(id);
      });
    }

    internal string BeginKitchenAiExtraction(Dictionary<string, object> payload) {
      string importId = SafeImportId(GetString(payload, "import_id"));
      if (importId == "") return "";
      string runId = "kitchen_run_" + Guid.NewGuid().ToString("N"), now = Now();
      using (SqliteDb db = Open()) {
        if (db.QueryOne("SELECT import_id FROM local_kitchen_imports WHERE import_id=? AND plugin_slug='kitchen-planner' LIMIT 1", importId) == null) throw new InvalidOperationException("The Kitchen import is unavailable.");
        db.Execute("INSERT INTO local_kitchen_extraction_runs(run_id,import_id,mode,status,provider_kind,created_at)VALUES(?,?,'ai','running','loopback',?)", runId, importId, now);
      }
      ProtectDatabaseFile(); return runId;
    }

    internal void CompleteKitchenAiExtraction(string runId, Dictionary<string, object> result) {
      if (String.IsNullOrEmpty(runId)) return;
      bool isFood = ToBool(result.ContainsKey("is_food") ? result["is_food"] : true);
      string status = isFood ? "pending" : "ignored_non_food", reason = isFood ? "Local AI rerun is ready for field-by-field review. Manual records were not overwritten." : SafeReason(GetString(result, "reason"));
      string provider = SafeReason(GetString(result, "provider")), model = SafeReason(GetString(result, "model")), confidence = SafeReason(GetString(result, "confidence")); if (confidence == "") confidence = "unknown";
      string encoded = json.Serialize(result); if (encoded.Length > 500000) encoded = "{}";
      using (SqliteDb db = Open()) {
        Dictionary<string, string> run = db.QueryOne("SELECT import_id FROM local_kitchen_extraction_runs WHERE run_id=? AND mode='ai' AND status='running' LIMIT 1", runId); if (run == null) return;
        db.Execute("UPDATE local_kitchen_extraction_runs SET status=?,confidence=?,source_language=?,provider_kind=?,model=?,normalized_output_json=?,reason=?,completed_at=? WHERE run_id=?", status, confidence, SafeReason(GetString(result, "source_language")), provider, model, encoded, reason, Now(), runId);
        db.Execute("UPDATE local_kitchen_imports SET latest_mode='ai',status=?,reason=?,updated_at=? WHERE import_id=?", status == "ignored_non_food" ? "ignored_non_food" : "pending", reason, Now(), run["import_id"]);
      }
      ProtectDatabaseFile();
    }

    internal void FailKitchenAiExtraction(string runId, string failure) {
      if (String.IsNullOrEmpty(runId)) return;
      string reason = SafeReason(failure), now = Now();
      using (SqliteDb db = Open()) {
        Dictionary<string, string> run = db.QueryOne("SELECT import_id FROM local_kitchen_extraction_runs WHERE run_id=? AND mode='ai' AND status='running' LIMIT 1", runId); if (run == null) return;
        db.Execute("UPDATE local_kitchen_extraction_runs SET status='failed',reason=?,completed_at=? WHERE run_id=?", reason, now, runId);
        db.Execute("UPDATE local_kitchen_imports SET latest_mode='ai',status='pending',reason=?,updated_at=? WHERE import_id=?", reason, now, run["import_id"]);
      }
      ProtectDatabaseFile();
    }

    private void ProcessKitchenImport(string importId) {
      string runId = "kitchen_run_" + Guid.NewGuid().ToString("N"), now = Now(); Dictionary<string, string> item;
      using (SqliteDb db = Open()) {
        if (db.Execute("UPDATE local_kitchen_imports SET status='extracting',attempts=attempts+1,updated_at=? WHERE import_id=? AND status='queued'", now, importId) != 1) return;
        item = db.QueryOne("SELECT plugin_slug,workspace_long_id,receipt_id,source_url FROM local_kitchen_imports WHERE import_id=? LIMIT 1", importId);
        db.Execute("INSERT INTO local_kitchen_extraction_runs(run_id,import_id,mode,status,created_at)VALUES(?,?,'web_scrape','running',?)", runId, importId, now);
      }
      try {
        Uri uri = new Uri(item["source_url"]); System.Net.IPAddress address = KitchenResolvePublicAddress(uri.DnsSafeHost);
        Dictionary<string, object> fetched = KitchenPinnedHttpsGet(uri, address, 2 * 1024 * 1024);
        TraditionalRecipeResult extraction = TraditionalRecipeExtractor.Extract(GetString(fetched, "text"), GetString(fetched, "content_type"), item["source_url"]);
        string end = Now();
        if (extraction.ExplicitNonFood) {
          using (SqliteDb db = Open()) {
            db.Execute("UPDATE local_kitchen_imports SET status='ignored_non_food',reason=?,updated_at=? WHERE import_id=?", "The page explicitly describes non-food content and contains no recipe evidence.", end, importId);
            db.Execute("UPDATE local_kitchen_extraction_runs SET status='ignored_non_food',confidence='high',source_language=?,evidence_json=?,reason=?,completed_at=? WHERE run_id=?", extraction.SourceLanguage, extraction.EvidenceJson, "Non-food source", end, runId);
            UpdateDeliveryForImport(db, importId, "completed", "", end);
          }
          ProtectDatabaseFile(); return;
        }
        if (extraction.Ingredients.Count == 0 || extraction.Steps.Count == 0) {
          string reason = "Traditional extraction could not find both an ingredient list and ordered cooking steps. Review the source manually or configure optional local AI.";
          using (SqliteDb db = Open()) {
            db.Execute("UPDATE local_kitchen_imports SET status='pending',reason=?,updated_at=? WHERE import_id=?", reason, end, importId);
            db.Execute("UPDATE local_kitchen_extraction_runs SET status='pending',confidence=?,source_language=?,evidence_json=?,normalized_output_json=?,reason=?,completed_at=? WHERE run_id=?", extraction.Confidence, extraction.SourceLanguage, extraction.EvidenceJson, extraction.ToJson(), reason, end, runId);
            UpdateDeliveryForImport(db, importId, "pending", reason, end);
          }
          ProtectDatabaseFile(); return;
        }
        List<Dictionary<string, object> > ingredients = extraction.Ingredients.Select(TraditionalRecipeExtractor.ParseIngredient).ToList();
        bool quantities = ingredients.All(line => Convert.ToDouble(line["amount"], CultureInfo.InvariantCulture) > 0);
        string recipeStatus = extraction.Structured && extraction.SourceLanguage == "en" && quantities ? "active" : "pending";
        Dictionary<string, object> recipe = new Dictionary<string, object> {
          { "title", extraction.Title == "" ? "Imported recipe from " + uri.Host : extraction.Title }, { "description", extraction.Description }, { "servings", extraction.Servings <= 0 ? 1 : extraction.Servings },
          { "status", recipeStatus }, { "source_url", item["source_url"] }, { "source_language", extraction.SourceLanguage }, { "canonical_language", "en" }, { "ingredients", ingredients.ToArray() },
          { "steps", extraction.Steps.Select((step, index) => (object)new Dictionary<string, object> { { "action", step }, { "position", index + 1 }, { "duration_minutes", 0 }, { "timer_eligible", false } }).ToArray() },
          { "preparation_minutes", extraction.PreparationMinutes }, { "cooking_minutes", extraction.CookingMinutes }, { "total_minutes", extraction.TotalMinutes }, { "extraction_mode", "web_scrape" }, { "evidence_confidence", extraction.Confidence }, { "import_id", importId }
        };
        Dictionary<string, object> saved = (Dictionary<string, object>)KitchenSaveRecord(item["plugin_slug"], "recipes", new Dictionary<string, object> { { "workspace_long_id", item["workspace_long_id"] }, { "data", recipe } });
        string recipeId = GetString(saved, "long_id");
        using (SqliteDb db = Open()) {
          db.Execute("UPDATE local_kitchen_imports SET status='completed',result_recipe_long_id=?,reason='',updated_at=? WHERE import_id=?", recipeId, end, importId);
          db.Execute("UPDATE local_kitchen_extraction_runs SET status='completed',confidence=?,source_language=?,evidence_json=?,normalized_output_json=?,completed_at=? WHERE run_id=?", extraction.Confidence, extraction.SourceLanguage, extraction.EvidenceJson, extraction.ToJson(), end, runId);
          UpdateDeliveryForImport(db, importId, "completed", "", end);
        }
        ProtectDatabaseFile();
      } catch (Exception failure) {
        string reason = SafeReason(failure.Message), end = Now();
        using (SqliteDb db = Open()) {
          db.Execute("UPDATE local_kitchen_imports SET status='pending',reason=?,updated_at=? WHERE import_id=?", reason, end, importId);
          db.Execute("UPDATE local_kitchen_extraction_runs SET status='pending',reason=?,completed_at=? WHERE run_id=?", reason, end, runId);
          UpdateDeliveryForImport(db, importId, "pending", reason, end);
        }
        ProtectDatabaseFile(); Program.Log("Traditional Kitchen extraction pending: " + failure.GetType().Name);
      }
    }

    private static void UpdateDeliveryForImport(SqliteDb db, string importId, string status, string reason, string now) {
      db.Execute("UPDATE local_share_deliveries SET status=?,error_message=?,updated_at=? WHERE result_id=?", status, reason, now, importId);
    }

    private List<Dictionary<string, object> > KitchenSourceImports(SqliteDb db, string slug) {
      List<Dictionary<string, object> > output = new List<Dictionary<string, object> >();
      foreach (Dictionary<string, string> row in db.Query("SELECT import_id,workspace_long_id,receipt_id,source_url,status,latest_mode,result_recipe_long_id,reason,attempts,created_at,updated_at FROM local_kitchen_imports WHERE plugin_slug=? ORDER BY created_at DESC LIMIT 500", slug)) { Dictionary<string, object> item = row.ToDictionary(pair => pair.Key, pair => (object)pair.Value); item["attempts"] = ToInt(row["attempts"]); output.Add(item); }
      return output;
    }

    private List<Dictionary<string, object> > KitchenExtractionRuns(SqliteDb db, string slug) {
      List<Dictionary<string, object> > output = new List<Dictionary<string, object> >();
      foreach (Dictionary<string, string> row in db.Query("SELECT r.run_id,r.import_id,r.mode,r.status,r.confidence,r.source_language,r.provider_kind,r.model,r.evidence_json,r.normalized_output_json,r.reason,r.created_at,r.completed_at FROM local_kitchen_extraction_runs r JOIN local_kitchen_imports i ON i.import_id=r.import_id WHERE i.plugin_slug=? ORDER BY r.created_at DESC LIMIT 1000", slug)) output.Add(row.ToDictionary(pair => pair.Key, pair => (object)pair.Value));
      return output;
    }

    private static string SafeShareId(string value) { value = value ?? ""; return Regex.IsMatch(value, @"^share_[a-f0-9]{32}$") ? value : ""; }
    private static string SafeImportId(string value) { value = value ?? ""; return Regex.IsMatch(value, @"^kitchen_import_[a-f0-9]{32}$") ? value : ""; }
    private static string SafeIdempotency(string value) { value = value ?? ""; if (!Regex.IsMatch(value, @"^[a-f0-9]{64}$")) throw new InvalidDataException("The share action idempotency key is invalid."); return value; }
    private static string SafeShareSource(string value) { return new[] { "paste", "command_line", "windows_share_target" }.Contains(value) ? value : "paste"; }
    private static string SafeReason(string value) { value = Regex.Replace(value ?? "", @"\s+", " ").Trim(); return value.Length > 500 ? value.Substring(0, 500) : value; }
  }

  internal sealed class TraditionalRecipeResult {
    internal string Title = "", Description = "", Confidence = "unknown", SourceLanguage = "und", EvidenceJson = "{}";
    internal double Servings;
    internal int PreparationMinutes, CookingMinutes, TotalMinutes;
    internal bool Structured, ExplicitNonFood;
    internal readonly List<string> Ingredients = new List<string>();
    internal readonly List<string> Steps = new List<string>();
    internal string ToJson() { return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "title", Title }, { "description", Description }, { "servings", Servings }, { "ingredients", Ingredients }, { "steps", Steps }, { "preparation_minutes", PreparationMinutes }, { "cooking_minutes", CookingMinutes }, { "total_minutes", TotalMinutes } }); }
  }

  internal static class TraditionalRecipeExtractor {
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    private static readonly string[] IngredientHeadings = { "ingredients", "ingredient", "ingrédients", "ingredientes", "zutaten", "ingredienti", "المكونات" };
    private static readonly string[] StepHeadings = { "instructions", "directions", "method", "preparation", "préparation", "méthode", "instrucciones", "preparación", "zubereitung", "procedimento", "الطريقة", "التحضير" };

    internal static TraditionalRecipeResult Extract(string source, string contentType, string url) {
      TraditionalRecipeResult result = new TraditionalRecipeResult(); source = source ?? "";
      List<string> evidence = new List<string>();
      foreach (Match match in Regex.Matches(source, @"<script\b[^>]*type\s*=\s*['""]application/ld\+json['""][^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) {
        try { FindRecipe(Json.DeserializeObject(WebUtility.HtmlDecode(match.Groups[1].Value)), result, evidence); } catch { }
        if (result.Structured) break;
      }
      if (!result.Structured) ExtractSemanticHtml(source, result, evidence);
      string visible = StripTags(source);
      if (result.Title == "") result.Title = Meta(source, "og:title");
      if (result.Title == "") { Match h1 = Regex.Match(source, @"<h1\b[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase); if (h1.Success) result.Title = Clean(StripTags(h1.Groups[1].Value)); }
      result.SourceLanguage = DetectLanguage(source, visible);
      result.ExplicitNonFood = !result.Structured && result.Ingredients.Count == 0 && result.Steps.Count == 0 && Regex.IsMatch(source, @"""@type""\s*:\s*""(Person|SoftwareApplication|WebSite|JobPosting|MusicRecording)""", RegexOptions.IgnoreCase);
      result.Confidence = result.Structured && result.Ingredients.Count > 0 && result.Steps.Count > 0 ? "high" : result.Ingredients.Count > 0 && result.Steps.Count > 0 ? "medium" : "low";
      result.EvidenceJson = Json.Serialize(new Dictionary<string, object> { { "source_url", url }, { "anchors", evidence.Take(100).ToArray() }, { "content_sha256", Hash(source) } });
      return result;
    }

    private static void FindRecipe(object node, TraditionalRecipeResult result, List<string> evidence) {
      Dictionary<string, object> map = node as Dictionary<string, object>;
      if (map != null) {
        if (IsRecipeType(Value(map, "@type"))) { ReadRecipe(map, result, evidence); return; }
        foreach (object value in map.Values) { FindRecipe(value, result, evidence); if (result.Structured) return; }
      }
      object[] array = node as object[]; if (array != null) foreach (object value in array) { FindRecipe(value, result, evidence); if (result.Structured) return; }
    }

    private static void ReadRecipe(Dictionary<string, object> map, TraditionalRecipeResult result, List<string> evidence) {
      result.Structured = true; result.Title = Clean(Value(map, "name")); result.Description = Clean(StripTags(Value(map, "description")));
      result.Servings = FirstNumber(Value(map, "recipeYield")); result.PreparationMinutes = IsoMinutes(Value(map, "prepTime")); result.CookingMinutes = IsoMinutes(Value(map, "cookTime")); result.TotalMinutes = IsoMinutes(Value(map, "totalTime"));
      AddStrings(map.ContainsKey("recipeIngredient") ? map["recipeIngredient"] : null, result.Ingredients);
      AddInstructions(map.ContainsKey("recipeInstructions") ? map["recipeInstructions"] : null, result.Steps);
      evidence.Add("jsonld:Recipe");
    }

    private static void ExtractSemanticHtml(string html, TraditionalRecipeResult result, List<string> evidence) {
      foreach (Match match in Regex.Matches(html, @"<[^>]+itemprop\s*=\s*['""]recipeIngredient['""][^>]*>([\s\S]*?)</[^>]+>", RegexOptions.IgnoreCase)) AddUnique(result.Ingredients, Clean(StripTags(match.Groups[1].Value)));
      foreach (Match match in Regex.Matches(html, @"<[^>]+itemprop\s*=\s*['""]recipeInstructions['""][^>]*>([\s\S]*?)</[^>]+>", RegexOptions.IgnoreCase)) AddUnique(result.Steps, Clean(StripTags(match.Groups[1].Value)));
      if (result.Ingredients.Count > 0) evidence.Add("microdata:recipeIngredient"); if (result.Steps.Count > 0) evidence.Add("microdata:recipeInstructions");
      MatchCollection sections = Regex.Matches(html, @"<h[2-5]\b[^>]*>([\s\S]*?)</h[2-5]>([\s\S]*?)(?=<h[2-5]\b|</body>|$)", RegexOptions.IgnoreCase);
      foreach (Match section in sections) {
        string heading = Clean(StripTags(section.Groups[1].Value)).ToLowerInvariant(); List<string> lines = ListItems(section.Groups[2].Value);
        if (IngredientHeadings.Any(value => heading.Contains(value))) { foreach (string line in lines) AddUnique(result.Ingredients, line); if (lines.Count > 0) evidence.Add("heading:" + heading); }
        if (StepHeadings.Any(value => heading.Contains(value))) { foreach (string line in lines) AddUnique(result.Steps, line); if (lines.Count > 0) evidence.Add("heading:" + heading); }
      }
      if (result.Ingredients.Count == 0) foreach (Match match in Regex.Matches(html, @"<[^>]+class\s*=\s*['""][^'""]*(recipe[-_ ]ingredient|ingredient[-_ ]item)[^'""]*['""][^>]*>([\s\S]*?)</[^>]+>", RegexOptions.IgnoreCase)) AddUnique(result.Ingredients, Clean(StripTags(match.Groups[2].Value)));
      if (result.Steps.Count == 0) foreach (Match match in Regex.Matches(html, @"<[^>]+class\s*=\s*['""][^'""]*(recipe[-_ ]instruction|instruction[-_ ]step|direction[-_ ]step)[^'""]*['""][^>]*>([\s\S]*?)</[^>]+>", RegexOptions.IgnoreCase)) AddUnique(result.Steps, Clean(StripTags(match.Groups[2].Value)));
    }

    internal static Dictionary<string, object> ParseIngredient(string original) {
      original = Clean(original); double amount = 0; string unit = "", name = original;
      Match match = Regex.Match(original, @"^(?<amount>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)\s*(?<unit>cups?|tbsp|tsp|tablespoons?|teaspoons?|g|kg|mg|ml|l|oz|lb|pounds?|cloves?|pieces?|cans?)?\b\s*(?<name>.+)$", RegexOptions.IgnoreCase);
      if (match.Success) { amount = ParseAmount(match.Groups["amount"].Value); unit = match.Groups["unit"].Value; name = match.Groups["name"].Value.Trim(' ', '-', ':'); }
      return new Dictionary<string, object> { { "original_wording", original }, { "name", name == "" ? original : name }, { "amount", amount }, { "unit", unit }, { "preparation", "" }, { "optional", Regex.IsMatch(original, @"\boptional\b", RegexOptions.IgnoreCase) } };
    }

    private static void AddInstructions(object value, List<string> target) {
      string text = value as string; if (text != null) { AddUnique(target, Clean(StripTags(text))); return; }
      object[] array = value as object[]; if (array != null) foreach (object item in array) AddInstructions(item, target);
      Dictionary<string, object> map = value as Dictionary<string, object>; if (map != null) { string item = Value(map, "text"); if (item == "") item = Value(map, "name"); AddUnique(target, Clean(StripTags(item))); if (map.ContainsKey("itemListElement")) AddInstructions(map["itemListElement"], target); }
    }
    private static void AddStrings(object value, List<string> target) { string text = value as string; if (text != null) AddUnique(target, Clean(StripTags(text))); object[] array = value as object[]; if (array != null) foreach (object item in array) AddStrings(item, target); }
    private static List<string> ListItems(string html) { List<string> list = new List<string>(); foreach (Match match in Regex.Matches(html, @"<li\b[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase)) AddUnique(list, Clean(StripTags(match.Groups[1].Value))); if (list.Count == 0) foreach (string line in StripTags(html).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (line.Trim().Length > 2) AddUnique(list, Clean(line)); return list; }
    private static void AddUnique(List<string> target, string value) { if (value.Length > 1 && value.Length <= 1000 && !target.Contains(value)) target.Add(value); }
    private static string Value(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
    private static bool IsRecipeType(string value) { return value.Split(',').Any(part => part.Trim().Equals("Recipe", StringComparison.OrdinalIgnoreCase)); }
    private static string Meta(string html, string property) { Match match = Regex.Match(html, "<meta\\b[^>]*(?:property|name)\\s*=\\s*['\"]" + Regex.Escape(property) + "['\"][^>]*content\\s*=\\s*['\"]([^'\"]+)['\"][^>]*>", RegexOptions.IgnoreCase); if (!match.Success) match = Regex.Match(html, "<meta\\b[^>]*content\\s*=\\s*['\"]([^'\"]+)['\"][^>]*(?:property|name)\\s*=\\s*['\"]" + Regex.Escape(property) + "['\"][^>]*>", RegexOptions.IgnoreCase); return match.Success ? Clean(WebUtility.HtmlDecode(match.Groups[1].Value)) : ""; }
    private static string StripTags(string value) { return Clean(WebUtility.HtmlDecode(Regex.Replace(value ?? "", @"<script\b[^>]*>[\s\S]*?</script>|<style\b[^>]*>[\s\S]*?</style>|<[^>]+>", " ", RegexOptions.IgnoreCase))); }
    private static string Clean(string value) { return Regex.Replace(value ?? "", @"\s+", " ").Trim(); }
    private static double FirstNumber(string value) { Match match = Regex.Match(value ?? "", @"\d+(?:[\.,]\d+)?"); double number; return match.Success && Double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out number) ? number : 0; }
    private static int IsoMinutes(string value) { Match match = Regex.Match(value ?? "", @"^P(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?)?$", RegexOptions.IgnoreCase); if (!match.Success) return 0; return ToInt(match, "d") * 1440 + ToInt(match, "h") * 60 + ToInt(match, "m"); }
    private static int ToInt(Match match, string group) { int value; return Int32.TryParse(match.Groups[group].Value, out value) ? value : 0; }
    private static double ParseAmount(string value) { string[] parts = value.Split('/'); if (parts.Length == 2) { double a, b; if (Double.TryParse(parts[0].Trim(), out a) && Double.TryParse(parts[1].Trim(), out b) && b != 0) return a / b; } double parsed; return Double.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed) ? parsed : 0; }
    private static string DetectLanguage(string html, string text) { Match match = Regex.Match(html ?? "", @"<html\b[^>]*lang\s*=\s*['""](?<lang>[a-z]{2,3})", RegexOptions.IgnoreCase); if (match.Success) return match.Groups["lang"].Value.ToLowerInvariant(); return (text ?? "").Any(c => c > 127) ? "und" : "en"; }
    private static string Hash(string value) { using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant(); }
  }
}

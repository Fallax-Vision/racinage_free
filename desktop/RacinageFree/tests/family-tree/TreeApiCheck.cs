using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// End-to-end check of the local tree endpoints against the real LocalServer, in an isolated temporary
// data folder (PortablePaths is redirected by reflection, so the user's own Racinage Free data is never used).
namespace RacinageFreeDesktop {
  internal static partial class FamilyTreeSelfCheck {
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    private static string baseUrl, session, csrf;

    private static LocalStore StartIsolated(string root) {
      if (root == "") root = Path.Combine(Path.GetTempPath(), "racinage-free-tree-api-" + Guid.NewGuid().ToString("N"));
      foreach (string field in new[] { "Root", "DataDir", "MediaDir", "LogsDir", "UpdatesDir", "TokensDir", "WebViewDir", "PluginsDir", "PluginCacheDir", "ShareInboxDir" }) {
        string value = field == "Root" ? root : field == "ShareInboxDir" ? Path.Combine(root, "data", "share-inbox") : Path.Combine(root, field == "DataDir" ? "data" : field == "MediaDir" ? "media" : field == "LogsDir" ? "logs" : field == "UpdatesDir" ? "updates" : field == "TokensDir" ? "device-tokens" : field == "WebViewDir" ? "webview" : field == "PluginsDir" ? "plugins" : "plugin-cache");
        typeof(PortablePaths).GetField(field, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
      }
      PortablePaths.EnsureMutableFolders();
      LocalStore store = new LocalStore();
      store.Initialize();
      if (!store.HasUser()) store.CreateAccount("Tree Tester", "tree-tester", Guid.NewGuid().ToString("N"), "Test Family");
      session = store.IssueSession(); csrf = store.CsrfToken;
      LocalServer server = new LocalServer(store);
      server.Start();
      baseUrl = server.BaseUrl;
      return store;
    }

    private static int Serve(string root) {
      StartIsolated(root);
      Console.WriteLine("Serving " + baseUrl + " session=" + session);
      Thread.Sleep(Timeout.Infinite);
      return 0;
    }

    private static string Http(string method, string path, byte[] body, string contentType, out int status) {
      HttpWebRequest request = (HttpWebRequest)WebRequest.Create(baseUrl + path);
      request.Method = method; request.AllowAutoRedirect = false;
      request.Headers["Cookie"] = "rf_session=" + session;
      request.Headers["X-Racinage-CSRF"] = csrf;
      if (body != null) { request.ContentType = contentType; request.ContentLength = body.Length; using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length); }
      HttpWebResponse response;
      try { response = (HttpWebResponse)request.GetResponse(); } catch (WebException error) { response = (HttpWebResponse)error.Response; }
      status = (int)response.StatusCode;
      using (response) using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
    }

    private static Dictionary<string, object> Post(string path, Dictionary<string, string> fields, string fileField = null, byte[] file = null, string fileName = "photo.png") {
      string boundary = "----tree" + Guid.NewGuid().ToString("N");
      using (MemoryStream body = new MemoryStream()) {
        Action<string> write = chunk => { byte[] bytes = Encoding.UTF8.GetBytes(chunk); body.Write(bytes, 0, bytes.Length); };
        foreach (var field in fields) write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + field.Key + "\"\r\n\r\n" + field.Value + "\r\n");
        if (fileField != null) { write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + fileField + "\"; filename=\"" + fileName + "\"\r\nContent-Type: image/png\r\n\r\n"); body.Write(file, 0, file.Length); write("\r\n"); }
        write("--" + boundary + "--\r\n");
        int status;
        string text = Http("POST", path, body.ToArray(), "multipart/form-data; boundary=" + boundary, out status);
        try { return (Dictionary<string, object>)Json.DeserializeObject(text); } catch { return new Dictionary<string, object> { { "ok", false }, { "message", "HTTP " + status + ": " + text } }; }
      }
    }

    private static string treeId;
    private static Dictionary<string, object> Act(string action, params string[] pairs) {
      Dictionary<string, string> fields = new Dictionary<string, string> { { "action", action }, { "tree_long_id", treeId } };
      for (int i = 0; i < pairs.Length; i += 2) fields[pairs[i]] = pairs[i + 1];
      return Post("/includes/tree_save.include.php", fields);
    }
    private static bool Ok(Dictionary<string, object> result, string label) {
      bool ok = result != null && true.Equals(result["ok"]);
      Check(ok, label + (ok ? "" : ": " + (result == null ? "null" : Convert.ToString(result["message"]))));
      return ok;
    }
    private static int PersonId(string html, string name) {
      Match match = Regex.Match(html ?? "", "title='" + Regex.Escape(name) + "' data-person-id='(\\d+)'");
      return match.Success ? Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }
    private static byte[] Png(Color color) {
      using (Bitmap bitmap = new Bitmap(300, 200)) using (Graphics g = Graphics.FromImage(bitmap)) using (MemoryStream stream = new MemoryStream()) {
        g.Clear(color); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
      }
    }

    private static void CheckApi() {
      LocalStore store = StartIsolated("");
      int status;
      string page = Http("GET", "/family", null, null, out status);
      Check(status == 200 && page.Contains("id=\"tree_container\"") && page.Contains("addFirstMemberBtn"), "tree page renders an empty first tree");
      treeId = Regex.Match(page, "data-tree-id=\"([^\"]+)\"").Groups[1].Value;
      Check(treeId != "", "tree page names the selected tree");
      foreach (string asset in new[] { "family_tree_script.js", "tree.css", "tree_adapter.js", "default_male.webp" }) { Http("GET", "/tree-assets/" + asset, null, null, out status); Check(status == 200, "asset " + asset); }

      Dictionary<string, object> result = Act("add_first_member", "first_name", "Adam", "last_name", "Root", "gender", "M", "birth_date", "1950-04-02");
      if (!Ok(result, "add_first_member")) return;
      int adam = PersonId((string)result["tree_html"], "Adam Root");
      result = Act("add_spouse", "anchor_person_id", adam.ToString(), "person_source", "new", "person_first_name", "Eve", "person_last_name", "Root", "family_sibling_person", "anchor");
      Ok(result, "add_spouse");
      int eve = PersonId((string)result["tree_html"], "Eve Root");
      Ok(Act("add_child", "anchor_person_id", adam.ToString(), "person_source", "new", "person_first_name", "Cain", "person_gender", "M"), "add_child without position");
      Ok(Act("add_child", "anchor_person_id", adam.ToString(), "person_source", "new", "person_first_name", "Abel", "person_gender", "M", "sibling_birth_order", "1"), "add_child at position 1");
      result = Act("add_child", "anchor_person_id", adam.ToString(), "person_source", "new", "person_first_name", "Seth", "person_gender", "M", "sibling_birth_order", "3");
      Ok(result, "add_child at position 3");
      string html = (string)result["tree_html"];
      int cain = PersonId(html, "Cain"), abel = PersonId(html, "Abel"), seth = PersonId(html, "Seth");
      Check(html.IndexOf("title='Abel'", StringComparison.Ordinal) < html.IndexOf("title='Cain'", StringComparison.Ordinal) && html.IndexOf("title='Cain'", StringComparison.Ordinal) < html.IndexOf("title='Seth'", StringComparison.Ordinal), "birth positions are slots: Abel 1, Cain fills 2, Seth 3");
      result = Act("list_sibling_orders", "person_id", cain.ToString());
      Ok(result, "list_sibling_orders");
      Check(Json.Serialize(result["siblings"]).Contains("\"order\":1") && Json.Serialize(result["siblings"]).Contains("\"order\":3"), "sibling orders listed");
      result = Act("update_person", "person_id", cain.ToString(), "person_first_name", "Cain", "person_gender", "M", "sibling_birth_order", "1");
      Ok(result, "update_person takes a held position");
      html = (string)result["tree_html"];
      Check(html.IndexOf("title='Cain'", StringComparison.Ordinal) < html.IndexOf("title='Abel'", StringComparison.Ordinal), "a taken position hands the holder the previous one");
      result = Act("add_partner", "anchor_person_id", adam.ToString(), "person_source", "new", "person_first_name", "Lilith");
      Ok(result, "add_partner");
      int lilith = PersonId((string)result["tree_html"], "Lilith");
      result = Act("add_child", "anchor_person_id", lilith.ToString(), "second_parent_id", adam.ToString(), "person_source", "new", "person_first_name", "Enoch", "person_gender", "M");
      Ok(result, "add_child to an external partner");
      Check(((string)result["tree_html"]).Contains("external_partner husband") || ((string)result["tree_html"]).Contains("external_partner wife"), "external partner branch rendered");
      int enoch = PersonId((string)result["tree_html"], "Enoch");
      result = Act("get_person", "person_id", adam.ToString());
      Ok(result, "get_person");
      string person = Json.Serialize(result["person"]);
      Check(person.Contains("\"spouses\":[{\"id\":" + eve) && person.Contains("\"partners\":[{\"id\":" + lilith), "get_person lists spouse and partner pills");
      result = Act("list_tree_couples", "source_relationship_id", "0", "person_id", enoch.ToString());
      Ok(result, "list_tree_couples");
      string couplesJson = Json.Serialize(result["couples"]);
      int mainUnion = Int32.Parse(Regex.Match(couplesJson, "\"id\":(\\d+),\"label\":\"Adam Root \\+ Eve Root\"").Groups[1].Value, CultureInfo.InvariantCulture);
      int partnerUnion = Int32.Parse(Regex.Match(couplesJson, "\"id\":(\\d+),\"label\":\"Adam Root \\+ Lilith\"").Groups[1].Value, CultureInfo.InvariantCulture);
      string mainPair = String.Join(",", new[] { adam, eve }.OrderBy(i => i)), partnerPair = String.Join(",", new[] { adam, lilith }.OrderBy(i => i));
      result = Act("move_child", "person_id", enoch.ToString(), "expected_parent_ids", partnerPair, "source_person_ids", partnerPair, "source_relationship_id", partnerUnion.ToString(), "relationship_id", mainUnion.ToString(), "target_person_ids", mainPair);
      Ok(result, "move_child to the main couple");
      Ok(Act("promote_partner", "anchor_person_id", adam.ToString(), "source_relationship_id", mainUnion.ToString(), "source_person_ids", mainPair, "relationship_id", partnerUnion.ToString(), "target_person_ids", partnerPair), "promote_partner");
      result = Act("add_parents", "child_id", adam.ToString(), "parent_role", "father", "person_source", "new", "parent_first_name", "Old", "parent_last_name", "Root");
      Ok(result, "add_parents");
      Ok(Act("hide_person", "person_id", seth.ToString(), "visibility", "hidden"), "hide_person");
      result = Act("toggle_life_status", "person_id", seth.ToString());
      Check(Ok(result, "toggle_life_status") && Json.Serialize(result["person_patch"]).Contains("\"is_deceased\":true"), "mark dead with unknown date");
      Ok(Act("set_sibling_status", "person_id", eve.ToString(), "sibling_value", "yes"), "set_sibling_status");

      // Media: profile photo upload, serve, remove (kept in the private gallery), gallery upload, file streaming.
      result = Post("/includes/tree_save.include.php", new Dictionary<string, string> { { "action", "update_person" }, { "tree_long_id", treeId }, { "person_id", eve.ToString() }, { "person_first_name", "Eve" }, { "person_last_name", "Root" }, { "person_gender", "F" } }, "person_image", Png(Color.SteelBlue));
      Ok(result, "profile photo upload");
      Match photo = Regex.Match((string)result["tree_html"], "<img src='(/media/images/persons/[^']+)'");
      Http("GET", photo.Groups[1].Value, null, null, out status);
      Check(photo.Success && status == 200, "profile photo served from LocalAppData media");
      result = Act("get_person", "person_id", eve.ToString());
      Dictionary<string, object> evePerson = (Dictionary<string, object>)result["person"];
      Ok(Post("/includes/persons.include.php", new Dictionary<string, string> { { "action", "remove_photo" }, { "person", (string)evePerson["long_id"] }, { "expected_image", (string)evePerson["image_name"] } }), "remove profile photo");
      result = Post("/includes/person_gallery.include.php", new Dictionary<string, string> { { "action", "upload" }, { "person", (string)evePerson["long_id"] } }, "media_files[]", Png(Color.DarkOrange), "garden.png");
      Ok(result, "gallery upload");
      result = Post("/includes/person_gallery.include.php?action=list&person=" + evePerson["long_id"], new Dictionary<string, string>());
      Check(Ok(result, "gallery list") && Convert.ToInt32(result["count"]) == 2, "gallery holds the upload and the retained profile photo");
      string thumb = (string)((Dictionary<string, object>)((object[])result["items"])[0])["thumb_url"];
      Http("GET", thumb, null, null, out status);
      Check(status == 200, "gallery thumbnail streamed");

      // Partials, statistics, important dates.
      result = Act("create_partial_tree", "relationship_id", mainUnion.ToString(), "partial_kind", "temporary");
      Ok(result, "temporary partial");
      Http("GET", (string)result["url"], null, null, out status);
      Check(status == 200, "temporary partial page");
      result = Act("create_partial_tree", "relationship_id", mainUnion.ToString(), "partial_kind", "permanent", "name", "Adam branch");
      Ok(result, "permanent partial");
      string partialId = (string)result["long_id"];
      result = Post("/includes/tree_save.include.php", new Dictionary<string, string> { { "action", "get_tree_state" }, { "tree_long_id", treeId }, { "partial_id", partialId } });
      Check(Ok(result, "partial tree state") && ((string)result["tree_html"]).Contains("Cain"), "partial renders the anchored couple");
      Ok(Act("update_partial_tree", "partial_id", partialId, "name", "Adam family"), "rename partial");
      Ok(Act("tree_insights"), "tree_insights");
      Ok(Act("tree_important_dates"), "tree_important_dates");
      result = Act("list_stat_persons", "category", "men");
      Check(Ok(result, "list_stat_persons") && Json.Serialize(result["people"]).Contains("Adam Root"), "stat person list");
      result = Act("partial_package", "relationship_id", mainUnion.ToString(), "photos", "1", "gallery", "1");
      Ok(result, "partial_package");
      string package = (string)result["package"];
      result = Post("/includes/tree_save.include.php", new Dictionary<string, string> { { "action", "create_tree" }, { "name", "Second tree" } });
      Ok(result, "create second tree");
      string firstTree = treeId; treeId = (string)result["tree_long_id"];
      result = Act("paste_partial_preview", "package", package);
      Check(Ok(result, "paste preview") && ((object[])result["conflicts"]).Length > 0, "paste preview lists duplicate names");
      result = Act("paste_partial", "package", package, "paste_mode", "add_first_member", "resolutions", "{}");
      Check(Ok(result, "paste into empty tree") && ((string)result["tree_html"]).Contains("Cain"), "pasted Partial renders");
      result = Act("paste_partial", "package", package.Replace("Adam", "Adan"), "paste_mode", "add_first_member");
      Check(result != null && false.Equals(result["ok"]), "a changed Partial is rejected");
      Check(false.Equals(Post("/includes/tree_save.include.php", new Dictionary<string, string> { { "action", "create_tree" }, { "name", "Third" } })["ok"]), "Free keeps the 2-tree cap");
      treeId = firstTree;
      Ok(Act("remove_person_from_tree", "person_id", seth.ToString(), "remove_mode", "relationships"), "remove_person_from_tree");
      Ok(Act("rename_tree", "name", "Root family"), "rename_tree");

      // 500+ people: three generations under one couple, then render it.
      using (SqliteDbHandle db = new SqliteDbHandle(store)) {
        int tree = Int32.Parse(db.Scalar("SELECT id FROM family_trees WHERE long_id=?", treeId), CultureInfo.InvariantCulture);
        List<int> parents = new List<int> { cain };
        int added = 0;
        db.Exec("BEGIN");
        for (int generation = 0; generation < 3 && added < 620; generation++) {
          List<int> next = new List<int>();
          foreach (int parent in parents) {
            db.Execute("INSERT INTO persons(long_id,first_name,last_name,gender,created_at,updated_at)VALUES(?,?,?,'F','x','x')", "bulk" + added + "s", "Spouse" + added, "Bulk"); int spouse = db.LastId(); added++;
            db.Execute("INSERT INTO relationships(family_tree_id,person1_id,person2_id,relationship_type,sibling_person_id,created_at)VALUES(?,?,?,'spouse',?,'x')", tree, parent, spouse, parent);
            for (int c = 0; c < 8 && added < 620; c++) {
              db.Execute("INSERT INTO persons(long_id,first_name,last_name,gender,created_at,updated_at)VALUES(?,?,?,'M','x','x')", "bulk" + added, "Child" + added, "Bulk"); int child = db.LastId(); added++;
              db.Execute("INSERT INTO parent_child(family_tree_id,child_id,parent_id,created_at)VALUES(?,?,?,'x')", tree, child, parent);
              db.Execute("INSERT INTO parent_child(family_tree_id,child_id,parent_id,created_at)VALUES(?,?,?,'x')", tree, child, spouse);
              next.Add(child);
            }
          }
          parents = next;
        }
        db.Exec("COMMIT");
      }
      Stopwatch watch = Stopwatch.StartNew();
      result = Act("get_tree_state");
      watch.Stop();
      int cards = Regex.Matches(Ok(result, "large tree state") ? (string)result["tree_html"] : "", "class='one_person").Count;
      Check(cards > 500, "large tree renders 500+ people (" + cards + ")");
      page = Http("GET", "/family", null, null, out status);
      Check(page.Contains("data-tree-deferred=\"true\""), "large tree page defers rendering like hosted");
      Console.WriteLine("Tree API: " + cards + " cards rendered in " + watch.ElapsedMilliseconds + " ms.");
    }

    // Small wrapper so the check can seed rows through the same SQLite engine the app uses.
    private sealed class SqliteDbHandle : IDisposable {
      private readonly SqliteDb db;
      internal SqliteDbHandle(LocalStore store) { db = new SqliteDb(Path.Combine(PortablePaths.DataDir, "racinage-free.sqlite")); }
      internal void Exec(string sql) { db.Exec(sql); }
      internal void Execute(string sql, params object[] args) { db.Execute(sql, args); }
      internal string Scalar(string sql, params object[] args) { return Convert.ToString(db.Scalar(sql, args), CultureInfo.InvariantCulture); }
      internal int LastId() { return Int32.Parse(Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture); }
      public void Dispose() { db.Dispose(); }
    }
  }
}

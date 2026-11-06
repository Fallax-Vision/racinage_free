using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// Local port of the hosted tree endpoints (includes/tree_save.include.php, person_gallery.include.php,
// persons.include.php remove_photo) and the trees page, for the vendored hosted tree JS. Same request
// fields and JSON shapes; one local user and family, so hosted permissions are all granted and
// collaboration, public sharing, live sync, collections and AI are left out.
namespace RacinageFreeDesktop {
  internal sealed class TreeUpload { internal string Name = "", FileName = "", ContentType = ""; internal byte[] Data = new byte[0]; }

  internal sealed class TreeRequest {
    internal readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
    internal readonly Dictionary<string, List<TreeUpload> > Files = new Dictionary<string, List<TreeUpload> >(StringComparer.Ordinal);
    internal string Method = "GET";
    internal string this[string key] { get { string value; return Fields.TryGetValue(key, out value) && value != null ? value : ""; } }
    internal bool Has(string key) { return Fields.ContainsKey(key); }
    internal int Int(string key) { int value; return Int32.TryParse(this[key].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0; }
    internal TreeUpload File(string name) { List<TreeUpload> list; return Files.TryGetValue(name, out list) && list.Count > 0 && list[0].Data.Length > 0 ? list[0] : null; }
  }

  internal sealed class TreeError : Exception {
    internal readonly int Status; internal readonly string Code;
    internal TreeError(string message, int status = 200, string code = "") : base(message) { Status = status; Code = code; }
  }

  internal static class TreeText {
    private static Dictionary<string, string> strings;
    internal static readonly bool French = String.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase);
    internal static string Lang { get { return French ? "fr" : "en"; } }
    internal static Dictionary<string, string> All {
      get {
        if (strings == null) {
          Dictionary<string, string> loaded = new Dictionary<string, string>(StringComparer.Ordinal);
          try {
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tree", "i18n.json"), Encoding.UTF8));
            foreach (var pair in (Dictionary<string, object>)root[Lang]) loaded[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
          } catch (Exception error) { Program.Log("Tree strings unavailable: " + error.Message); }
          strings = loaded;
        }
        return strings;
      }
    }
    internal static string T(string key) { string value; return All.TryGetValue(key, out value) ? value : key; }
    internal static string H(string key) { return FamilyTreeRenderer.H(T(key)); }
  }

  internal sealed partial class LocalServer {
    // Returns true when the request belongs to the tree. GET /family is the tree page; legacy POST /family stays.
    private bool TreeRoute(HttpListenerContext context, string path) {
      if (path.StartsWith("/tree-assets/", StringComparison.Ordinal)) {
        string name = path.Substring(13);
        if (!Regex.IsMatch(name, @"^[a-z_]+\.(js|css|webp)$")) { context.Response.StatusCode = 404; context.Response.Close(); return true; }
        string type = name.EndsWith(".js", StringComparison.Ordinal) ? "application/javascript; charset=utf-8" : name.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "image/webp";
        WriteFile(context, "tree\\" + name, type);
        return true;
      }
      if (path == "/media/images/default_male.webp" || path == "/media/images/default_female.webp") { WriteFile(context, "tree\\" + path.Substring(14), "image/webp"); return true; }
      bool page = (path == "/family" || path == "/trees" || path == "/persons") && context.Request.HttpMethod == "GET";
      bool api = path == "/includes/tree_save.include.php" || path == "/includes/person_gallery.include.php" || path == "/includes/persons.include.php";
      bool photo = path.StartsWith("/media/images/persons/", StringComparison.Ordinal);
      if (!page && !api && !photo) return false;
      if (!IsAuthenticated(context)) {
        if (page) Redirect(context, "/login");
        else if (api) WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", false }, { "message", TreeText.T("Please log in first.") } }), 401);
        else { context.Response.StatusCode = 404; context.Response.Close(); }
        return true;
      }
      if (photo) { TreePhoto(context, path.Substring(22)); return true; }
      if (page) {
        if (context.Request.QueryString["tree_picker"] == "1") { WriteHtml(context, store.TreePickerHtml(context.Request.QueryString["tree"] ?? "", context.Request.QueryString["partial"] ?? "", context.Request.QueryString["partial_temp"] ?? "", context.Request.QueryString["tree_picker_query"] ?? "")); return true; }
        WriteHtml(context, path == "/persons" ? TreeShell(TreeText.T("Manage Persons"), store.PersonsPageHtml(context.Request.QueryString["q"] ?? "", context.Request.QueryString["view"] ?? "", context.Request.QueryString["page"] ?? "", context.Request.QueryString["tree"] ?? ""), "free_persons_body") : TreePage(context));
        return true;
      }
      TreeRequest request;
      try { request = ReadTreeRequest(context); } catch (TreeError error) { WriteJson(context, json.Serialize(new Dictionary<string, object> { { "ok", false }, { "message", TreeText.T(error.Message) } }), error.Status); return true; }
      if (path == "/includes/person_gallery.include.php" && request["action"] == "file") { store.TreeGalleryFile(context, request["media"], request["variant"] == "thumb"); return true; }
      Dictionary<string, object> result; int status = 200;
      try {
        string csrf = context.Request.Headers["X-Racinage-CSRF"] ?? "";
        if (csrf == "") csrf = request["tree_preference_csrf"] != "" ? request["tree_preference_csrf"] : request["family_switch_csrf"] != "" ? request["family_switch_csrf"] : request["csrf"];
        if (path == "/includes/tree_save.include.php") result = store.TreeAction(request, csrf);
        else if (path == "/includes/person_gallery.include.php") result = store.TreeGalleryAction(request, csrf);
        else result = store.TreeRemovePhoto(request, csrf);
      } catch (TreeError error) {
        status = error.Status;
        result = new Dictionary<string, object> { { "ok", false }, { "message", TreeText.T(error.Message) } };
        if (error.Code != "") result["error_code"] = error.Code;
      } catch (Exception error) {
        Program.Log("Tree action failed: " + error);
        status = 400;
        result = new Dictionary<string, object> { { "ok", false }, { "message", TreeText.T("Could not update the tree.") } };
      }
      WriteJson(context, json.Serialize(result), status);
      return true;
    }

    private void TreePhoto(HttpListenerContext context, string name) {
      string path = store.TreePhotoPath(Uri.UnescapeDataString(name));
      if (path == "") { context.Response.StatusCode = 404; context.Response.Close(); return; }
      context.Response.ContentType = path.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp" : "image/jpeg";
      context.Response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
      byte[] bytes = File.ReadAllBytes(path);
      context.Response.ContentLength64 = bytes.Length;
      context.Response.OutputStream.Write(bytes, 0, bytes.Length);
      context.Response.Close();
    }

    private TreeRequest ReadTreeRequest(HttpListenerContext context) {
      TreeRequest request = new TreeRequest { Method = context.Request.HttpMethod };
      foreach (string key in context.Request.QueryString.AllKeys) if (key != null) request.Fields[key] = context.Request.QueryString[key];
      if (request.Method != "POST") return request;
      long length = context.Request.ContentLength64;
      if (length > 520L * 1024L * 1024L) throw new TreeError("The upload is too large.", 413);
      byte[] body;
      using (MemoryStream buffer = new MemoryStream()) {
        byte[] chunk = new byte[81920]; int read; long total = 0;
        while ((read = context.Request.InputStream.Read(chunk, 0, chunk.Length)) > 0) {
          total += read;
          if (total > 520L * 1024L * 1024L) throw new TreeError("The upload is too large.", 413);
          buffer.Write(chunk, 0, read);
        }
        body = buffer.ToArray();
      }
      string type = (context.Request.ContentType ?? "").ToLowerInvariant();
      if (type.StartsWith("multipart/form-data", StringComparison.Ordinal)) {
        Match boundary = Regex.Match(context.Request.ContentType, "boundary=\"?([^\";]+)\"?", RegexOptions.IgnoreCase);
        if (!boundary.Success) throw new TreeError("Invalid request.", 400);
        ParseMultipart(body, boundary.Groups[1].Value, request);
      } else if (type.Contains("application/json")) {
        object parsed = json.DeserializeObject(Encoding.UTF8.GetString(body));
        Dictionary<string, object> values = parsed as Dictionary<string, object>;
        if (values != null) foreach (var pair in values) request.Fields[pair.Key] = pair.Value is string || pair.Value == null ? (string)pair.Value ?? "" : json.Serialize(pair.Value);
      } else {
        foreach (string pair in Encoding.UTF8.GetString(body).Split('&')) {
          if (pair == "") continue;
          int equals = pair.IndexOf('=');
          request.Fields[UrlDecode(equals >= 0 ? pair.Substring(0, equals) : pair)] = equals >= 0 ? UrlDecode(pair.Substring(equals + 1)) : "";
        }
      }
      return request;
    }

    private static void ParseMultipart(byte[] body, string boundary, TreeRequest request) {
      byte[] delimiter = Encoding.ASCII.GetBytes("--" + boundary), headerEnd = { 13, 10, 13, 10 };
      int position = IndexOf(body, delimiter, 0);
      while (position >= 0) {
        int start = position + delimiter.Length;
        if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-') break;
        start += 2;
        int split = IndexOf(body, headerEnd, start);
        if (split < 0) break;
        string headers = Encoding.UTF8.GetString(body, start, split - start);
        int next = IndexOf(body, delimiter, split + 4);
        if (next < 0) break;
        int length = Math.Max(0, next - 2 - (split + 4));
        string name = Regex.Match(headers, "name=\"([^\"]*)\"").Groups[1].Value;
        if (name.EndsWith("[]", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
        Match file = Regex.Match(headers, "filename=\"([^\"]*)\"");
        if (file.Success) {
          byte[] data = new byte[length];
          Buffer.BlockCopy(body, split + 4, data, 0, length);
          List<TreeUpload> list;
          if (!request.Files.TryGetValue(name, out list)) request.Files[name] = list = new List<TreeUpload>();
          list.Add(new TreeUpload { Name = name, FileName = file.Groups[1].Value, ContentType = Regex.Match(headers, @"Content-Type:\s*([^\r\n]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim(), Data = data });
        } else if (name != "") {
          request.Fields[name] = Encoding.UTF8.GetString(body, split + 4, length);
        }
        position = next;
      }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from) {
      for (int i = Math.Max(0, from); i <= haystack.Length - needle.Length; i++) {
        int j = 0;
        while (j < needle.Length && haystack[i + j] == needle[j]) j++;
        if (j == needle.Length) return i;
      }
      return -1;
    }

    private string TreePage(HttpListenerContext context) {
      return TreeShell(TreeText.T("Family Tree"), store.TreePageHtml(context.Request.QueryString["tree"] ?? "", context.Request.QueryString["partial"] ?? "", context.Request.QueryString["partial_temp"] ?? "", context.Request.QueryString["p"] ?? ""), "");
    }

    private string TreeShell(string title, string body, string bodyClass) {
      string v = Uri.EscapeDataString(PortablePaths.Version);
      return "<!doctype html><html lang='" + TreeText.Lang + "'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'><meta name='app-root' content='/'>" +
        "<title>" + FamilyTreeRenderer.H(title) + " - Racinage Free</title><style>" + Css() + "</style>" +
        "<link rel='stylesheet' href='/tree-assets/tree.css?v=" + v + "'><link rel='stylesheet' href='/tree-assets/tree_adapter.css?v=" + v + "'></head><body class='racinage_free_tree " + bodyClass + "'>" +
        "<header id='header'><a class='brand' href='/'>Racinage Free</a><nav><a href='/'>Home</a><a href='/family'>Dashboard</a><a href='/messages'>Messages</a><a href='/calendar'>Calendar</a><a href='/manage'>Manage</a><a href='" + PortablePaths.PricingUrl + "'>Upgrade</a></nav></header>" +
        body +
        "<script>window.ROOT='';</script><script src='/tree-assets/tree_adapter.js?v=" + v + "'></script><script src='/tree-assets/profile_crop.js?v=" + v + "'></script><script src='/tree-assets/person_profile_photo.js?v=" + v + "'></script>" +
        "<script src='/tree-assets/tree_loading.js?v=" + v + "'></script><script src='/tree-assets/family_tree_script.js?v=" + v + "'></script></body></html>";
    }
  }

  internal sealed partial class LocalStore {
    private const int TreeLimit = 2; // Lite plan: two family trees.
    private static readonly string[] UnknownNames = { "unknown person", "unknown", "personne inconnue", "inconnu", "inconnue" };
    private static readonly string[] PersonFields = { "first_name", "last_name", "birth_date", "death_date", "is_deceased", "gender", "phone1", "phone2", "email1", "email2", "address", "religion", "website", "birth_place", "nationality", "blood_group", "marital_status", "education_level" };
    private static string PersonsDir { get { return Path.Combine(PortablePaths.MediaDir, "persons"); } }
    private static string GalleryDir { get { return Path.Combine(PortablePaths.MediaDir, "person-gallery"); } }
    private static string T(string key) { return TreeText.T(key); }
    private static string Q(Dictionary<string, string> row, string key) { return FamilyTreeGraph.S(row, key); }
    private static int QI(Dictionary<string, string> row, string key) { return FamilyTreeGraph.I(row, key); }
    private static string N(long value) { return value.ToString(CultureInfo.InvariantCulture); }
    private static string E(string value) { return FamilyTreeRenderer.H(value); }
    private static Dictionary<string, object> Ok(string message, Dictionary<string, object> extra = null) {
      Dictionary<string, object> result = new Dictionary<string, object> { { "ok", true }, { "message", T(message) } };
      if (extra != null) foreach (var pair in extra) result[pair.Key] = pair.Value;
      return result;
    }
    private static TreeError Fail(string message, int status = 200, string code = "") { return new TreeError(message, status, code); }
    private static long LastId(SqliteDb db) { return Convert.ToInt64(db.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture); }
    private static string NewId(int bytes) { byte[] raw = new byte[bytes]; using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw); return BitConverter.ToString(raw).Replace("-", "").ToLowerInvariant(); }

    // ---------- media ----------
    internal string TreePhotoPath(string name) {
      if (!Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,80}\.(jpg|webp)$")) return "";
      string path = Path.Combine(PersonsDir, name);
      return File.Exists(path) ? path : "";
    }
    private static string PhotoBase(string image) { return Path.GetFileNameWithoutExtension(Path.GetFileName(image ?? "")); }
    private static string ExistingPhotoFile(string image, string suffix) {
      string baseName = PhotoBase(image);
      if (!Regex.IsMatch(baseName, @"^[A-Za-z0-9_-]{1,80}$")) return "";
      foreach (string candidate in new[] { baseName + suffix + ".jpg", baseName + suffix + ".webp", baseName + ".jpg", baseName + ".webp" })
        if (File.Exists(Path.Combine(PersonsDir, candidate))) return candidate;
      return "";
    }
    internal static string DefaultImageUrl(string gender) { string g = (gender ?? "").ToLowerInvariant(); return "/media/images/" + (g == "f" || g == "female" ? "default_female.webp" : "default_male.webp"); }
    internal static string PersonImageUrl(string image, string gender, string size) {
      string file = ExistingPhotoFile(image, size == "lg" ? "_lg" : size == "sm" ? "_sm" : "");
      return file == "" ? DefaultImageUrl(gender) : "/media/images/persons/" + Uri.EscapeDataString(file);
    }

    // Square crop like the hosted GD resize; JPEG because .NET Framework has no WebP encoder.
    private static string StorePersonPhoto(TreeUpload upload) {
      if (upload == null) return "";
      if (upload.Data.Length > 5 * 1024 * 1024) throw Fail("Profile image is too large. Use an image under 5 MB.");
      string photoId = NewId(10);
      Directory.CreateDirectory(PersonsDir);
      try {
        using (MemoryStream input = new MemoryStream(upload.Data))
        using (Image source = Image.FromStream(input, true, true)) {
          if (source.Width < 1 || source.Height < 1 || source.Width > 12000 || source.Height > 12000 || (long)source.Width * source.Height > 40000000L) throw Fail("The profile image dimensions are too large.");
          foreach (var size in new[] { new { Suffix = "", Px = 200 }, new { Suffix = "_sm", Px = 80 }, new { Suffix = "_lg", Px = 640 } })
            SaveJpeg(source, Path.Combine(PersonsDir, photoId + size.Suffix + ".jpg"), size.Px, size.Px, true);
        }
      } catch (TreeError) { throw; } catch { throw Fail("Use a JPEG, PNG, or WebP profile image."); }
      return photoId;
    }

    private static void SaveJpeg(Image source, string path, int maxWidth, int maxHeight, bool square) {
      int sx = 0, sy = 0, sw = source.Width, sh = source.Height, width, height;
      if (square) { int dim = Math.Min(sw, sh); sx = (sw - dim) / 2; sy = (sh - dim) / 2; sw = sh = dim; width = maxWidth; height = maxHeight; }
      else { double ratio = Math.Min(1.0, Math.Min((double)maxWidth / sw, (double)maxHeight / sh)); width = Math.Max(1, (int)Math.Round(sw * ratio)); height = Math.Max(1, (int)Math.Round(sh * ratio)); }
      using (Bitmap canvas = new Bitmap(width, height))
      using (Graphics g = Graphics.FromImage(canvas)) {
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.HighQuality; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (ImageAttributes wrap = new ImageAttributes()) { wrap.SetWrapMode(WrapMode.TileFlipXY); g.DrawImage(source, new Rectangle(0, 0, width, height), sx, sy, sw, sh, GraphicsUnit.Pixel, wrap); }
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using (EncoderParameters parameters = new EncoderParameters(1)) { parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L); canvas.Save(path, codec, parameters); }
      }
    }

    private static void DeletePhotoFiles(string image) {
      string baseName = PhotoBase(image);
      if (!Regex.IsMatch(baseName, @"^[A-Za-z0-9_-]{1,80}$")) return;
      foreach (string suffix in new[] { "", "_sm", "_lg" }) foreach (string ext in new[] { ".jpg", ".webp" }) {
        string path = Path.Combine(PersonsDir, baseName + suffix + ext);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
      }
    }

    // Hosted keeps a replaced profile photo in the private gallery (person_gallery_retain_profile).
    private static void RetainProfile(SqliteDb db, int personId, string image) {
      string baseName = PhotoBase(image);
      if (baseName == "" || ExistingPhotoFile(baseName, "") == "") return;
      string fileName = "profile_" + baseName;
      if (db.QueryOne("SELECT id FROM person_gallery_media WHERE person_id=? AND file_name=? AND status='active'", personId, fileName) != null) return;
      long size = new[] { "", "_sm", "_lg" }.Select(s => ExistingPhotoFile(baseName, s)).Distinct().Where(f => f != "").Sum(f => new FileInfo(Path.Combine(PersonsDir, f)).Length);
      db.Execute("INSERT INTO person_gallery_media(long_id,person_id,media_type,file_name,original_name,mime_type,file_size,created_at,updated_at)VALUES(?,?,'image',?,?,'image/jpeg',?,?,?)", NewId(16), personId, fileName, baseName + ".jpg", size, Now(), Now());
    }

    private string ApplyPersonUpload(SqliteDb db, int personId, TreeUpload upload) {
      string photoId = StorePersonPhoto(upload);
      if (photoId == "") return "";
      Dictionary<string, string> row = db.QueryOne("SELECT image_name FROM persons WHERE id=?", personId);
      RetainProfile(db, personId, Q(row, "image_name"));
      db.Execute("UPDATE persons SET image_name=?,updated_at=? WHERE id=?", photoId, Now(), personId);
      return photoId;
    }

    // ---------- shared reads ----------
    private static Dictionary<string, string> TreeByLongId(SqliteDb db, string longId) {
      return longId == "" ? null : db.QueryOne("SELECT * FROM family_trees WHERE long_id=? AND status='active'", longId.Trim());
    }
    private static Dictionary<string, string> RequireTree(SqliteDb db, TreeRequest r) {
      Dictionary<string, string> tree = TreeByLongId(db, r["tree_long_id"]);
      if (tree == null) throw Fail("Family tree not found.", 200, "tree_not_found");
      return tree;
    }
    private const string MemberSql = "SELECT root_person_id AS person_id FROM family_trees WHERE id=?1 AND root_person_id IS NOT NULL UNION SELECT child_id FROM parent_child WHERE family_tree_id=?1 UNION SELECT parent_id FROM parent_child WHERE family_tree_id=?1 UNION SELECT person1_id FROM relationships WHERE family_tree_id=?1 UNION SELECT person2_id FROM relationships WHERE family_tree_id=?1";
    private static bool InTree(SqliteDb db, int treeId, int personId) {
      return personId > 0 && db.QueryOne("SELECT 1 AS x FROM (" + MemberSql + ") m WHERE m.person_id=?2", treeId, personId) != null;
    }
    private static HashSet<int> TreeMemberIds(SqliteDb db, int treeId) {
      return new HashSet<int>(db.Query("SELECT person_id FROM (" + MemberSql + ")", treeId).Select(row => QI(row, "person_id")));
    }
    private static bool PersonExists(SqliteDb db, int id) { return id > 0 && db.QueryOne("SELECT id FROM persons WHERE id=?", id) != null; }
    private static string Gender(SqliteDb db, int id) { return Q(db.QueryOne("SELECT gender FROM persons WHERE id=?", id), "gender"); }

    private static FamilyTreeGraph LoadGraph(SqliteDb db, Dictionary<string, string> tree) {
      int id = QI(tree, "id");
      return new FamilyTreeGraph(
        db.Query("SELECT p.id,p.long_id,p.first_name,p.last_name,p.gender,p.image_name,p.birth_date,p.death_date,p.is_deceased,p.visibility,s.sibling_birth_order,s.preferred_relationship_id FROM persons p JOIN (" + MemberSql + ") m ON m.person_id=p.id LEFT JOIN family_tree_person_settings s ON s.family_tree_id=?1 AND s.person_id=p.id ORDER BY p.id", id),
        db.Query("SELECT id,person1_id,person2_id,relationship_type,is_current,sibling_person_id FROM relationships WHERE family_tree_id=? ORDER BY id", id),
        db.Query("SELECT id,parent_id,child_id FROM parent_child WHERE family_tree_id=? ORDER BY id", id),
        QI(tree, "primary_relationship_id"));
    }
    private static FamilyTreeRenderer Renderer(FamilyTreeGraph graph, bool canEdit, int partialRelationshipId) {
      return new FamilyTreeRenderer(graph, canEdit, partialRelationshipId, TreeText.Lang, DateTime.Today, (image, gender) => PersonImageUrl(image, gender, "sm"));
    }

    // ---------- partial trees ----------
    private string PartialKey { get { return GetOrCreateProtectedToken("tree-partial.key"); } }
    private string Hmac(string purpose, string payload) {
      using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(PartialKey + ":" + purpose))) return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "").ToLowerInvariant();
    }
    private string TempToken(int treeId, int relationshipId) {
      string payload = N(treeId) + "." + N(relationshipId);
      return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + "." + Hmac("temp", payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private int[] TempDecode(string token) {
      if (token == "" || token.Length > 220) return null;
      try {
        string encoded = token.Replace('-', '+').Replace('_', '/'); encoded += new string('=', (4 - encoded.Length % 4) % 4);
        string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('.');
        int tree, relationship;
        if (parts.Length != 3 || !Int32.TryParse(parts[0], out tree) || !Int32.TryParse(parts[1], out relationship)) return null;
        return FixedEquals(Hmac("temp", parts[0] + "." + parts[1]), parts[2]) ? new[] { tree, relationship } : null;
      } catch { return null; }
    }
    private static Dictionary<string, string> PartialAnchor(SqliteDb db, int treeId, int relationshipId) {
      return db.QueryOne("SELECT r.id,r.family_tree_id,r.person1_id,r.person2_id,r.relationship_type,r.sibling_person_id,ft.long_id AS tree_long_id,ft.name AS tree_name,trim(coalesce(p1.first_name,'')||' '||coalesce(p1.last_name,'')) AS person1_name,trim(coalesce(p2.first_name,'')||' '||coalesce(p2.last_name,'')) AS person2_name FROM relationships r JOIN family_trees ft ON ft.id=r.family_tree_id JOIN persons p1 ON p1.id=r.person1_id JOIN persons p2 ON p2.id=r.person2_id WHERE r.id=? AND r.family_tree_id=? AND ft.status='active' AND r.relationship_type IN ('spouse','partner')", relationshipId, treeId);
    }
    private static List<Dictionary<string, string> > PartialList(SqliteDb db, bool includeArchived) {
      List<Dictionary<string, string> > items = db.Query("SELECT pv.*,'private' AS visibility,ft.long_id AS tree_long_id,ft.name AS tree_name,ft.status AS tree_status,r.id AS relationship_exists FROM family_tree_partial_views pv JOIN family_trees ft ON ft.id=pv.family_tree_id LEFT JOIN relationships r ON r.id=pv.anchor_relationship_id AND r.family_tree_id=pv.family_tree_id AND r.relationship_type IN ('spouse','partner') AND ((r.person1_id=pv.anchor_person_id AND r.person2_id=pv.anchor_partner_id) OR (r.person1_id=pv.anchor_partner_id AND r.person2_id=pv.anchor_person_id)) WHERE pv.status IN (" + (includeArchived ? "'active','disabled','archived'" : "'active','disabled'") + ") ORDER BY ft.id,pv.name,pv.id");
      foreach (Dictionary<string, string> item in items) {
        if ((Q(item, "relationship_exists") == "" || Q(item, "tree_status") != "active") && Q(item, "status") == "active") {
          string reason = Q(item, "relationship_exists") == "" ? "anchor_removed" : "tree_unavailable";
          db.Execute("UPDATE family_tree_partial_views SET status='disabled',disabled_reason=?,updated_at=? WHERE id=? AND status='active'", reason, Now(), QI(item, "id"));
          item["status"] = "disabled"; item["disabled_reason"] = reason;
        }
      }
      return items;
    }
    // [anchor person id, anchor relationship id, name, long id] for partial_id / partial_temp in the request.
    private Dictionary<string, string> PartialContext(SqliteDb db, Dictionary<string, string> tree, string partialId, string temporary, bool includeArchived) {
      if (partialId.Trim() != "") {
        Dictionary<string, string> row = PartialList(db, true).FirstOrDefault(p => FixedEquals(Q(p, "long_id"), partialId.Trim()));
        return row != null && (Q(row, "status") == "active" || (includeArchived && Q(row, "status") == "archived")) && Q(row, "relationship_exists") != "" && QI(row, "family_tree_id") == QI(tree, "id") ? row : null;
      }
      if (temporary.Trim() != "") {
        int[] decoded = TempDecode(temporary.Trim());
        if (decoded != null && decoded[0] == QI(tree, "id")) {
          Dictionary<string, string> anchor = PartialAnchor(db, decoded[0], decoded[1]);
          if (anchor != null) return new Dictionary<string, string> { { "anchor_person_id", Q(anchor, "person1_id") }, { "anchor_relationship_id", Q(anchor, "id") }, { "name", T("Temporary Partial Tree") }, { "long_id", "" } };
        }
      }
      return null;
    }
    private List<int> PartialPersonIds(FamilyTreeGraph graph, Dictionary<string, string> partial) {
      FamilyTreeRenderer renderer = Renderer(graph, false, QI(partial, "anchor_relationship_id"));
      renderer.BuildTree(QI(partial, "anchor_person_id"));
      return renderer.RenderedPersonIds.ToList();
    }

    // ---------- canvas and stats ----------
    private string CanvasHtml(SqliteDb db, Dictionary<string, string> tree, Dictionary<string, string> partial, FamilyTreeGraph graph, out List<int> rendered) {
      rendered = null;
      int root = partial != null ? QI(partial, "anchor_person_id") : QI(tree, "root_person_id");
      if (root > 0) {
        FamilyTreeRenderer renderer = Renderer(graph ?? LoadGraph(db, tree), true, partial != null ? QI(partial, "anchor_relationship_id") : 0);
        string html = "<ul>" + renderer.BuildTree(root) + "</ul>";
        rendered = renderer.RenderedPersonIds.ToList();
        return html;
      }
      return "\n      <div class=\"empty_tree_canvas\">\n        <button type=\"button\" class=\"empty_tree_add_first\" id=\"addFirstMemberBtn\" title=\"" + TreeText.H("Add First Member") + "\">\n          <span>+</span>\n        </button>\n      </div>\n    ";
    }

    private static List<Dictionary<string, string> > ScopePeople(SqliteDb db, int treeId, ICollection<int> ids) {
      List<Dictionary<string, string> > people = db.Query("SELECT p.* FROM persons p JOIN (" + MemberSql + ") m ON m.person_id=p.id ORDER BY p.id", treeId);
      if (ids != null) { HashSet<int> scope = new HashSet<int>(ids); people = people.Where(p => scope.Contains(QI(p, "id"))).ToList(); }
      return people;
    }
    private static bool Deceased(Dictionary<string, string> p) { string d = Q(p, "death_date").Trim(); return QI(p, "is_deceased") == 1 || (d != "" && d != "0000-00-00"); }
    private static HashSet<int> SpouseTargets(SqliteDb db, int treeId) { return new HashSet<int>(db.Query("SELECT person2_id FROM relationships WHERE family_tree_id=? AND relationship_type='spouse'", treeId).Select(r => QI(r, "person2_id"))); }
    private static bool InCategory(Dictionary<string, string> p, string category, HashSet<int> spouses) {
      string g = Q(p, "gender");
      switch (category) {
        case "total": return true;
        case "women": return g == "F";
        case "men": return g == "M";
        case "deceased": return Deceased(p);
        case "brothers_in_law": return g == "M" && spouses.Contains(QI(p, "id"));
        case "daughters_in_law": return g == "F" && spouses.Contains(QI(p, "id"));
        default: return false;
      }
    }
    private static readonly string[] StatCategories = { "total", "women", "men", "brothers_in_law", "daughters_in_law", "deceased" };
    private static string StatsHtml(SqliteDb db, int treeId, ICollection<int> ids) {
      List<Dictionary<string, string> > people = ScopePeople(db, treeId, ids);
      HashSet<int> spouses = SpouseTargets(db, treeId);
      string[] labels = { "All", "Women", "Men", "Brothers-in-Law", "Daughters-in-Law", "Deceased" };
      StringBuilder html = new StringBuilder();
      for (int i = 0; i < StatCategories.Length; i++) {
        if (html.Length > 0) html.Append("<span>•</span>");
        html.Append("<button type='button' class='one_value_stats' data-tree-stat='" + StatCategories[i] + "' aria-haspopup='dialog'><span class='wrapper_one_value_stats display_grid auto2 gap05'><span class='title'>" + TreeText.H(labels[i]) + ":</span><span class='value'>" + N(people.Count(p => InCategory(p, StatCategories[i], spouses))) + "</span></span></button>");
      }
      return html.ToString();
    }

    private Dictionary<string, object> Success(SqliteDb db, string message, string treeLongId, int focus, bool profileUpdate, TreeRequest r) {
      Dictionary<string, string> tree = TreeByLongId(db, treeLongId);
      if (tree == null) return Ok(message, new Dictionary<string, object> { { "tree_long_id", treeLongId }, { "focus_person_id", focus } });
      FamilyTreeGraph graph = LoadGraph(db, tree);
      Dictionary<string, string> partial = PartialContext(db, tree, r["partial_id"], r["partial_temp"], false);
      List<int> rendered;
      string html = CanvasHtml(db, tree, partial, graph, out rendered);
      return Ok(message, new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") }, { "focus_person_id", focus }, { "profile_update", profileUpdate }, { "tree_html", html }, { "stats_html", StatsHtml(db, QI(tree, "id"), partial != null ? rendered : null) } });
    }

    // ---------- person helpers ----------
    private static string CleanText(string value, int max) { value = (value ?? "").Trim(); return value == "" ? null : value.Length > max ? value.Substring(0, max) : value; }
    private static string CleanDate(string value) { DateTime parsed; value = (value ?? "").Trim(); return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ? value : null; }
    private static string CleanEmail(string value) { value = CleanText(value, 191); return value != null && Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") ? value : null; }
    private static string CleanUrl(string value) {
      value = CleanText(value, 255);
      if (value == null) return null;
      if (!Regex.IsMatch(value, "^https?://", RegexOptions.IgnoreCase)) value = "https://" + value;
      Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https") && uri.Host.Contains(".") ? value : null;
    }
    private static Dictionary<string, object> PersonPayload(TreeRequest r, string prefix) {
      string marital = r.Has(prefix + "marital_status") ? r[prefix + "marital_status"] : "single";
      if (!new[] { "single", "married", "widowed", "divorced", "separated" }.Contains(marital)) marital = "single";
      if (r.Has(prefix + "is_deceased") && r[prefix + "is_deceased"] != "0" && r[prefix + "is_deceased"] != "1") throw Fail("Invalid deceased status.");
      string death = CleanDate(r[prefix + "death_date"]);
      object deceased = death != null ? (object)1 : r.Has(prefix + "is_deceased") ? (object)Int32.Parse(r[prefix + "is_deceased"], CultureInfo.InvariantCulture) : null;
      return new Dictionary<string, object> {
        { "birth_date", CleanDate(r[prefix + "birth_date"]) }, { "death_date", death }, { "is_deceased", deceased },
        { "phone1", CleanText(r[prefix + "phone1"], 50) }, { "phone2", CleanText(r[prefix + "phone2"], 50) },
        { "email1", CleanEmail(r[prefix + "email1"]) }, { "email2", CleanEmail(r[prefix + "email2"]) },
        { "address", CleanText(r[prefix + "address"], 255) }, { "birth_place", CleanText(r[prefix + "birth_place"], 191) },
        { "nationality", CleanText(r[prefix + "nationality"], 120) }, { "blood_group", CleanText(r[prefix + "blood_group"], 20) },
        { "education_level", CleanText(r[prefix + "education_level"], 120) }, { "religion", CleanText(r[prefix + "religion"], 120) },
        { "website", CleanUrl(r[prefix + "website"]) }, { "marital_status", marital },
      };
    }
    private static readonly string[] ExtraColumns = { "birth_date", "death_date", "phone1", "phone2", "email1", "email2", "address", "religion", "website", "birth_place", "nationality", "blood_group", "marital_status", "education_level" };
    private static int CreatePerson(SqliteDb db, string first, string last, string gender, Dictionary<string, object> extra) {
      first = (first ?? "").Trim(); last = (last ?? "").Trim();
      if (first == "") return 0;
      extra = extra ?? new Dictionary<string, object>();
      object value;
      bool deceased = (extra.TryGetValue("death_date", out value) && value != null) || (extra.TryGetValue("is_deceased", out value) && value != null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1);
      List<object> args = new List<object> { "local_person_" + NewId(16), first, last, gender == "F" ? "F" : "M", deceased ? 1 : 0 };
      foreach (string column in ExtraColumns) args.Add(extra.TryGetValue(column, out value) ? value : (column == "marital_status" ? "single" : null));
      if (args[args.Count - 2] == null) args[args.Count - 2] = "single";
      args.Add(Now()); args.Add(Now());
      db.Execute("INSERT INTO persons(long_id,first_name,last_name,gender,is_deceased," + String.Join(",", ExtraColumns) + ",visibility,created_at,updated_at)VALUES(?,?,?,?,?," + String.Join(",", ExtraColumns.Select(c => "?")) + ",'visible',?,?)", args.ToArray());
      return (int)LastId(db);
    }
    private static int ExistingPersonForAdd(SqliteDb db, TreeRequest r, int treeId, string requiredGender) {
      int id = r.Int("existing_person_id");
      if (!PersonExists(db, id)) throw Fail("Select an existing person first.");
      if (InTree(db, treeId, id)) throw Fail("This person is already in the selected family tree.");
      if ((requiredGender == "M" || requiredGender == "F") && Gender(db, id) != requiredGender) throw Fail("Selected person does not match the required gender.");
      return id;
    }
    private static string UnknownOr(TreeRequest r, string flag, string field) { return r[flag] == "1" ? T("Unknown person") : r[field]; }
    private static Dictionary<string, string> SpouseRelationship(SqliteDb db, int treeId, int personId) {
      return db.QueryOne("SELECT id,person1_id,person2_id,sibling_person_id FROM relationships WHERE family_tree_id=? AND relationship_type='spouse' AND (person1_id=?2 OR person2_id=?2) ORDER BY id LIMIT 1", treeId, personId);
    }
    private static List<int> SpouseIds(SqliteDb db, int treeId, int personId) {
      return db.Query("SELECT person1_id,person2_id FROM relationships WHERE family_tree_id=? AND relationship_type='spouse' AND (person1_id=?2 OR person2_id=?2) ORDER BY id", treeId, personId)
        .Select(row => QI(row, "person1_id") == personId ? QI(row, "person2_id") : QI(row, "person1_id")).Where(id => id > 0).Distinct().ToList();
    }
    // Port of hosted tree_couple_sibling_parents: the partner flagged as this family's child owns the couple's
    // parent links. Returns (other partner, their parents) when the flagged partner has none here but the other does.
    private static Tuple<int, List<int> > CoupleSiblingParents(SqliteDb db, int treeId, int personId) {
      Dictionary<string, string> relationship = SpouseRelationship(db, treeId, personId);
      if (relationship == null) return null;
      int siblingId = QI(relationship, "sibling_person_id") > 0 ? QI(relationship, "sibling_person_id") : QI(relationship, "person1_id");
      if (siblingId != personId || ParentIds(db, treeId, personId).Count > 0) return null;
      int other = QI(relationship, "person1_id") == personId ? QI(relationship, "person2_id") : QI(relationship, "person1_id");
      List<int> parents = ParentIds(db, treeId, other);
      return parents.Count > 0 ? Tuple.Create(other, parents) : null;
    }
    // Port of hosted tree_sync_couple_sibling: move those parent links and the birth position to the flagged partner.
    private static void SyncCoupleSibling(SqliteDb db, int treeId, int personId) {
      Tuple<int, List<int> > move = CoupleSiblingParents(db, treeId, personId);
      if (move == null) return;
      db.Execute("UPDATE OR IGNORE parent_child SET child_id=? WHERE family_tree_id=? AND child_id=?", personId, treeId, move.Item1);
      db.Execute("DELETE FROM parent_child WHERE family_tree_id=? AND child_id=?", treeId, move.Item1);
      int order = QI(db.QueryOne("SELECT sibling_birth_order FROM family_tree_person_settings WHERE family_tree_id=? AND person_id=?", treeId, move.Item1), "sibling_birth_order");
      if (order <= 0) return;
      db.Execute("INSERT INTO family_tree_person_settings(family_tree_id,person_id,sibling_birth_order,created_at,updated_at)VALUES(?,?,?,?,?) ON CONFLICT(family_tree_id,person_id) DO UPDATE SET sibling_birth_order=excluded.sibling_birth_order,updated_at=excluded.updated_at", treeId, personId, order, Now(), Now());
      db.Execute("UPDATE family_tree_person_settings SET sibling_birth_order=NULL WHERE family_tree_id=? AND person_id=?", treeId, move.Item1);
    }

    private static void UpdateRootIfNeeded(SqliteDb db, int treeId) {
      if (QI(db.QueryOne("SELECT root_person_id FROM family_trees WHERE id=?", treeId), "root_person_id") > 0) return;
      Dictionary<string, string> row = db.QueryOne("SELECT person_id FROM (SELECT parent_id AS person_id FROM parent_child WHERE family_tree_id=?1 UNION SELECT child_id FROM parent_child WHERE family_tree_id=?1 UNION SELECT person1_id FROM relationships WHERE family_tree_id=?1 UNION SELECT person2_id FROM relationships WHERE family_tree_id=?1) x WHERE person_id IS NOT NULL LIMIT 1", treeId);
      if (row != null) db.Execute("UPDATE family_trees SET root_person_id=?,updated_at=? WHERE id=?", QI(row, "person_id"), Now(), treeId);
    }
    private static void SetRoot(SqliteDb db, int treeId, int? personId) { db.Execute("UPDATE family_trees SET root_person_id=?,updated_at=? WHERE id=?", personId.HasValue ? (object)personId.Value : null, Now(), treeId); }
    private static void TouchTree(SqliteDb db, int treeId) { db.Execute("UPDATE family_trees SET updated_at=? WHERE id=?", Now(), treeId); }
    private static void AddParentLink(SqliteDb db, int treeId, int child, int parent) { db.Execute("INSERT OR IGNORE INTO parent_child(family_tree_id,child_id,parent_id,created_at)VALUES(?,?,?,?)", treeId, child, parent, Now()); }
    private static List<int> ParentIds(SqliteDb db, int treeId, int personId) { return db.Query("SELECT DISTINCT parent_id FROM parent_child WHERE family_tree_id=? AND child_id=?", treeId, personId).Select(r => QI(r, "parent_id")).ToList(); }
    private static List<int> AddChildParentIds(SqliteDb db, int treeId, int anchor, int second) {
      if (second > 0 && second != anchor && InTree(db, treeId, second)) return new List<int> { anchor, second };
      Dictionary<string, string> spouse = SpouseRelationship(db, treeId, anchor);
      int other = spouse == null ? 0 : QI(spouse, "person1_id") == anchor ? QI(spouse, "person2_id") : QI(spouse, "person1_id");
      return new[] { anchor, other }.Where(id => id > 0).ToList();
    }
    // Children who share exactly these parents in this tree: one birth-position sequence.
    private static Dictionary<int, int?> SiblingOrders(SqliteDb db, int treeId, List<int> parentIds) {
      parentIds = parentIds.Where(id => id > 0).Distinct().ToList();
      Dictionary<int, int?> orders = new Dictionary<int, int?>();
      if (parentIds.Count == 0) return orders;
      foreach (Dictionary<string, string> row in db.Query("SELECT pc.child_id,s.sibling_birth_order FROM parent_child pc LEFT JOIN family_tree_person_settings s ON s.family_tree_id=?1 AND s.person_id=pc.child_id WHERE pc.family_tree_id=?1 AND pc.parent_id IN (" + String.Join(",", parentIds) + ") GROUP BY pc.child_id HAVING COUNT(DISTINCT pc.parent_id)=" + N(parentIds.Count) + " ORDER BY pc.child_id", treeId))
        orders[QI(row, "child_id")] = QI(row, "sibling_birth_order") > 0 ? (int?)QI(row, "sibling_birth_order") : null;
      return orders;
    }
    private static bool BirthOrderPresent(TreeRequest r, out int? order) {
      order = null;
      if (!r.Has("sibling_birth_order")) return false;
      string raw = r["sibling_birth_order"].Trim();
      if (raw == "") return true;
      int value;
      if (!Regex.IsMatch(raw, "^[0-9]+$") || !Int32.TryParse(raw, out value) || value < 1 || value > 65535) throw Fail("Birth position must be a positive number.");
      order = value; return true;
    }
    // A position is never shared: taking one already held hands that sibling this person's previous position.
    private void SaveSiblingBirthOrder(SqliteDb db, int treeId, int personId, TreeRequest r) {
      int? birthOrder;
      if (!BirthOrderPresent(r, out birthOrder)) return;
      if (!InTree(db, treeId, personId)) throw Fail("Person not found.");
      Dictionary<int, int?> current = SiblingOrders(db, treeId, ParentIds(db, treeId, personId));
      if (!current.ContainsKey(personId)) { int value = QI(db.QueryOne("SELECT sibling_birth_order FROM family_tree_person_settings WHERE family_tree_id=? AND person_id=?", treeId, personId), "sibling_birth_order"); current[personId] = value > 0 ? (int?)value : null; }
      Dictionary<int, int?> next = new Dictionary<int, int?>(current);
      Dictionary<string, object> moves = null;
      try { moves = new JavaScriptSerializer().DeserializeObject(r["sibling_birth_moves"]) as Dictionary<string, object>; } catch { }
      foreach (var move in moves ?? new Dictionary<string, object>()) {
        int id; Int32.TryParse(move.Key, out id);
        string raw = move.Value == null ? "" : Convert.ToString(move.Value, CultureInfo.InvariantCulture);
        int order = 0;
        if (id == personId || !current.ContainsKey(id) || (raw != "" && (!Regex.IsMatch(raw, "^[0-9]+$") || !Int32.TryParse(raw, out order) || order < 1 || order > 65535))) throw Fail("Birth position must be a positive number.");
        next[id] = raw == "" ? null : (int?)order;
      }
      if (birthOrder.HasValue) foreach (int id in next.Keys.ToList()) if (id != personId && next[id] == birthOrder) next[id] = current[personId];
      next[personId] = birthOrder;
      List<int> changed = next.Where(p => p.Value != current[p.Key] || p.Key == personId).Select(p => p.Key).ToList();
      foreach (int id in changed) if (next[id].HasValue && next.Values.Count(v => v == next[id]) > 1) throw Fail("Two children cannot share a birth position.");
      foreach (int id in changed) db.Execute("INSERT INTO family_tree_person_settings(family_tree_id,person_id,sibling_birth_order,created_at,updated_at)VALUES(?,?,?,?,?) ON CONFLICT(family_tree_id,person_id) DO UPDATE SET sibling_birth_order=excluded.sibling_birth_order,updated_at=excluded.updated_at", treeId, id, next[id].HasValue ? (object)next[id].Value : null, Now(), Now());
    }

    // ---------- the action endpoint ----------
    private static readonly HashSet<string> TreeReadActions = new HashSet<string> { "resolve_tree", "session_refresh", "tree_insights", "tree_important_dates", "list_stat_persons", "list_available_persons", "list_tree_couples", "get_person", "get_tree_state", "select_tree" };

    internal Dictionary<string, object> TreeAction(TreeRequest r, string csrf) {
      string action = r["action"];
      if (action == "session_refresh") return Ok("", new Dictionary<string, object> { { "csrf", CsrfToken } });
      if (!TreeReadActions.Contains(action) || action == "select_tree") {
        if (!CheckCsrf(csrf)) throw Fail("Invalid request. Please refresh and try again.", 403, "csrf_expired");
      }
      using (SqliteDb db = Open()) {
        bool write = !TreeReadActions.Contains(action) && action != "partial_package" && action != "paste_partial_preview" && action != "list_sibling_orders";
        if (write) db.Exec("BEGIN IMMEDIATE");
        try {
          Dictionary<string, object> result = TreeDispatch(db, r, action);
          if (write) db.Exec("COMMIT");
          if (write) ProtectDatabaseFile();
          return result;
        } catch {
          if (write) { try { db.Exec("ROLLBACK"); } catch { } }
          throw;
        }
      }
    }

    private Dictionary<string, object> TreeDispatch(SqliteDb db, TreeRequest r, string action) {
      Dictionary<string, string> tree;
      int treeId;
      switch (action) {
        case "resolve_tree": {
          tree = TreeByLongId(db, r["tree_long_id"]) ?? db.QueryOne("SELECT long_id FROM family_trees WHERE status='active' ORDER BY id LIMIT 1");
          return Ok("", new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") } });
        }
        case "select_tree": {
          tree = TreeByLongId(db, r["tree_long_id"]) ?? db.QueryOne("SELECT long_id FROM family_trees WHERE status='active' ORDER BY id LIMIT 1");
          if (tree == null) return Ok("", new Dictionary<string, object> { { "tree_long_id", "" } });
          db.Execute("INSERT INTO local_plugin_settings(slug,setting_key,setting_value,updated_at)VALUES('core-tree','last_tree',?,?) ON CONFLICT(slug,setting_key) DO UPDATE SET setting_value=excluded.setting_value,updated_at=excluded.updated_at", Q(tree, "long_id"), Now());
          return Ok("Family tree selected.", new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") } });
        }
        case "get_tree_state": {
          tree = RequireTree(db, r);
          Dictionary<string, string> partial = PartialContext(db, tree, r["partial_id"], r["partial_temp"], false);
          if ((r["partial_id"] != "" || r["partial_temp"] != "") && partial == null) throw Fail("Access denied", 403);
          List<int> rendered;
          string html = CanvasHtml(db, tree, partial, LoadGraph(db, tree), out rendered);
          string stats = StatsHtml(db, QI(tree, "id"), partial != null ? rendered : null);
          string digest;
          using (SHA256 sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(html + "\n" + stats))).Replace("-", "").ToLowerInvariant();
          return Ok("Family tree loaded.", new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") }, { "tree_html", html }, { "stats_html", stats }, { "cache_token", "" }, { "cache_digest", digest } });
        }
        case "tree_insights":
        case "tree_important_dates":
        case "list_stat_persons": {
          tree = RequireTree(db, r);
          Dictionary<string, string> partial = PartialContext(db, tree, r["partial_id"], r["partial_temp"], action != "list_stat_persons");
          if ((r["partial_id"] != "" || r["partial_temp"] != "") && partial == null) throw Fail(action == "list_stat_persons" ? "Family tree not found." : "Access denied", action == "list_stat_persons" ? 404 : 403);
          List<int> ids = partial != null ? PartialPersonIds(LoadGraph(db, tree), partial) : null;
          string title = partial != null ? Q(partial, "name") : Q(tree, "name");
          if (action == "tree_insights") return Ok("", new Dictionary<string, object> { { "html", InsightsHtml(db, QI(tree, "id"), ids) }, { "title", title }, { "traffic_available", false }, { "visits_html", null } });
          if (action == "tree_important_dates") { Dictionary<string, object> page = ImportantDates(db, QI(tree, "id"), ids, r); page["title"] = title; return Ok("", page); }
          return StatPersons(db, QI(tree, "id"), ids, r);
        }
        case "list_available_persons": {
          tree = RequireTree(db, r);
          string gender = r["gender"] == "M" || r["gender"] == "F" ? r["gender"] : "", search = r["search"].Trim();
          int limit = Math.Max(12, Math.Min(48, r.Has("limit") ? r.Int("limit") : 24)), offset = Math.Max(0, r.Int("offset"));
          List<Dictionary<string, object> > people = db.Query("SELECT p.id,p.first_name,p.last_name,p.gender,p.image_name,p.visibility,p.death_date,p.is_deceased FROM persons p WHERE p.id NOT IN (SELECT person_id FROM (" + MemberSql + ") WHERE person_id IS NOT NULL) AND (?2='' OR p.gender=?2) AND (?3='' OR (coalesce(p.first_name,'')||' '||coalesce(p.last_name,'')) LIKE '%'||?3||'%') ORDER BY p.first_name COLLATE NOCASE,p.last_name COLLATE NOCASE,p.id LIMIT ?4 OFFSET ?5", QI(tree, "id"), gender, search, limit, offset)
            .Select(p => new Dictionary<string, object> { { "id", QI(p, "id") }, { "name", (Q(p, "first_name") + " " + Q(p, "last_name")).Trim() }, { "gender", Q(p, "gender") == "" ? "M" : Q(p, "gender") }, { "visibility", Q(p, "visibility") == "" ? "visible" : Q(p, "visibility") }, { "is_deceased", Deceased(p) }, { "image_sm_url", PersonImageUrl(Q(p, "image_name"), Q(p, "gender") == "" ? "M" : Q(p, "gender"), "sm") } }).ToList();
          return Ok("Available people loaded.", new Dictionary<string, object> { { "people", people }, { "next_offset", offset + people.Count }, { "has_more", people.Count == limit } });
        }
        case "create_tree": {
          if (QI(db.QueryOne("SELECT COUNT(*) AS n FROM family_trees WHERE status IN ('active','archived')"), "n") >= TreeLimit) throw Fail("Racinage Free includes 2 family trees. Archive or delete a tree, or upgrade to add more.");
          string name = r["name"].Trim(); if (name == "") name = "New Family Tree"; if (name.Length > 120) name = name.Substring(0, 120);
          string description = r["description"].Trim(); if (description.Length > 2000) description = description.Substring(0, 2000);
          string longId = NewId(10);
          db.Execute("INSERT INTO family_trees(long_id,name,description,status,created_at,updated_at)VALUES(?,?,?,'active',?,?)", longId, name, description, Now(), Now());
          tree = TreeByLongId(db, longId);
          List<int> rendered;
          return Ok("Family tree created.", new Dictionary<string, object> { { "tree_long_id", longId }, { "tree_name", name }, { "tree_html", CanvasHtml(db, tree, null, null, out rendered) }, { "stats_html", StatsHtml(db, QI(tree, "id"), null) } });
        }
        case "rename_tree": {
          tree = RequireTree(db, r);
          string name = r["name"].Trim();
          if (name == "") throw Fail("Tree name is required.");
          if (name.Length > 120) throw Fail("Tree name is too long.");
          db.Execute("UPDATE family_trees SET name=?,updated_at=? WHERE id=?", name, Now(), QI(tree, "id"));
          return Ok("Tree renamed.", new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") } });
        }
        case "duplicate_tree": return DuplicateTree(db, r);
        case "archive_tree":
        case "delete_tree": {
          tree = RequireTree(db, r);
          if (QI(db.QueryOne("SELECT COUNT(*) AS n FROM family_trees WHERE status='active'"), "n") <= 1) throw Fail("Keep at least one active family tree.");
          db.Execute("UPDATE family_trees SET status=?,updated_at=? WHERE id=?", action == "archive_tree" ? "archived" : "deleted", Now(), QI(tree, "id"));
          return Ok(action == "archive_tree" ? "Tree archived." : "Tree deleted.", new Dictionary<string, object> { { "tree_long_id", Q(db.QueryOne("SELECT long_id FROM family_trees WHERE status='active' ORDER BY id LIMIT 1"), "long_id") } });
        }
        case "add_first_member": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          if (QI(tree, "root_person_id") > 0) throw Fail("This tree already has a first member.");
          int personId;
          if (r["person_source"] == "existing") personId = ExistingPersonForAdd(db, r, treeId, "");
          else {
            personId = CreatePerson(db, UnknownOr(r, "identity_unknown", "first_name"), r["identity_unknown"] == "1" ? "" : r["last_name"], r["gender"] == "" ? "M" : r["gender"], PersonPayload(r, ""));
            if (personId == 0) throw Fail("First name is required.");
            ApplyPersonUpload(db, personId, r.File("first_member_image"));
          }
          SetRoot(db, treeId, personId);
          return Success(db, "First member added.", Q(tree, "long_id"), personId, false, r);
        }
        case "add_parents": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          int childId = r.Int("child_id");
          if (!PersonExists(db, childId)) throw Fail("Child member not found.");
          bool mother = r["parent_role"] == "mother";
          string gender = mother ? "F" : "M";
          if (db.QueryOne("SELECT pc.parent_id FROM parent_child pc JOIN persons p ON p.id=pc.parent_id WHERE pc.child_id=? AND pc.family_tree_id=? AND p.gender=?", childId, treeId, gender) != null)
            throw Fail(mother ? "This member already has a mother in this tree." : "This member already has a father in this tree.");
          int parentId;
          if (r["person_source"] == "existing") {
            parentId = ExistingPersonForAdd(db, r, treeId, "");
            if (parentId == childId) throw Fail("A member cannot be their own parent.");
            if (Gender(db, parentId) != gender) throw Fail("Selected parent was not found.");
            if (Descendants(db, treeId, childId).Contains(parentId)) throw Fail("Selected parent is already below this member in the tree.");
          } else {
            parentId = CreatePerson(db, UnknownOr(r, "parent_identity_unknown", "parent_first_name"), r["parent_identity_unknown"] == "1" ? "" : r["parent_last_name"], gender, null);
            if (parentId == 0) throw Fail("First name is required.");
            ApplyPersonUpload(db, parentId, r.File("parent_image"));
          }
          AddParentLink(db, treeId, childId, parentId);
          Dictionary<string, string> other = db.QueryOne("SELECT pc.parent_id FROM parent_child pc JOIN persons p ON p.id=pc.parent_id WHERE pc.child_id=? AND pc.family_tree_id=? AND p.gender=? LIMIT 1", childId, treeId, mother ? "M" : "F");
          int otherId = QI(other, "parent_id");
          if (otherId > 0 && db.QueryOne("SELECT id FROM relationships WHERE family_tree_id=? AND relationship_type='spouse' AND ((person1_id=?2 AND person2_id=?3) OR (person1_id=?3 AND person2_id=?2))", treeId, parentId, otherId) == null)
            db.Execute("INSERT INTO relationships(family_tree_id,person1_id,person2_id,relationship_type,is_current,sibling_person_id,created_at)VALUES(?,?,?,'spouse',1,?,?)", treeId, parentId, otherId, parentId, Now());
          if (QI(tree, "root_person_id") == childId || QI(tree, "root_person_id") == 0) SetRoot(db, treeId, parentId);
          return Success(db, "Parent added.", Q(tree, "long_id"), parentId, false, r);
        }
        case "add_spouse":
        case "add_partner":
        case "add_child": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          int? ignored; BirthOrderPresent(r, out ignored);
          int anchor = r.Int("anchor_person_id");
          if (!PersonExists(db, anchor)) throw Fail("Anchor person not found.");
          string anchorGender = Gender(db, anchor);
          string required = action == "add_child" ? "" : anchorGender == "M" ? "F" : anchorGender == "F" ? "M" : "";
          int newId;
          if (r["person_source"] == "existing") newId = ExistingPersonForAdd(db, r, treeId, required);
          else {
            newId = CreatePerson(db, UnknownOr(r, "person_identity_unknown", "person_first_name"), r["person_identity_unknown"] == "1" ? "" : r["person_last_name"], required != "" ? required : (r["person_gender"] == "" ? "M" : r["person_gender"]), PersonPayload(r, "person_"));
            if (newId == 0) throw Fail("First name is required.");
            ApplyPersonUpload(db, newId, r.File("person_image"));
          }
          if (action == "add_child") {
            AddParentLink(db, treeId, newId, anchor);
            int second = r.Int("second_parent_id"), other = 0;
            if (second > 0 && second != newId && second != anchor && PersonExists(db, second)) other = second;
            else { Dictionary<string, string> spouse = SpouseRelationship(db, treeId, anchor); if (spouse != null) other = QI(spouse, "person1_id") == anchor ? QI(spouse, "person2_id") : QI(spouse, "person1_id"); }
            if (other > 0) AddParentLink(db, treeId, newId, other);
            SaveSiblingBirthOrder(db, treeId, newId, r);
          } else if (action == "add_spouse") {
            db.Execute("INSERT INTO relationships(family_tree_id,person1_id,person2_id,relationship_type,is_current,sibling_person_id,created_at)VALUES(?,?,?,'spouse',1,?,?)", treeId, anchor, newId, r["family_sibling_person"] == "new" ? newId : anchor, Now());
          } else {
            db.Execute("INSERT INTO relationships(family_tree_id,person1_id,person2_id,relationship_type,is_current,created_at)VALUES(?,?,?,'partner',1,?)", treeId, anchor, newId, Now());
          }
          UpdateRootIfNeeded(db, treeId);
          return Success(db, "Person added.", Q(tree, "long_id"), newId, false, r);
        }
        case "save_view": {
          db.Execute("INSERT INTO local_plugin_settings(slug,setting_key,setting_value,updated_at)VALUES('core-tree','view',?,?) ON CONFLICT(slug,setting_key) DO UPDATE SET setting_value=excluded.setting_value,updated_at=excluded.updated_at", r["view"] == "vertical" ? "vertical" : "horizontal", Now());
          return Ok("");
        }
        case "get_person": return GetPerson(db, r);
        case "list_sibling_orders": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          int personId = r.Int("person_id"), anchor = r.Int("anchor_person_id");
          List<int> parents;
          int hidden = 0;
          if (personId > 0 && InTree(db, treeId, personId)) {
            parents = ParentIds(db, treeId, personId);
            Tuple<int, List<int> > move = parents.Count > 0 ? null : CoupleSiblingParents(db, treeId, personId);
            if (move != null) { parents = move.Item2; hidden = move.Item1; }
          }
          else if (anchor > 0 && InTree(db, treeId, anchor)) parents = AddChildParentIds(db, treeId, anchor, r.Int("second_parent_id"));
          else throw Fail("Person not found.");
          Dictionary<int, int?> orders = SiblingOrders(db, treeId, parents);
          List<Dictionary<string, object> > siblings = new List<Dictionary<string, object> >();
          if (orders.Count > 0)
            foreach (Dictionary<string, string> row in db.Query("SELECT id,first_name,last_name,gender,image_name FROM persons WHERE id IN (" + String.Join(",", orders.Keys) + ") ORDER BY id"))
              if (QI(row, "id") != personId && QI(row, "id") != hidden) siblings.Add(new Dictionary<string, object> { { "id", QI(row, "id") }, { "name", (Q(row, "first_name") + " " + Q(row, "last_name")).Trim() }, { "image_url", PersonImageUrl(Q(row, "image_name"), Q(row, "gender"), "sm") }, { "order", orders[QI(row, "id")] } });
          return Ok("", new Dictionary<string, object> { { "siblings", siblings }, { "has_parents", parents.Count > 0 } });
        }
        case "update_person": {
          int personId = r.Int("person_id");
          if (personId == 0 && r["person_long_id"] != "") personId = QI(db.QueryOne("SELECT id FROM persons WHERE long_id=?", r["person_long_id"].Trim()), "id");
          if (!PersonExists(db, personId)) throw Fail("Person not found.");
          tree = TreeByLongId(db, r["tree_long_id"]);
          if (tree == null || !InTree(db, QI(tree, "id"), personId)) throw Fail("Person not found.");
          int? ignored; BirthOrderPresent(r, out ignored);
          string first = r["person_first_name"].Trim();
          if (first == "") throw Fail("First name is required.");
          Dictionary<string, object> extra = PersonPayload(r, "person_");
          List<object> args = new List<object> { first, r["person_last_name"].Trim(), r["person_gender"] == "F" ? "F" : "M" };
          foreach (string column in ExtraColumns) args.Add(extra[column]);
          args.Add(extra["is_deceased"]); args.Add(Now()); args.Add(personId);
          db.Execute("UPDATE persons SET first_name=?,last_name=?,gender=?," + String.Join(",", ExtraColumns.Select(c => c + "=?")) + ",is_deceased=COALESCE(?,is_deceased),updated_at=? WHERE id=?", args.ToArray());
          ApplyPersonUpload(db, personId, r.File("person_image"));
          SyncCoupleSibling(db, QI(tree, "id"), personId);
          SaveSiblingBirthOrder(db, QI(tree, "id"), personId, r);
          return Success(db, "Person updated.", Q(tree, "long_id"), personId, true, r);
        }
        case "set_primary_relationship": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          int personId = r.Int("person_id"), relationshipId = r.Int("relationship_id"), root = QI(tree, "root_person_id");
          if (personId <= 0 || relationshipId <= 0 || root <= 0 || personId == root) throw Fail("Relationship not found.");
          if (db.QueryOne("SELECT id FROM relationships WHERE id=? AND family_tree_id=? AND relationship_type IN ('spouse','partner') AND ((person1_id=?3 AND person2_id=?4) OR (person1_id=?4 AND person2_id=?3))", relationshipId, treeId, root, personId) == null) throw Fail("Relationship not found.");
          db.Execute("UPDATE family_trees SET primary_relationship_id=?,updated_at=? WHERE id=?", relationshipId, Now(), treeId);
          db.Execute("UPDATE family_tree_person_settings SET preferred_relationship_id=NULL WHERE family_tree_id=? AND person_id=?", treeId, root);
          return Success(db, "Main couple updated.", Q(tree, "long_id"), personId, false, r);
        }
        case "set_sibling_status": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          int personId = r.Int("person_id");
          if (!PersonExists(db, personId) || !InTree(db, treeId, personId)) throw Fail("Person not found.");
          Dictionary<string, string> relationship = SpouseRelationship(db, treeId, personId);
          if (relationship == null) throw Fail("Only people in a couple can change sibling status.");
          int other = QI(relationship, "person1_id") == personId ? QI(relationship, "person2_id") : QI(relationship, "person1_id");
          int siblingId = r["sibling_value"] == "no" ? other : personId;
          db.Execute("UPDATE relationships SET sibling_person_id=? WHERE id=? AND family_tree_id=?", siblingId, QI(relationship, "id"), treeId);
          SyncCoupleSibling(db, treeId, siblingId);
          return Success(db, "Sibling status updated.", Q(tree, "long_id"), personId, false, r);
        }
        case "replace_person": {
          tree = RequireTree(db, r); treeId = QI(tree, "id");
          int oldId = r.Int("person_id");
          if (!PersonExists(db, oldId) || !InTree(db, treeId, oldId)) throw Fail("Person not found.");
          int newId;
          if (r["person_source"] == "existing") newId = ExistingPersonForAdd(db, r, treeId, "");
          else {
            newId = CreatePerson(db, r["person_first_name"], r["person_last_name"], r["person_gender"] == "" ? "M" : r["person_gender"], PersonPayload(r, "person_"));
            if (newId == 0) throw Fail("First name is required.");
            ApplyPersonUpload(db, newId, r.File("person_image"));
          }
          ReplaceInTree(db, treeId, oldId, newId);
          if (QI(tree, "root_person_id") == oldId) SetRoot(db, treeId, newId);
          return Success(db, "Person replaced in this tree.", Q(tree, "long_id"), newId, false, r);
        }
        case "hide_person": {
          tree = RequireTree(db, r);
          int personId = r.Int("person_id");
          if (personId == 0 && r["person_long_id"] != "") personId = QI(db.QueryOne("SELECT id FROM persons WHERE long_id=?", r["person_long_id"].Trim()), "id");
          string visibility = r["visibility"] == "visible" ? "visible" : "hidden";
          if (!PersonExists(db, personId) || !InTree(db, QI(tree, "id"), personId)) throw Fail("Person not found.");
          Dictionary<string, string> person = db.QueryOne("SELECT first_name,last_name FROM persons WHERE id=?", personId);
          db.Execute("UPDATE persons SET visibility=?,updated_at=? WHERE id=?", visibility, Now(), personId);
          string label = (Q(person, "first_name") + " " + Q(person, "last_name")).Trim(); if (label == "") label = T("Person");
          return Ok("Visibility updated.", new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") }, { "focus_person_id", personId }, { "person_patch", new Dictionary<string, object> { { "id", personId }, { "visibility", visibility }, { "label", visibility == "hidden" ? T("Hidden person") : label } } } });
        }
        case "toggle_life_status": {
          tree = RequireTree(db, r);
          int personId = r.Int("person_id");
          if (!PersonExists(db, personId) || !InTree(db, QI(tree, "id"), personId)) throw Fail("Person not found.");
          bool deceased = !Deceased(db.QueryOne("SELECT is_deceased,death_date FROM persons WHERE id=?", personId));
          db.Execute("UPDATE persons SET death_date=NULL,is_deceased=?,updated_at=? WHERE id=?", deceased ? 1 : 0, Now(), personId);
          return Ok(deceased ? "Person marked as deceased." : "Person marked as alive.", new Dictionary<string, object> { { "tree_long_id", Q(tree, "long_id") }, { "focus_person_id", personId }, { "person_patch", new Dictionary<string, object> { { "id", personId }, { "death_date", null }, { "is_deceased", deceased } } }, { "stats_delta", new Dictionary<string, object> { { "deceased", deceased ? 1 : -1 } } } });
        }
        case "remove_person_from_tree": return RemovePerson(db, r);
        case "list_tree_couples":
        case "promote_partner":
        case "change_partner":
        case "move_child": return Rearrange(db, r, action);
        case "create_partial_tree": {
          tree = RequireTree(db, r);
          int relationshipId = Math.Max(0, r.Int("relationship_id"));
          if (r["anchor_ref"] != "") { int[] anchorRef = TempDecode(r["anchor_ref"]); if (anchorRef == null || anchorRef[0] != QI(tree, "id")) throw Fail("The selected parent couple reference is invalid or expired."); relationshipId = anchorRef[1]; }
          Dictionary<string, string> anchor = PartialAnchor(db, QI(tree, "id"), relationshipId);
          if (anchor == null) throw Fail("The selected parent couple is no longer available.");
          if (r["partial_kind"] != "permanent") {
            string token = TempToken(QI(tree, "id"), relationshipId);
            return Ok("Temporary Partial Tree ready.", new Dictionary<string, object> { { "temporary", true }, { "partial_token", token }, { "tree_long_id", Q(tree, "long_id") }, { "url", "/trees?tree=" + Uri.EscapeDataString(Q(tree, "long_id")) + "&partial_temp=" + Uri.EscapeDataString(token) } });
          }
          string name = Regex.Replace(r["name"], "<[^>]*>", "").Trim(); if (name.Length > 140) name = name.Substring(0, 140);
          if (name == "") name = (Q(anchor, "person1_name") + " & " + Q(anchor, "person2_name")).Trim();
          string longId = NewId(10);
          db.Execute("INSERT INTO family_tree_partial_views(long_id,family_tree_id,anchor_relationship_id,anchor_person_id,anchor_partner_id,name,status,created_at,updated_at)VALUES(?,?,?,?,?,?,'active',?,?)", longId, QI(tree, "id"), relationshipId, QI(anchor, "person1_id"), QI(anchor, "person2_id"), name, Now(), Now());
          return Ok("Permanent Partial Tree created.", new Dictionary<string, object> { { "long_id", longId }, { "name", name }, { "tree_long_id", Q(tree, "long_id") }, { "visibility", "private" }, { "status", "active" }, { "url", "/trees?tree=" + Uri.EscapeDataString(Q(tree, "long_id")) + "&partial=" + Uri.EscapeDataString(longId) } });
        }
        case "update_partial_tree": return UpdatePartial(db, r);
        case "partial_package": return PartialPackage(db, r);
        case "paste_partial_preview":
        case "paste_partial": return PastePartial(db, r, action == "paste_partial_preview");
        case "ai_bulk_add_people":
        case "ai_cancel_progressive_plan":
        case "ai_undo_progressive_unit": throw Fail("This feature is available on racinage.com.", 400);
        default: throw Fail("Unknown tree action.", 400);
      }
    }

    private static HashSet<int> Descendants(SqliteDb db, int treeId, int personId) {
      Dictionary<int, List<int> > children = new Dictionary<int, List<int> >();
      foreach (Dictionary<string, string> link in db.Query("SELECT parent_id,child_id FROM parent_child WHERE family_tree_id=? ORDER BY id", treeId)) {
        List<int> list; if (!children.TryGetValue(QI(link, "parent_id"), out list)) children[QI(link, "parent_id")] = list = new List<int>();
        list.Add(QI(link, "child_id"));
      }
      HashSet<int> seen = new HashSet<int> { personId }; Queue<int> queue = new Queue<int>(new[] { personId });
      while (queue.Count > 0) { List<int> list; if (children.TryGetValue(queue.Dequeue(), out list)) foreach (int id in list) if (seen.Add(id)) queue.Enqueue(id); }
      return seen;
    }

    private static void ReplaceInTree(SqliteDb db, int treeId, int oldId, int newId) {
      foreach (string sql in new[] { "UPDATE relationships SET person1_id=?1 WHERE family_tree_id=?2 AND person1_id=?3", "UPDATE relationships SET person2_id=?1 WHERE family_tree_id=?2 AND person2_id=?3", "UPDATE relationships SET sibling_person_id=?1 WHERE family_tree_id=?2 AND sibling_person_id=?3", "UPDATE OR IGNORE parent_child SET parent_id=?1 WHERE family_tree_id=?2 AND parent_id=?3", "UPDATE OR IGNORE parent_child SET child_id=?1 WHERE family_tree_id=?2 AND child_id=?3" })
        db.Execute(sql, newId, treeId, oldId);
      db.Execute("DELETE FROM parent_child WHERE family_tree_id=? AND (parent_id=?3 OR child_id=?3)", treeId, newId, oldId);
    }

    private static void RemoveLinks(SqliteDb db, int treeId, int personId) {
      db.Execute("UPDATE family_trees SET primary_relationship_id=NULL WHERE id=?1 AND primary_relationship_id IN (SELECT id FROM relationships WHERE family_tree_id=?1 AND (person1_id=?2 OR person2_id=?2))", treeId, personId);
      db.Execute("UPDATE family_tree_person_settings SET preferred_relationship_id=NULL WHERE family_tree_id=?1 AND (person_id=?2 OR preferred_relationship_id IN (SELECT id FROM relationships WHERE family_tree_id=?1 AND (person1_id=?2 OR person2_id=?2)))", treeId, personId);
      db.Execute("UPDATE family_tree_partial_views SET status='disabled',disabled_reason='anchor_removed',updated_at=?3 WHERE family_tree_id=?1 AND status='active' AND anchor_relationship_id IN (SELECT id FROM relationships WHERE family_tree_id=?1 AND (person1_id=?2 OR person2_id=?2))", treeId, personId, Now());
      db.Execute("DELETE FROM relationships WHERE family_tree_id=?1 AND (person1_id=?2 OR person2_id=?2)", treeId, personId);
      db.Execute("DELETE FROM parent_child WHERE family_tree_id=?1 AND (child_id=?2 OR parent_id=?2)", treeId, personId);
    }

    private Dictionary<string, object> RemovePerson(SqliteDb db, TreeRequest r) {
      Dictionary<string, string> tree = RequireTree(db, r);
      int treeId = QI(tree, "id"), personId = r.Int("person_id");
      if (!PersonExists(db, personId)) throw Fail("Person not found.");
      if (r["remove_mode"] == "keep") {
        Dictionary<string, string> person = db.QueryOne("SELECT gender FROM persons WHERE id=?", personId);
        db.Execute("INSERT INTO persons(long_id,first_name,last_name,gender,visibility,created_at,updated_at)VALUES(?,'Hidden','person',?,'hidden',?,?)", "local_person_" + NewId(16), Q(person, "gender") == "F" ? "F" : "M", Now(), Now());
        int ghost = (int)LastId(db);
        ReplaceInTree(db, treeId, personId, ghost);
        if (QI(tree, "root_person_id") == personId) SetRoot(db, treeId, ghost);
        return Success(db, "Person removed from this tree.", Q(tree, "long_id"), ghost, false, r);
      }
      bool isChild = db.QueryOne("SELECT 1 AS x FROM parent_child WHERE family_tree_id=? AND child_id=?", treeId, personId) != null;
      List<int> spouses = SpouseIds(db, treeId, personId);
      List<int> remove = new List<int> { personId };
      if (isChild) remove.AddRange(spouses);
      else if (spouses.Count > 0)
        foreach (Dictionary<string, string> child in db.Query("SELECT child_id FROM parent_child WHERE family_tree_id=? AND parent_id=?", treeId, personId))
          db.Execute("DELETE FROM parent_child WHERE family_tree_id=? AND child_id=?", treeId, QI(child, "child_id"));
      bool removedRoot = remove.Contains(QI(tree, "root_person_id"));
      foreach (int id in remove.Distinct()) RemoveLinks(db, treeId, id);
      if (removedRoot) {
        if (!isChild && spouses.Count > 0) SetRoot(db, treeId, spouses[0]);
        else { SetRoot(db, treeId, null); UpdateRootIfNeeded(db, treeId); }
      }
      return Success(db, "Person removed from this tree.", Q(tree, "long_id"), 0, false, r);
    }

    private Dictionary<string, object> DuplicateTree(SqliteDb db, TreeRequest r) {
      Dictionary<string, string> tree = RequireTree(db, r);
      string name = r["name"].Trim(); if (name == "") name = Q(tree, "name") + " Copy";
      if (name.Length > 120) throw Fail("Tree name is too long.");
      if (QI(db.QueryOne("SELECT COUNT(*) AS n FROM family_trees WHERE status IN ('active','archived')"), "n") >= TreeLimit) throw Fail("Racinage Free includes 2 family trees. Archive or delete a tree, or upgrade to add more.");
      string longId = NewId(10);
      int oldId = QI(tree, "id");
      db.Execute("INSERT INTO family_trees(long_id,name,description,root_person_id,status,created_at,updated_at)VALUES(?,?,'Duplicated from another family tree.',?,'active',?,?)", longId, name, QI(tree, "root_person_id") > 0 ? (object)QI(tree, "root_person_id") : null, Now(), Now());
      int newId = (int)LastId(db);
      Dictionary<int, int> unionMap = new Dictionary<int, int>();
      foreach (Dictionary<string, string> u in db.Query("SELECT * FROM relationships WHERE family_tree_id=? ORDER BY id", oldId)) {
        db.Execute("INSERT INTO relationships(family_tree_id,person1_id,person2_id,relationship_type,start_date,end_date,is_current,sibling_person_id,created_at)VALUES(?,?,?,?,?,?,?,?,?)", newId, QI(u, "person1_id"), QI(u, "person2_id"), Q(u, "relationship_type"), CleanDate(Q(u, "start_date")), CleanDate(Q(u, "end_date")), QI(u, "is_current"), QI(u, "sibling_person_id") > 0 ? (object)QI(u, "sibling_person_id") : null, Now());
        unionMap[QI(u, "id")] = (int)LastId(db);
      }
      int primary; if (unionMap.TryGetValue(QI(tree, "primary_relationship_id"), out primary)) db.Execute("UPDATE family_trees SET primary_relationship_id=? WHERE id=?", primary, newId);
      db.Execute("INSERT INTO parent_child(family_tree_id,child_id,parent_id,created_at) SELECT ?,child_id,parent_id,? FROM parent_child WHERE family_tree_id=? ORDER BY id", newId, Now(), oldId);
      foreach (Dictionary<string, string> s in db.Query("SELECT * FROM family_tree_person_settings WHERE family_tree_id=?", oldId)) {
        int preferred; unionMap.TryGetValue(QI(s, "preferred_relationship_id"), out preferred);
        db.Execute("INSERT INTO family_tree_person_settings(family_tree_id,person_id,sibling_birth_order,preferred_relationship_id,created_at,updated_at)VALUES(?,?,?,?,?,?)", newId, QI(s, "person_id"), QI(s, "sibling_birth_order") > 0 ? (object)QI(s, "sibling_birth_order") : null, preferred > 0 ? (object)preferred : null, Now(), Now());
      }
      return Ok("Tree duplicated.", new Dictionary<string, object> { { "tree_long_id", longId } });
    }

    // Port of tree_relationship_actions.php (list_tree_couples, promote_partner, change_partner, move_child).
    private Dictionary<string, object> Rearrange(SqliteDb db, TreeRequest r, string action) {
      Dictionary<string, string> tree = RequireTree(db, r);
      int treeId = QI(tree, "id");
      List<Dictionary<string, string> > rows = db.Query("SELECT r.*,trim(coalesce(p1.first_name,'')||' '||coalesce(p1.last_name,'')) AS first_name,trim(coalesce(p2.first_name,'')||' '||coalesce(p2.last_name,'')) AS second_name FROM relationships r JOIN persons p1 ON p1.id=r.person1_id JOIN persons p2 ON p2.id=r.person2_id WHERE r.family_tree_id=? AND r.relationship_type IN ('spouse','partner') ORDER BY r.id", treeId);
      Func<Dictionary<string, string>, int[]> pair = row => { int[] ids = { QI(row, "person1_id"), QI(row, "person2_id") }; Array.Sort(ids); return ids; };
      Func<int[], string> key = ids => N(ids[0]) + "," + N(ids[1]);
      if (action == "list_tree_couples") {
        string search = r["search"].Trim().ToLowerInvariant(); if (search.Length > 100) search = search.Substring(0, 100);
        int source = r.Int("source_relationship_id"), child = r.Int("person_id");
        string sourcePair = rows.Where(row => QI(row, "id") == source).Select(row => key(pair(row))).FirstOrDefault() ?? "";
        HashSet<string> seen = new HashSet<string>();
        List<Dictionary<string, object> > couples = new List<Dictionary<string, object> >();
        foreach (Dictionary<string, string> row in rows) {
          int[] ids = pair(row); string k = key(ids);
          if (k == sourcePair || seen.Contains(k) || ids.Contains(child)) continue;
          seen.Add(k);
          string label = Q(row, "first_name").Trim() + " + " + Q(row, "second_name").Trim();
          if (search != "" && !label.ToLowerInvariant().Contains(search)) continue;
          couples.Add(new Dictionary<string, object> { { "id", QI(row, "id") }, { "label", label }, { "person_ids", k } });
          if (couples.Count >= 50) break;
        }
        return Ok("", new Dictionary<string, object> { { "couples", couples } });
      }
      Dictionary<int, Dictionary<string, string> > unions = rows.ToDictionary(row => QI(row, "id"));
      int sourceId = r.Int("source_relationship_id"), targetId = r.Int("relationship_id");
      Dictionary<string, string> sourceRow, targetRow;
      const string Changed = "This couple changed. Refresh the tree and try again.";
      if (!unions.TryGetValue(sourceId, out sourceRow) || !unions.TryGetValue(targetId, out targetRow) || sourceId == targetId) throw Fail(Changed);
      int[] sourcePairIds = pair(sourceRow), targetPairIds = pair(targetRow);
      if (r["source_person_ids"] != key(sourcePairIds) || r["target_person_ids"] != key(targetPairIds)) throw Fail(Changed);
      if (key(sourcePairIds) == key(targetPairIds) || targetPairIds[0] == targetPairIds[1]) throw Fail("Choose a different couple.");
      List<Dictionary<string, string> > links = db.Query("SELECT id,parent_id,child_id FROM parent_child WHERE family_tree_id=? ORDER BY id", treeId);
      Dictionary<int, List<int> > children = new Dictionary<int, List<int> >(), parents = new Dictionary<int, List<int> >();
      foreach (Dictionary<string, string> link in links) {
        List<int> list;
        if (!children.TryGetValue(QI(link, "parent_id"), out list)) children[QI(link, "parent_id")] = list = new List<int>(); list.Add(QI(link, "child_id"));
        if (!parents.TryGetValue(QI(link, "child_id"), out list)) parents[QI(link, "child_id")] = list = new List<int>(); list.Add(QI(link, "parent_id"));
      }
      Func<IEnumerable<int>, HashSet<int> > descendants = start => {
        HashSet<int> seen = new HashSet<int>(start); Queue<int> queue = new Queue<int>(seen);
        while (queue.Count > 0) { List<int> list; if (children.TryGetValue(queue.Dequeue(), out list)) foreach (int id in list) if (seen.Add(id)) queue.Enqueue(id); }
        return seen;
      };
      if (action == "promote_partner") {
        int anchor = r.Int("anchor_person_id");
        if (!PersonExists(db, anchor)) throw Fail("Relationship not found.");
        // Any promoted union must still be connected to this displayed couple through partner links.
        Dictionary<int, List<int> > neighbors = new Dictionary<int, List<int> >();
        foreach (Dictionary<string, string> u in rows) { int[] ids = pair(u); foreach (int i in new[] { 0, 1 }) { List<int> list; if (!neighbors.TryGetValue(ids[i], out list)) neighbors[ids[i]] = list = new List<int>(); list.Add(ids[1 - i]); } }
        HashSet<int> connected = new HashSet<int>(sourcePairIds); Queue<int> queue = new Queue<int>(sourcePairIds);
        while (queue.Count > 0) { List<int> list; if (neighbors.TryGetValue(queue.Dequeue(), out list)) foreach (int id in list) if (connected.Add(id)) queue.Enqueue(id); }
        if (!connected.Contains(targetPairIds[0]) || !connected.Contains(targetPairIds[1])) throw Fail("Relationship not found.");
        int preferred = QI(db.QueryOne("SELECT preferred_relationship_id FROM family_tree_person_settings WHERE family_tree_id=? AND person_id=?", treeId, anchor), "preferred_relationship_id");
        int primary = QI(tree, "primary_relationship_id"), selected;
        if (unions.ContainsKey(preferred)) selected = preferred;
        else selected = rows.Where(u => pair(u).Contains(anchor) && (Q(u, "relationship_type") == "spouse" || QI(u, "id") == primary))
          .OrderByDescending(u => QI(u, "id") == primary).ThenByDescending(u => QI(u, "is_current")).ThenBy(u => QI(u, "id")).Select(u => QI(u, "id")).FirstOrDefault();
        if (selected != sourceId) throw Fail(Changed);
        db.Execute("INSERT INTO family_tree_person_settings(family_tree_id,person_id,preferred_relationship_id,created_at,updated_at)VALUES(?,?,?,?,?) ON CONFLICT(family_tree_id,person_id) DO UPDATE SET preferred_relationship_id=excluded.preferred_relationship_id,updated_at=excluded.updated_at", treeId, anchor, targetId, Now(), Now());
      } else if (action == "change_partner") {
        int[] shared = sourcePairIds.Intersect(targetPairIds).ToArray(), external = targetPairIds.Except(sourcePairIds).ToArray(), other = sourcePairIds.Except(targetPairIds).ToArray();
        if (shared.Length != 1 || external.Length != 1 || other.Length != 1) throw Fail(Changed);
        int oldParent = shared[0], partner = external[0], newParent = other[0];
        int[] newPair = { partner, newParent }; Array.Sort(newPair);
        if (rows.Any(u => key(pair(u)) == key(newPair))) throw Fail("These people already have a relationship.");
        List<int> moving = new List<int>();
        List<int> partnerChildren; children.TryGetValue(partner, out partnerChildren);
        foreach (int child in (partnerChildren ?? new List<int>()).Distinct()) { int[] ids = parents[child].Distinct().OrderBy(i => i).ToArray(); if (ids.Length == 2 && key(ids) == key(targetPairIds)) moving.Add(child); }
        if (descendants(new[] { partner }.Concat(moving)).Contains(newParent)) throw Fail("A child cannot be moved under themselves or their descendants.");
        db.Execute("UPDATE relationships SET person1_id=CASE WHEN person1_id=?1 THEN ?2 ELSE person1_id END,person2_id=CASE WHEN person2_id=?1 THEN ?2 ELSE person2_id END,sibling_person_id=CASE WHEN sibling_person_id=?1 THEN ?2 ELSE sibling_person_id END WHERE id=?3 AND family_tree_id=?4", oldParent, newParent, targetId, treeId);
        foreach (int child in moving) db.Execute("UPDATE OR IGNORE parent_child SET parent_id=? WHERE family_tree_id=? AND parent_id=? AND child_id=?", newParent, treeId, oldParent, child);
      } else if (action == "move_child") {
        int child = r.Int("person_id");
        if (!PersonExists(db, child)) throw Fail("Person not found.");
        List<int> current; parents.TryGetValue(child, out current);
        int[] parentIds = (current ?? new List<int>()).Distinct().OrderBy(i => i).ToArray();
        string expectedRaw = r.Has("expected_parent_ids") ? r["expected_parent_ids"] : key(sourcePairIds);
        if (!Regex.IsMatch(expectedRaw, "^[1-9][0-9]*(,[1-9][0-9]*)?$")) throw Fail("Invalid request.");
        int[] expected = expectedRaw.Split(',').Select(s => Int32.Parse(s, CultureInfo.InvariantCulture)).OrderBy(i => i).ToArray();
        if (!parentIds.SequenceEqual(expected) || parentIds.Length == 0 || parentIds.Except(sourcePairIds).Any()) throw Fail("This child’s parents changed. Refresh the tree and try again.");
        HashSet<int> below = descendants(new[] { child });
        if (below.Contains(targetPairIds[0]) || below.Contains(targetPairIds[1])) throw Fail("A child cannot be moved under themselves or their descendants.");
        db.Execute("DELETE FROM parent_child WHERE family_tree_id=? AND child_id=? AND parent_id IN (?,?)", treeId, child, sourcePairIds[0], sourcePairIds[1]);
        foreach (int parent in targetPairIds) AddParentLink(db, treeId, child, parent);
      }
      TouchTree(db, treeId);
      return Success(db, action == "promote_partner" ? "Main partner updated." : action == "change_partner" ? "Partner changed." : "Child moved.", Q(tree, "long_id"), r.Has("person_id") ? r.Int("person_id") : r.Int("anchor_person_id"), false, r);
    }

    private Dictionary<string, object> UpdatePartial(SqliteDb db, TreeRequest r) {
      Dictionary<string, string> row = PartialList(db, true).FirstOrDefault(p => FixedEquals(Q(p, "long_id"), r["partial_id"].Trim()));
      if (row == null) throw Fail("The Partial Tree was not found.");
      List<string> updates = new List<string>(); List<object> values = new List<object>();
      if (r.Has("name")) { string name = Regex.Replace(r["name"], "<[^>]*>", "").Trim(); if (name.Length > 140) name = name.Substring(0, 140); if (name == "") throw Fail("Enter a Partial Tree name."); updates.Add("name=?"); values.Add(name); }
      int anchorId = r.Has("anchor_relationship_id") ? r.Int("anchor_relationship_id") : 0;
      if (r["anchor_ref"] != "") { int[] anchorRef = TempDecode(r["anchor_ref"]); if (anchorRef == null) throw Fail("The selected parent couple reference is invalid or expired."); anchorId = anchorRef[1]; }
      if (r.Has("anchor_relationship_id") || r["anchor_ref"] != "") {
        Dictionary<string, string> anchor = PartialAnchor(db, QI(row, "family_tree_id"), anchorId);
        if (anchor == null) throw Fail("Choose a valid parent couple in the canonical tree.");
        updates.Add("anchor_relationship_id=?"); values.Add(QI(anchor, "id")); updates.Add("anchor_person_id=?"); values.Add(QI(anchor, "person1_id")); updates.Add("anchor_partner_id=?"); values.Add(QI(anchor, "person2_id"));
        updates.Add("status='active'"); updates.Add("disabled_reason=NULL");
      }
      string status = Q(row, "status");
      if (r.Has("status")) {
        status = new[] { "active", "archived", "deleted" }.Contains(r["status"]) ? r["status"] : "active";
        updates.Add("status=?"); values.Add(status);
        if (status == "archived") { updates.Add("archived_at=?"); values.Add(Now()); }
        if (status == "deleted") { updates.Add("deleted_at=?"); values.Add(Now()); }
      }
      if (r.Has("visibility")) updates.Add("updated_at=updated_at"); // ponytail: one local user, so every Partial Tree is private.
      if (updates.Count == 0) throw Fail("No Partial Tree changes were provided.");
      updates.Add("updated_at=?"); values.Add(Now()); values.Add(QI(row, "id"));
      db.Execute("UPDATE family_tree_partial_views SET " + String.Join(",", updates) + " WHERE id=?", values.ToArray());
      return Ok("Partial Tree updated.", new Dictionary<string, object> { { "long_id", Q(row, "long_id") }, { "status", status } });
    }

    // ---------- person details ----------
    // Profile fields shared by get_person and the persons directory details popup.
    private static Dictionary<string, object> BasePerson(SqliteDb db, int personId) {
      Dictionary<string, string> row = db.QueryOne("SELECT id,long_id,first_name,last_name,gender,image_name,birth_date,death_date,is_deceased,visibility,marital_status,phone1,phone2,email1,email2,address,birth_place,nationality,blood_group,education_level,religion,website,updated_at FROM persons WHERE id=?", personId);
      if (row == null) throw Fail("Person not found.");
      Dictionary<string, object> person = new Dictionary<string, object>();
      foreach (var pair in row) person[pair.Key] = pair.Value == "" ? null : pair.Value;
      string gender = Q(row, "gender") == "" ? "M" : Q(row, "gender");
      person["id"] = personId; person["family_long_id"] = "local"; person["death_date"] = CleanDate(Q(row, "death_date")); person["is_deceased"] = Deceased(row);
      person["creator_user_name"] = ""; person["updater_user_name"] = ""; person["username"] = "";
      person["image_url"] = PersonImageUrl(Q(row, "image_name"), gender, ""); person["image_sm_url"] = PersonImageUrl(Q(row, "image_name"), gender, "sm"); person["image_lg_url"] = PersonImageUrl(Q(row, "image_name"), gender, "lg");
      person["gallery_preview"] = GallerySummary(db, personId, 5); person["gallery_can_manage"] = true;
      return person;
    }

    private Dictionary<string, object> GetPerson(SqliteDb db, TreeRequest r) {
      Dictionary<string, string> tree = TreeByLongId(db, r["tree_long_id"]);
      int personId = r.Int("person_id"), treeId = QI(tree, "id");
      if (tree == null || personId == 0 || !InTree(db, treeId, personId)) throw Fail("Person not found.");
      Dictionary<string, object> person = BasePerson(db, personId);
      // tree_person_tree_context
      person["has_children"] = db.QueryOne("SELECT 1 AS x FROM parent_child WHERE family_tree_id=? AND parent_id=?", treeId, personId) != null;
      person["is_child"] = db.QueryOne("SELECT 1 AS x FROM parent_child WHERE family_tree_id=? AND child_id=?", treeId, personId) != null;
      Dictionary<string, string> spouse = SpouseRelationship(db, treeId, personId);
      int siblingId = spouse == null ? 0 : QI(spouse, "sibling_person_id") > 0 ? QI(spouse, "sibling_person_id") : QI(spouse, "person1_id");
      person["has_spouse"] = spouse != null; person["sibling_editable"] = spouse != null; person["sibling_value"] = spouse == null || siblingId == personId ? "yes" : "no";
      int order = QI(db.QueryOne("SELECT sibling_birth_order FROM family_tree_person_settings WHERE family_tree_id=? AND person_id=?", treeId, personId), "sibling_birth_order");
      person["sibling_birth_order"] = order > 0 ? (object)order : null;
      int root = QI(tree, "root_person_id"), primary = QI(tree, "primary_relationship_id");
      Dictionary<string, string> main = db.QueryOne("SELECT id,relationship_type FROM relationships WHERE family_tree_id=? AND relationship_type IN ('spouse','partner') AND ((person1_id=?2 AND person2_id=?3) OR (person2_id=?2 AND person1_id=?3)) ORDER BY id LIMIT 1", treeId, root, personId);
      int mainId = QI(main, "id");
      bool selected = mainId > 0 && mainId == primary;
      if (mainId > 0 && primary <= 0 && Q(main, "relationship_type") == "spouse")
        selected = mainId == QI(db.QueryOne("SELECT id FROM relationships WHERE family_tree_id=? AND relationship_type='spouse' AND (person1_id=?2 OR person2_id=?2) ORDER BY is_current DESC,id LIMIT 1", treeId, root), "id");
      person["main_relationship_id"] = mainId; person["main_relationship_eligible"] = mainId > 0 && root != personId; person["main_relationship_selected"] = selected;
      person["relationship_creator_user_name"] = ""; person["relationship_updater_user_name"] = "";
      person["has_deceased_partner"] = db.Query("SELECT p.is_deceased,p.death_date FROM relationships r JOIN persons p ON p.id=CASE WHEN r.person1_id=?2 THEN r.person2_id ELSE r.person1_id END WHERE r.family_tree_id=?1 AND r.relationship_type IN ('spouse','partner') AND (r.person1_id=?2 OR r.person2_id=?2)", treeId, personId).Any(Deceased);
      List<Dictionary<string, object> > spouses = new List<Dictionary<string, object> >(), partners = new List<Dictionary<string, object> >();
      HashSet<int> seen = new HashSet<int>();
      foreach (Dictionary<string, string> rel in db.Query("SELECT r.relationship_type,p.id,p.first_name,p.last_name,p.gender,p.image_name FROM relationships r JOIN persons p ON p.id=CASE WHEN r.person1_id=?2 THEN r.person2_id ELSE r.person1_id END WHERE r.family_tree_id=?1 AND (r.person1_id=?2 OR r.person2_id=?2) AND r.relationship_type IN ('spouse','partner') ORDER BY r.relationship_type='spouse' DESC,r.is_current DESC,r.id", treeId, personId)) {
        if (QI(rel, "id") == personId || !seen.Add(QI(rel, "id"))) continue;
        (Q(rel, "relationship_type") == "spouse" ? spouses : partners).Add(new Dictionary<string, object> { { "id", QI(rel, "id") }, { "name", (Q(rel, "first_name") + " " + Q(rel, "last_name")).Trim() }, { "gender", Q(rel, "gender") }, { "image_url", PersonImageUrl(Q(rel, "image_name"), Q(rel, "gender"), "sm") } });
      }
      person["spouses"] = spouses; person["partners"] = partners;
      return Ok("Person loaded.", new Dictionary<string, object> { { "person", person } });
    }

    // ---------- statistics ----------
    private static int? AgeYears(Dictionary<string, string> p) {
      DateTime birth, end;
      if (!DateTime.TryParseExact(Q(p, "birth_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out birth)) return null;
      if (Deceased(p)) { if (!DateTime.TryParseExact(Q(p, "death_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end)) return null; }
      else end = DateTime.UtcNow.Date;
      if (end < birth) return null;
      int years = end.Year - birth.Year;
      if (end.Month < birth.Month || (end.Month == birth.Month && end.Day < birth.Day)) years--;
      return years;
    }

    private Dictionary<string, object> StatPersons(SqliteDb db, int treeId, List<int> ids, TreeRequest r) {
      string category = r.Has("category") ? r["category"] : "total";
      if (!StatCategories.Contains(category)) throw Fail("Invalid request. Please refresh and try again.", 400);
      HashSet<int> spouses = SpouseTargets(db, treeId);
      string search = r["search"].Trim().ToLowerInvariant(), life = r["life"], marital = r["marital"], gender = r["gender"], age = r["age"];
      int offset = Math.Max(0, Math.Min(1000000, r.Int("offset")));
      IEnumerable<Dictionary<string, string> > people = ScopePeople(db, treeId, ids).Where(p => InCategory(p, category, spouses));
      if (life == "alive") people = people.Where(p => !Deceased(p)); else if (life == "deceased") people = people.Where(Deceased);
      if (new[] { "single", "married", "divorced", "widowed" }.Contains(marital)) people = people.Where(p => Q(p, "marital_status") == marital);
      if (gender == "M" || gender == "F") people = people.Where(p => Q(p, "gender") == gender);
      if (age != "") people = people.Where(p => {
        int? years = AgeYears(p);
        switch (age) {
          case "under5": return years >= 0 && years <= 4; case "5to9": return years >= 5 && years <= 9; case "10to17": return years >= 10 && years <= 17;
          case "18to29": return years >= 18 && years <= 29; case "30to49": return years >= 30 && years <= 49; case "50plus": return years >= 50;
          case "unknown": return CleanDate(Q(p, "birth_date")) == null; default: return true;
        }
      });
      if (search != "") people = people.Where(p => (Q(p, "first_name") + " " + Q(p, "last_name")).ToLowerInvariant().Contains(search));
      List<Dictionary<string, string> > page = people.Skip(offset).Take(13).ToList();
      List<Dictionary<string, object> > rows = page.Take(12).Select(p => new Dictionary<string, object> { { "id", QI(p, "id") }, { "name", (Q(p, "first_name") + " " + Q(p, "last_name")).Trim() }, { "gender", Q(p, "gender") }, { "is_deceased", Deceased(p) }, { "image_sm_url", PersonImageUrl(Q(p, "image_name"), Q(p, "gender"), "sm") } }).ToList();
      return Ok("Available people loaded.", new Dictionary<string, object> { { "people", rows }, { "has_more", page.Count > 12 }, { "next_offset", offset + rows.Count } });
    }

    private const string PersonIcon = "<svg class='svg-icon' viewBox='0 0 20 20'><path d=\"M10,10.9c2.373,0,4.303-1.932,4.303-4.306c0-2.372-1.93-4.302-4.303-4.302S5.696,4.223,5.696,6.594C5.696,8.969,7.627,10.9,10,10.9z M10,3.331c1.801,0,3.266,1.463,3.266,3.263c0,1.802-1.465,3.267-3.266,3.267c-1.8,0-3.265-1.465-3.265-3.267C6.735,4.794,8.2,3.331,10,3.331z\"></path><path d=\"M10,12.503c-4.418,0-7.878,2.058-7.878,4.685c0,0.288,0.231,0.52,0.52,0.52c0.287,0,0.519-0.231,0.519-0.52c0-1.976,3.132-3.646,6.84-3.646c3.707,0,6.838,1.671,6.838,3.646c0,0.288,0.234,0.52,0.521,0.52s0.52-0.231,0.52-0.52C17.879,14.561,14.418,12.503,10,12.503z\"></path></svg>";
    private const string CalendarIcon = "<svg class='svg-icon' viewBox='0 0 20 20'><path d=\"M16.557,4.467h-1.64v-0.82c0-0.225-0.183-0.41-0.409-0.41c-0.226,0-0.41,0.185-0.41,0.41v0.82H5.901v-0.82c0-0.225-0.185-0.41-0.41-0.41c-0.226,0-0.41,0.185-0.41,0.41v0.82H3.442c-0.904,0-1.64,0.735-1.64,1.639v9.017c0,0.904,0.736,1.64,1.64,1.64h13.114c0.904,0,1.64-0.735,1.64-1.64V6.106C18.196,5.203,17.461,4.467,16.557,4.467 M17.377,15.123c0,0.453-0.366,0.819-0.82,0.819H3.442c-0.453,0-0.82-0.366-0.82-0.819V8.976h14.754V15.123z M17.377,8.156H2.623V6.106c0-0.453,0.367-0.82,0.82-0.82h1.639v1.23c0,0.225,0.184,0.41,0.41,0.41c0.225,0,0.41-0.185,0.41-0.41v-1.23h8.196v1.23c0,0.225,0.185,0.41,0.41,0.41c0.227,0,0.409-0.185,0.409-0.41v-1.23h1.64c0.454,0,0.82,0.367,0.82,0.82V8.156z\"></path></svg>";

    // Port of tree_insights_html with the counts computed in C# (SQLite has no TIMESTAMPDIFF).
    private static string InsightsHtml(SqliteDb db, int treeId, List<int> ids) {
      List<Dictionary<string, string> > people = ScopePeople(db, treeId, ids);
      HashSet<string> inLaws = new HashSet<string>(db.Query("SELECT person1_id,person2_id,coalesce(sibling_person_id,person1_id) AS sibling FROM relationships WHERE family_tree_id=? AND relationship_type='spouse'", treeId).SelectMany(u => new[] { Q(u, "person1_id"), Q(u, "person2_id") }.Where(id => id != Q(u, "sibling"))));
      List<int> ages = people.Select(AgeYears).Where(a => a.HasValue && a.Value <= 130).Select(a => a.Value).ToList();
      Dictionary<string, int> v = new Dictionary<string, int> {
        { "total", people.Count }, { "women", people.Count(p => Q(p, "gender") == "F") }, { "men", people.Count(p => Q(p, "gender") == "M") }, { "deceased", people.Count(Deceased) },
        { "married", people.Count(p => Q(p, "marital_status") == "married") }, { "singles", people.Count(p => Q(p, "marital_status") == "single") },
        { "photos", people.Count(p => Q(p, "image_name") != "") }, { "birthdays", people.Count(p => Q(p, "birth_date") != "" && Q(p, "birth_date") != "0000-00-00") },
        { "in_laws", people.Count(p => inLaws.Contains(Q(p, "id"))) },
      };
      v["living"] = Math.Max(0, v["total"] - v["deceased"]);
      Func<string, string> t = TreeText.H;
      Func<double, string> f1 = value => value.ToString("0.#", CultureInfo.InvariantCulture);
      StringBuilder html = new StringBuilder("<div class=\"tree_insights_numbers\">");
      foreach (string[] item in new[] { new[] { "total", "Total Members", "teal", "p" }, new[] { "women", "Women", "pink", "p" }, new[] { "men", "Men", "blue", "p" }, new[] { "deceased", "Deceased", "gray", "p" }, new[] { "in_laws", "In-laws", "purple", "p" }, new[] { "married", "Married", "green", "p" }, new[] { "singles", "Single", "blue", "p" }, new[] { "average_age", "Average age", "amber", "c" } }) {
        string value = item[0] == "average_age" ? (ages.Count == 0 ? "-" : ages.Average().ToString("0.0", CultureInfo.InvariantCulture)) : N(v[item[0]]);
        html.Append("<div class=\"tree_insights_tint_" + item[2] + "\"><span class=\"tree_insights_icon\">" + (item[3] == "c" ? CalendarIcon : PersonIcon) + "</span><div><span>" + t(item[1]) + "</span><strong>" + value + "</strong></div></div>");
      }
      html.Append("</div><p class=\"tree_insights_note\">" + t("Age is measured today for living people and at death for deceased people. Only valid birthdates are included.") + "</p><div class=\"tree_insights_charts\">");
      foreach (string[] chart in new[] { new[] { "men", "Men", "women", "Women", "blue", "pink" }, new[] { "living", "Living", "deceased", "Deceased", "green", "gray" }, new[] { "photos", "With profile image", "", "Without profile image", "purple", "gray" }, new[] { "birthdays", "With birthdate", "", "Without birthdate", "amber", "gray" } }) {
        int value = v[chart[0]], other = chart[2] != "" ? v[chart[2]] : Math.Max(0, v["total"] - value), denominator = value + other;
        double percent = denominator > 0 ? Math.Round(value * 100.0 / denominator, 1) : 0, otherPercent = denominator > 0 ? Math.Round(other * 100.0 / denominator, 1) : 0;
        string first = t(chart[1]) + ": " + N(value) + " (" + f1(percent) + "%)", second = t(chart[3]) + ": " + N(other) + " (" + f1(otherPercent) + "%)";
        bool completeness = chart[2] == "";
        html.Append("<section class=\"tree_insights_tint_" + chart[4] + "\"><h3>" + t(chart[1]) + (completeness ? "" : " / " + t(chart[3])) + "</h3>");
        if (completeness) {
          html.Append("<div class=\"tree_insights_completion\"><strong>" + f1(percent) + "<small>%</small></strong><span>" + N(value) + " / " + N(v["total"]) + "</span></div>");
          html.Append("<div class=\"tree_insights_bar\" role=\"progressbar\" tabindex=\"0\" aria-label=\"" + t(chart[1]) + "\" aria-valuemin=\"0\" aria-valuemax=\"100\" aria-valuenow=\"" + f1(percent) + "\" aria-valuetext=\"" + first + "; " + second + "\" data-insight-value=\"" + first + "; " + second + "\"><span style=\"width:" + f1(percent) + "%\"></span></div>");
        } else {
          html.Append("<div class=\"tree_insights_ring\"><svg viewBox=\"0 0 120 120\"><circle class=\"tree_insights_track\" cx=\"60\" cy=\"60\" r=\"42\" stroke-width=\"18\" fill=\"none\"></circle>");
          double circumference = 2 * Math.PI * 42, offset = 0;
          foreach (var segment in new[] { new { Count = value, Text = first, Tint = chart[4] }, new { Count = other, Text = second, Tint = chart[5] } }) {
            if (segment.Count == 0) continue;
            double length = (double)segment.Count / denominator * circumference;
            html.Append("<circle class=\"tree_insights_segment tree_insights_tint_" + segment.Tint + "\" cx=\"60\" cy=\"60\" r=\"42\" stroke-width=\"18\" fill=\"none\" stroke-dasharray=\"" + Math.Round(length, 3).ToString(CultureInfo.InvariantCulture) + " " + Math.Round(circumference - length, 3).ToString(CultureInfo.InvariantCulture) + "\" stroke-dashoffset=\"" + Math.Round(-offset, 3).ToString(CultureInfo.InvariantCulture) + "\" tabindex=\"0\" role=\"img\" aria-label=\"" + segment.Text + "\" data-insight-value=\"" + segment.Text + "\"></circle>");
            offset += length;
          }
          html.Append("</svg><div class=\"tree_insights_center\"><strong>" + N(denominator) + "</strong><span>" + t("Total") + "</span></div></div>");
        }
        html.Append("<div class=\"tree_insights_legend\">");
        foreach (var legend in new[] { new { Label = chart[1], Count = value, Ratio = percent, Tint = chart[4] }, new { Label = chart[3], Count = other, Ratio = otherPercent, Tint = chart[5] } })
          html.Append("<div class=\"tree_insights_tint_" + legend.Tint + "\"><i aria-hidden=\"true\"></i><span>" + t(legend.Label) + "</span><strong>" + N(legend.Count) + " <small>(" + f1(legend.Ratio) + "%)</small></strong></div>");
        html.Append("</div></section>");
      }
      return html.Append("</div>").ToString();
    }

    // Port of tree_important_dates_page/html. Leap-day anniversaries fall on February 28 in other years.
    private static Dictionary<string, object> ImportantDates(SqliteDb db, int treeId, List<int> ids, TreeRequest r) {
      DateTime today = DateTime.Today;
      string search = r["search"].Trim().ToLowerInvariant(), category = r["category"] == "birthday" || r["category"] == "death" ? r["category"] : "";
      int month = Math.Max(0, Math.Min(12, r.Int("month"))), page = Math.Max(1, Math.Min(100000, r.Has("page") ? r.Int("page") : 1));
      Func<DateTime, int, DateTime> occurrence = (source, year) => new DateTime(year, source.Month, Math.Min(source.Day, DateTime.DaysInMonth(year, source.Month)));
      var items = new List<Tuple<int, string, string, DateTime, DateTime> >();
      foreach (Dictionary<string, string> p in ScopePeople(db, treeId, ids)) {
        string name = (Q(p, "first_name") + " " + Q(p, "last_name")).Trim();
        if (search != "" && !name.ToLowerInvariant().Contains(search)) continue;
        foreach (string kind in new[] { "birthday", "death" }) {
          if (category != "" && category != kind) continue;
          DateTime source;
          if (!DateTime.TryParseExact(Q(p, kind == "birthday" ? "birth_date" : "death_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out source) || source > today || (month > 0 && source.Month != month)) continue;
          DateTime next = occurrence(source, today.Year); if (next < today) next = occurrence(source, today.Year + 1);
          items.Add(Tuple.Create(QI(p, "id"), name, kind, source, next));
        }
      }
      IEnumerable<Tuple<int, string, string, DateTime, DateTime> > sorted = r["sort"] == "name" ? items.OrderBy(i => i.Item2, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Item5).ThenBy(i => i.Item1).ThenBy(i => i.Item3)
        : r["sort"] == "date" ? items.OrderBy(i => i.Item4.Month).ThenBy(i => i.Item4.Day).ThenBy(i => i.Item2, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Item1).ThenBy(i => i.Item3)
        : items.OrderBy(i => i.Item5).ThenBy(i => i.Item2, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Item1).ThenBy(i => i.Item3);
      int pages = Math.Max(1, (items.Count + 29) / 30); page = Math.Min(page, pages);
      Func<string, string> t = TreeText.H;
      StringBuilder html = new StringBuilder("<div class=\"tree_dates_list\">");
      List<Tuple<int, string, string, DateTime, DateTime> > shown = sorted.Skip((page - 1) * 30).Take(30).ToList();
      foreach (var item in shown)
        html.Append("<article class=\"tree_dates_item\"><div class=\"tree_dates_day\" aria-hidden=\"true\"><strong>" + item.Item5.ToString("dd", CultureInfo.InvariantCulture) + "</strong><span>" + item.Item5.ToString("MM/yyyy", CultureInfo.InvariantCulture) + "</span></div><div><strong>" + E(item.Item2) + "</strong><span>" + t(item.Item3 == "birthday" ? "Birthday" : "Death anniversary") + " · " + item.Item4.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + "</span><small>" + t("Next occurrence") + ": <time datetime=\"" + item.Item5.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\">" + item.Item5.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + "</time></small></div></article>");
      if (shown.Count == 0) html.Append("<p class=\"tree_dates_empty\">" + t("No important dates match these filters.") + "</p>");
      html.Append("</div><nav class=\"tree_dates_pagination\" aria-label=\"" + t("Pagination") + "\">");
      if (page > 1) html.Append("<button type=\"button\" class=\"button1 secondary\" data-dates-page=\"" + N(page - 1) + "\">" + t("Previous") + "</button>");
      string pageLabel = TreeText.T("Page %s of %s"); int split = pageLabel.IndexOf("%s", StringComparison.Ordinal);
      html.Append("<span>" + E(split >= 0 ? pageLabel.Substring(0, split) + N(page) + pageLabel.Substring(split + 2).Replace("%s", N(pages)) : pageLabel) + "</span>");
      if (page < pages) html.Append("<button type=\"button\" class=\"button1 secondary\" data-dates-page=\"" + N(page + 1) + "\">" + t("Next") + "</button>");
      html.Append("</nav>");
      return new Dictionary<string, object> { { "html", html.ToString() }, { "total", items.Count }, { "page", page }, { "pages", pages } };
    }
  }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// Trees page, tree picker, private person gallery, profile photo removal and Partial export/copy/paste.
namespace RacinageFreeDesktop {
  internal sealed partial class LocalStore {
    private const string BackIcon = "<svg viewBox=\"0 0 20 20\" aria-hidden=\"true\"><path d=\"M11.611,10.049l-4.76-4.873c-0.303-0.31-0.297-0.804,0.012-1.105c0.309-0.304,0.803-0.293,1.105,0.012l5.306,5.433c0.304,0.31,0.296,0.805-0.012,1.105L7.83,15.928c-0.152,0.148-0.35,0.223-0.547,0.223c-0.203,0-0.406-0.08-0.559-0.236c-0.303-0.309-0.295-0.803,0.012-1.104L11.611,10.049z\"></path></svg>";
    private static string pageTemplate;

    // ---------- page ----------
    private Dictionary<string, string> SelectedTree(SqliteDb db, string requested) {
      List<Dictionary<string, string> > trees = db.Query("SELECT * FROM family_trees WHERE status='active' ORDER BY id");
      string preferred = Q(db.QueryOne("SELECT setting_value FROM local_plugin_settings WHERE slug='core-tree' AND setting_key='last_tree'"), "setting_value");
      foreach (string candidate in new[] { requested.Trim(), preferred })
        foreach (Dictionary<string, string> tree in trees) if (candidate != "" && FixedEquals(Q(tree, "long_id"), candidate)) return tree;
      return trees.FirstOrDefault();
    }

    internal string TreePageHtml(string requestedTree, string requestedPartial, string requestedTemporary, string focus) {
      using (SqliteDb db = Open()) {
        // A first visit gets one empty tree so the canvas can offer Add First Member.
        if (db.QueryOne("SELECT id FROM family_trees WHERE status='active' LIMIT 1") == null && QI(db.QueryOne("SELECT COUNT(*) AS n FROM family_trees WHERE status IN ('active','archived')"), "n") < TreeLimit)
          db.Execute("INSERT INTO family_trees(long_id,name,description,status,created_at,updated_at)VALUES(?,?,'','active',?,?)", NewId(10), Q(db.QueryOne("SELECT name FROM families WHERE id=1"), "name") == "" ? "Family Tree" : Q(db.QueryOne("SELECT name FROM families WHERE id=1"), "name"), Now(), Now());
        Dictionary<string, string> tree = SelectedTree(db, requestedTree);
        Dictionary<string, string> partial = null;
        if (tree != null && (requestedPartial != "" || requestedTemporary != "")) {
          int[] temp = requestedPartial == "" ? TempDecode(requestedTemporary) : null;
          Dictionary<string, string> partialTree = requestedPartial != ""
            ? PartialList(db, true).Where(p => FixedEquals(Q(p, "long_id"), requestedPartial)).Select(p => db.QueryOne("SELECT * FROM family_trees WHERE id=? AND status='active'", QI(p, "family_tree_id"))).FirstOrDefault()
            : temp != null ? db.QueryOne("SELECT * FROM family_trees WHERE id=? AND status='active'", temp[0]) : null;
          if (partialTree != null) { partial = PartialContext(db, partialTree, requestedPartial, requestedTemporary, false); if (partial != null) tree = partialTree; }
        }
        bool temporary = partial != null && Q(partial, "long_id") == "";
        int root = partial != null ? QI(partial, "anchor_person_id") : QI(tree, "root_person_id");
        FamilyTreeGraph graph = tree != null ? LoadGraph(db, tree) : null;
        int total = tree != null ? ScopePeople(db, QI(tree, "id"), null).Count : 0;
        bool deferred = root > 0 && total >= 150;
        string canvas = "", stats;
        List<int> rendered = null;
        if (root > 0 && !deferred) canvas = CanvasHtml(db, tree, partial, graph, out rendered);
        else if (root <= 0) canvas = "<div class=\"empty_tree_canvas\"><button type=\"button\" class=\"empty_tree_add_first\" id=\"addFirstMemberBtn\" title=\"" + TreeText.H("Add First Member") + "\"><span>+</span></button></div>";
        stats = tree == null ? "" : deferred && partial != null ? TreeText.H("Loading family tree") : StatsHtml(db, QI(tree, "id"), partial != null ? rendered : null);
        if (canvas.StartsWith("<ul>", StringComparison.Ordinal)) canvas = canvas.Substring(4, canvas.Length - 9);
        JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        string i18n = serializer.Serialize(TreeText.All).Replace("</", "<\\/");
        string treeName = Q(tree, "name");
        string partialLabel = temporary ? TreeText.T("Temporary") : TreeText.T("Permanent");
        string view = Q(db.QueryOne("SELECT setting_value FROM local_plugin_settings WHERE slug='core-tree' AND setting_key='view'"), "setting_value") == "vertical" ? "vertical" : "horizontal";
        // Same structure as the hosted stats modal: labelled filters, an age range, More filters and Reset.
        StringBuilder filters = new StringBuilder();
        Action<string, string, string[], bool> statSelect = (filter, label, options, dynamic) => {
          filters.Append("<label class=\"tree_stats_filter\"><span>" + TreeText.H(label) + "</span><select data-stat-filter=\"" + filter + "\"" + (dynamic ? " data-stat-options=\"" + filter + "\"" : "") + ">");
          for (int i = 0; i < options.Length; i += 2) filters.Append("<option value=\"" + options[i] + "\">" + TreeText.H(options[i + 1]) + "</option>");
          filters.Append("</select></label>");
        };
        statSelect("life", "Life status", new[] { "", "All statuses", "alive", "Alive", "deceased", "Deceased" }, false);
        statSelect("gender", "Gender", new[] { "", "All genders", "M", "Male", "F", "Female" }, false);
        statSelect("marital", "Marital status", new[] { "", "Any marital status", "single", "Single", "married", "Married", "divorced", "Divorced", "widowed", "Widowed" }, false);
        statSelect("sibling", "Family role", new[] { "", "Everyone", "yes", "Child of the family", "no", "Married into the family" }, false);
        filters.Append("<fieldset class=\"tree_stats_filter tree_stats_age\"><legend>" + TreeText.H("Age range") + "</legend>"
          + "<input type=\"number\" min=\"0\" max=\"150\" inputmode=\"numeric\" data-stat-filter=\"age_min\" placeholder=\"" + TreeText.H("From") + "\" aria-label=\"" + TreeText.H("Minimum age") + "\">"
          + "<span aria-hidden=\"true\">-</span>"
          + "<input type=\"number\" min=\"0\" max=\"150\" inputmode=\"numeric\" data-stat-filter=\"age_max\" placeholder=\"" + TreeText.H("To") + "\" aria-label=\"" + TreeText.H("Maximum age") + "\"></fieldset>");
        filters.Append("<details class=\"tree_stats_more\"><summary>" + TreeText.H("More filters") + "</summary><div class=\"tree_stats_more_grid\">");
        statSelect("photo", "Profile photo", new[] { "", "Any", "yes", "With a photo", "no", "Without a photo" }, false);
        statSelect("gallery", "Gallery media", new[] { "", "Any", "yes", "Has media", "no", "No media" }, false);
        statSelect("contact", "Contact details", new[] { "", "Any", "yes", "Has phone or email", "no", "No phone or email" }, false);
        statSelect("visibility", "Visibility", new[] { "", "Any", "visible", "Visible", "hidden", "Hidden" }, false);
        List<string> months = new List<string> { "", "Any month" };
        for (int m = 1; m <= 12; m++) { months.Add(m.ToString(CultureInfo.InvariantCulture)); months.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m)); }
        statSelect("birth_month", "Birth month", months.ToArray(), false);
        foreach (string[] text in new[] { new[] { "religion", "Religion" }, new[] { "nationality", "Nationality" }, new[] { "birth_place", "Birth place" }, new[] { "blood_group", "Blood group" }, new[] { "education_level", "Education" } })
          statSelect(text[0], text[1], new[] { "", "Any" }, true);
        filters.Append("</div></details><button type=\"button\" class=\"tree_secondary_action tree_stats_reset\" data-stats-reset>" + TreeText.H("Reset filters") + "</button>");
        Dictionary<string, string> values = new Dictionary<string, string> {
          { "i18n_json", i18n }, { "crop_json", i18n }, { "gallery_i18n", E(serializer.Serialize(TreeText.All)) },
          { "vertical_class", view == "vertical" ? " vertical" : "" }, { "view", view }, { "view_label", TreeText.H(view == "vertical" ? "Vertical" : "Horizontal") },
          { "horizontal_active", view == "horizontal" ? " active" : "" }, { "vertical_active", view == "vertical" ? " active" : "" },
          { "deferred_attrs", deferred ? "data-tree-deferred=\"true\" aria-busy=\"true\"" : "" },
          { "version", Uri.EscapeDataString(PortablePaths.Version) }, { "tree_long_id", E(Q(tree, "long_id")) },
          { "partial_id", E(partial != null ? Q(partial, "long_id") : "") }, { "partial_temp", E(temporary ? requestedTemporary : "") },
          { "share_type", partial != null && !temporary ? "tree_partial" : "tree" }, { "csrf", E(CsrfToken) }, { "tree_name", E(treeName) },
          { "tree_name_or_none", tree == null ? TreeText.H("No Family Tree") : E(treeName) },
          { "picker_partial_class", partial != null ? " is_partial " + (temporary ? "is_temporary" : "is_permanent") : "" },
          { "picker_label", E(partial != null ? partialLabel + " " + TreeText.T("Partial") + " - " + treeName : (tree == null ? TreeText.T("No Family Tree") : treeName)) },
          { "picker_badge", partial == null ? "" : "<span class=\"partial_tree_badge " + (temporary ? "is_temporary" : "is_permanent") + "\" title=\"" + TreeText.H(temporary ? "Temporary Partial Tree" : "Permanent Partial Tree") + "\">" + TreeText.H("Partial") + "</span><span class=\"family_tree_picker_separator\" aria-hidden=\"true\">-</span>" },
          { "picker_search", "" }, { "picker_rows", PickerRows(db, tree, partial, temporary ? requestedTemporary : "", "") },
          { "loading_state", root > 0 ? "<div class=\"tree_loading_state\" role=\"status\"><span>" + TreeText.H("Loading family tree") + "</span><button type=\"button\" class=\"form_btn\" data-tree-load-retry hidden>" + TreeText.H("Try again") + "</button></div>" : "" },
          { "canvas", canvas == "" ? "" : canvas.StartsWith("<div", StringComparison.Ordinal) ? canvas : "<ul>" + canvas + "</ul>" },
          { "back_icon", BackIcon }, { "zero_media", E(TreeText.T("%s media files").Replace("%s", "0")) }, { "stats_filters", filters.ToString() }, { "stats", stats },
        };
        if (pageTemplate == null) pageTemplate = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tree", "tree_page.html"), Encoding.UTF8);
        return Regex.Replace(pageTemplate, @"\{\{(t:)?([^{}]+)\}\}", m => m.Groups[1].Success ? TreeText.H(m.Groups[2].Value) : values.ContainsKey(m.Groups[2].Value) ? values[m.Groups[2].Value] : "");
      }
    }

    // ---------- Manage Persons (single local family) ----------
    internal string PersonsPageHtml(string query, string view, string pageValue, string requestedTree) {
      using (SqliteDb db = Open()) {
        query = (query ?? "").Trim(); if (query.Length > 100) query = query.Substring(0, 100);
        view = view == "list" ? "list" : "grid";
        int page; Int32.TryParse(pageValue, out page); page = Math.Max(1, page);
        List<Dictionary<string, string> > trees = db.Query("SELECT id,long_id,name,root_person_id FROM family_trees WHERE status='active' ORDER BY id");
        Dictionary<string, string> selected = SelectedTree(db, requestedTree);
        Dictionary<int, List<Dictionary<string, string> > > memberships = new Dictionary<int, List<Dictionary<string, string> > >();
        foreach (Dictionary<string, string> tree in trees)
          foreach (int id in TreeMemberIds(db, QI(tree, "id"))) { List<Dictionary<string, string> > list; if (!memberships.TryGetValue(id, out list)) memberships[id] = list = new List<Dictionary<string, string> >(); list.Add(tree); }
        string like = "%" + query.Replace("=", "==").Replace("%", "=%").Replace("_", "=_") + "%";
        int total = QI(db.QueryOne("SELECT COUNT(*) AS n FROM persons WHERE (coalesce(first_name,'')||' '||coalesce(last_name,'')) LIKE ? ESCAPE '='", like), "n");
        const int size = 24;
        int pages = Math.Max(1, (total + size - 1) / size); page = Math.Min(page, pages);
        List<Dictionary<string, string> > people = db.Query("SELECT * FROM persons WHERE (coalesce(first_name,'')||' '||coalesce(last_name,'')) LIKE ? ESCAPE '=' ORDER BY first_name COLLATE NOCASE,last_name COLLATE NOCASE,id LIMIT ? OFFSET ?", like, size, (page - 1) * size);
        Func<string, string> t = TreeText.H;
        string treeId = Q(selected, "long_id");
        Func<string, int, string> link = (v, p) => "/persons?q=" + Uri.EscapeDataString(query) + "&amp;view=" + v + "&amp;page=" + N(p) + "&amp;tree=" + Uri.EscapeDataString(treeId);
        StringBuilder html = new StringBuilder("<div id=\"main_container\" class=\"free_persons\" data-persons-csrf=\"" + E(CsrfToken) + "\">");
        html.Append("<script>window.RACINAGE_TREE_I18N = " + new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(TreeText.All).Replace("</", "<\\/") + ";</script>");
        html.Append("<section class=\"free_persons_head\"><div><h1>" + t("Manage Persons") + "</h1><p>" + E(TreeText.T("%s persons").Replace("%s", N(total))) + "</p></div><a class=\"tree_secondary_action\" href=\"/trees?tree=" + Uri.EscapeDataString(treeId) + "\">" + t("Back to tree") + "</a></section>");
        html.Append("<form class=\"free_persons_toolbar\" method=\"get\" action=\"/persons\" role=\"search\"><input type=\"hidden\" name=\"view\" value=\"" + view + "\">");
        html.Append("<label><span>" + t("Search people") + "</span><input type=\"search\" name=\"q\" value=\"" + E(query) + "\" placeholder=\"" + t("Search by name") + "\" autocomplete=\"off\"></label>");
        html.Append("<label><span>" + t("Family tree") + "</span><select name=\"tree\" data-free-persons-tree>");
        foreach (Dictionary<string, string> tree in trees)
          html.Append("<option value=\"" + E(Q(tree, "long_id")) + "\"" + (Q(tree, "long_id") == treeId ? " selected" : "") + " data-empty=\"" + (QI(tree, "root_person_id") > 0 ? "false" : "true") + "\">" + E(Q(tree, "name")) + "</option>");
        html.Append("</select></label><button type=\"submit\" class=\"tree_primary_action\">" + t("Apply") + "</button>");
        html.Append("<nav class=\"free_persons_views\" aria-label=\"" + t("Layout") + "\"><a href=\"" + link("grid", page) + "\"" + (view == "grid" ? " aria-current=\"page\"" : "") + ">" + t("Grid") + "</a><a href=\"" + link("list", page) + "\"" + (view == "list" ? " aria-current=\"page\"" : "") + ">" + t("List") + "</a></nav></form>");
        html.Append("<div class=\"free_persons_results is_" + view + "\">");
        foreach (Dictionary<string, string> person in people) {
          int id = QI(person, "id");
          string gender = Q(person, "gender") == "" ? "M" : Q(person, "gender"), name = (Q(person, "first_name") + " " + Q(person, "last_name")).Trim();
          string birth = CleanDate(Q(person, "birth_date")) ?? "", death = CleanDate(Q(person, "death_date")) ?? "";
          string dates = (birth == "" ? "?" : birth.Substring(0, 4)) + " - " + (death != "" ? death.Substring(0, 4) : TreeText.T(Deceased(person) ? "Deceased - date unknown" : "Present"));
          List<Dictionary<string, string> > member; memberships.TryGetValue(id, out member);
          html.Append("<article class=\"free_person_card" + (Q(person, "visibility") == "hidden" ? " is_hidden" : "") + "\" data-person-long-id=\"" + E(Q(person, "long_id")) + "\" data-person-id=\"" + N(id) + "\" data-person-name=\"" + E(name) + "\">");
          html.Append("<img src=\"" + PersonImageUrl(Q(person, "image_name"), gender, "sm") + "\" alt=\"\" width=\"56\" height=\"56\" loading=\"lazy\">");
          html.Append("<div class=\"free_person_text\"><strong>" + E(name) + "</strong><span>" + E(dates) + "</span><small>" + (member == null ? t("Not in a tree") : E(String.Join(", ", member.Select(tree => Q(tree, "name"))))) + "</small></div>");
          html.Append("<div class=\"free_person_actions\"><button type=\"button\" class=\"tree_secondary_action\" data-free-person-details>" + t("Details") + "</button>");
          Dictionary<string, string> placed = member == null ? null : (member.FirstOrDefault(tree => Q(tree, "long_id") == treeId) ?? member[0]);
          if (placed != null) html.Append("<a class=\"tree_secondary_action\" href=\"/trees?tree=" + Uri.EscapeDataString(Q(placed, "long_id")) + "&amp;p=" + N(id) + "\">" + t("Show on tree") + "</a>");
          if (member == null || member.All(tree => Q(tree, "long_id") != treeId)) html.Append("<button type=\"button\" class=\"tree_primary_action\" data-free-person-add>" + t("Add to tree") + "</button>");
          html.Append("</div></article>");
        }
        if (people.Count == 0) html.Append("<p class=\"free_persons_empty\">" + t("No people found.") + "</p>");
        html.Append("</div>");
        if (pages > 1) {
          html.Append("<nav class=\"free_persons_pagination\" aria-label=\"" + t("Pagination") + "\">");
          if (page > 1) html.Append("<a class=\"tree_secondary_action\" href=\"" + link(view, page - 1) + "\">" + t("Previous") + "</a>");
          string label = TreeText.T("Page %s of %s"); int split = label.IndexOf("%s", StringComparison.Ordinal);
          html.Append("<span>" + E(split >= 0 ? label.Substring(0, split) + N(page) + label.Substring(split + 2).Replace("%s", N(pages)) : label) + "</span>");
          if (page < pages) html.Append("<a class=\"tree_secondary_action\" href=\"" + link(view, page + 1) + "\">" + t("Next") + "</a>");
          html.Append("</nav>");
        }
        // The read-only person popup and private gallery reuse the tree page markup, without the edit actions.
        if (pageTemplate == null) pageTemplate = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tree", "tree_page.html"), Encoding.UTF8);
        int start = pageTemplate.IndexOf("<div class=\"racinage_modal_backdrop tree_modal_backdrop\" id=\"treePersonModal\"", StringComparison.Ordinal);
        int end = pageTemplate.IndexOf("<div class=\"racinage_modal_backdrop tree_modal_backdrop\" id=\"treeStatsModal\"", StringComparison.Ordinal);
        string modals = Regex.Replace(pageTemplate.Substring(start, end - start), "<div class=\"tree_person_actions\">.*?</div>", "", RegexOptions.Singleline);
        Dictionary<string, string> values = new Dictionary<string, string> { { "back_icon", BackIcon }, { "csrf", E(CsrfToken) }, { "gallery_i18n", E(new JavaScriptSerializer().Serialize(TreeText.All)) } };
        html.Append(Regex.Replace(modals, @"\{\{(t:)?([^{}]+)\}\}", m => m.Groups[1].Success ? TreeText.H(m.Groups[2].Value) : values.ContainsKey(m.Groups[2].Value) ? values[m.Groups[2].Value] : ""));
        return html.Append("</div>").ToString();
      }
    }

    internal string TreePickerHtml(string requestedTree, string requestedPartial, string requestedTemporary, string query) {
      using (SqliteDb db = Open()) {
        Dictionary<string, string> tree = SelectedTree(db, requestedTree);
        Dictionary<string, string> partial = tree == null ? null : PartialContext(db, tree, requestedPartial, requestedTemporary, false);
        return PickerRows(db, tree, partial, partial != null && Q(partial, "long_id") == "" ? requestedTemporary : "", query);
      }
    }

    // Port of collections_tree_picker_render + collections_tree_rows for the single local family (no collections).
    private string PickerRows(SqliteDb db, Dictionary<string, string> selected, Dictionary<string, string> partialView, string temporaryToken, string query) {
      query = (query ?? "").Trim().ToLowerInvariant();
      List<Dictionary<string, string> > partials = PartialList(db, true);
      StringBuilder html = new StringBuilder("<div class=\"tree_collection_results\" data-tree-picker-page=\"1\" data-tree-picker-query=\"" + E(query) + "\">");
      int shown = 0;
      foreach (Dictionary<string, string> tree in db.Query("SELECT * FROM family_trees WHERE status='active' ORDER BY id")) {
        bool treeMatch = query == "" || Q(tree, "name").ToLowerInvariant().Contains(query);
        string longId = E(Q(tree, "long_id")), name = E(Q(tree, "name")), url = "/trees?collection=all&amp;tree=" + Uri.EscapeDataString(Q(tree, "long_id"));
        if (treeMatch) {
          shown++;
          html.Append("<div class=\"family_tree_picker_row " + (partialView == null && Q(selected, "long_id") == Q(tree, "long_id") ? "active" : "") + "\" data-tree-long-id=\"" + longId + "\" data-tree-name=\"" + name + "\" data-tree-public=\"false\" data-tree-public-url=\"\">");
          html.Append("<button type=\"button\" class=\"family_tree_picker_item\"><span>" + name + "</span></button>");
          html.Append("<button type=\"button\" class=\"family_tree_more_btn\" title=\"" + TreeText.H("Manage Tree") + "\" aria-label=\"" + TreeText.H("Manage Tree") + "\"><span></span><span></span><span></span></button></div>");
        }
        if (temporaryToken != "" && partialView != null && QI(selected, "id") == QI(tree, "id") && (treeMatch || Q(partialView, "name").ToLowerInvariant().Contains(query))) {
          shown++;
          string tempUrl = url + "&amp;partial_temp=" + Uri.EscapeDataString(temporaryToken);
          html.Append("<div class=\"family_tree_picker_row family_tree_picker_partial_row is_temporary active\" data-partial-tree-row data-tree-long-id=\"" + longId + "\" data-partial-temp=\"" + E(temporaryToken) + "\" data-partial-id=\"\" data-partial-name=\"" + TreeText.H("Temporary Partial Tree") + "\" data-partial-visibility=\"private\" data-partial-status=\"active\">");
          html.Append("<a class=\"family_tree_partial_item active\" href=\"" + tempUrl + "\"><span class=\"family_tree_partial_identity\"><span class=\"partial_tree_badge is_temporary\">" + TreeText.H("Partial") + "</span><span aria-hidden=\"true\">-</span><strong>" + name + "</strong></span><small>" + TreeText.H("Temporary") + "</small></a>");
          html.Append("<button type=\"button\" class=\"family_tree_partial_more\" data-partial-tree-manage aria-label=\"" + TreeText.H("Tree Stats") + "\"><svg class=\"menu_item_icon\" viewBox=\"0 0 20 20\"><path d=\"M10.281,1.781C5.75,1.781,2.062,5.469,2.062,10s3.688,8.219,8.219,8.219S18.5,14.531,18.5,10S14.812,1.781,10.281,1.781M10.714,2.659c3.712,0.216,6.691,3.197,6.907,6.908h-6.907V2.659z M10.281,17.354c-4.055,0-7.354-3.298-7.354-7.354c0-3.911,3.067-7.116,6.921-7.341V10c0,0.115,0.045,0.225,0.127,0.305l5.186,5.189C13.863,16.648,12.154,17.354,10.281,17.354M15.775,14.882l-4.449-4.449h6.295C17.522,12.135,16.842,13.684,15.775,14.882\"></path></svg></button></div>");
        }
        foreach (Dictionary<string, string> saved in partials.Where(p => QI(p, "family_tree_id") == QI(tree, "id"))) {
          if (query != "" && !treeMatch && !Q(saved, "name").ToLowerInvariant().Contains(query)) continue;
          shown++;
          string status = Q(saved, "status"), partialName = E(Q(saved, "name"));
          bool active = partialView != null && Q(partialView, "long_id") == Q(saved, "long_id");
          html.Append("<div class=\"family_tree_picker_row family_tree_picker_partial_row is_permanent is_" + E(status) + (active ? " active" : "") + "\" data-partial-tree-row data-tree-long-id=\"" + longId + "\" data-partial-id=\"" + E(Q(saved, "long_id")) + "\" data-partial-name=\"" + partialName + "\" data-partial-visibility=\"private\" data-partial-status=\"" + E(status) + "\" data-partial-public=\"false\" data-partial-public-url=\"\" data-can-public=\"false\" data-can-manage-partial=\"true\">");
          string identity = "<span class=\"family_tree_partial_identity\"><span class=\"partial_tree_badge is_permanent\">" + TreeText.H("Partial") + "</span><span aria-hidden=\"true\">-</span><strong>" + partialName + "</strong></span>";
          if (status == "active") html.Append("<a class=\"family_tree_partial_item" + (active ? " active" : "") + "\" href=\"" + url + "&amp;partial=" + Uri.EscapeDataString(Q(saved, "long_id")) + "\">" + identity + "<small>" + E(Q(tree, "name") + " - " + TreeText.T("Private")) + "</small></a>");
          else html.Append("<span class=\"family_tree_partial_item is_disabled\">" + identity + "<small>" + TreeText.H(status == "archived" ? "Archived" : "Disabled") + "</small></span>");
          html.Append("<button type=\"button\" class=\"family_tree_partial_more\" data-partial-tree-manage aria-label=\"" + TreeText.H("Manage Partial Tree") + "\" title=\"" + TreeText.H("Manage Partial Tree") + "\"><span aria-hidden=\"true\">&#8230;</span></button></div>");
        }
      }
      if (shown == 0) html.Append("<p class=\"tree_collection_empty\" role=\"status\">" + TreeText.H("No family trees found.") + "</p>");
      return html.Append("</div>").ToString();
    }

    // ---------- private person gallery ----------
    private static Dictionary<string, object> GalleryItem(Dictionary<string, string> item) {
      string id = Q(item, "long_id"), url = "/includes/person_gallery.include.php?action=file&media=" + Uri.EscapeDataString(id);
      return new Dictionary<string, object> { { "id", id }, { "type", Q(item, "media_type") == "video" ? "video" : "image" }, { "thumb_url", url + "&variant=thumb" }, { "full_url", url + "&variant=full" }, { "caption", Q(item, "caption") }, { "original_name", Path.GetFileName(Q(item, "original_name")) }, { "created_at", Q(item, "created_at") } };
    }
    private static Dictionary<string, object> GallerySummary(SqliteDb db, int personId, int limit) {
      return new Dictionary<string, object> {
        { "count", QI(db.QueryOne("SELECT COUNT(*) AS n FROM person_gallery_media WHERE person_id=? AND status='active'", personId), "n") },
        { "items", db.Query("SELECT * FROM person_gallery_media WHERE person_id=? AND status='active' ORDER BY created_at DESC,id DESC LIMIT ?", personId, Math.Max(1, Math.Min(8, limit))).Select(GalleryItem).ToList() },
      };
    }

    internal Dictionary<string, object> TreeGalleryAction(TreeRequest r, string csrf) {
      using (SqliteDb db = Open()) {
        Dictionary<string, string> person = db.QueryOne("SELECT id,long_id FROM persons WHERE long_id=?", r["person"]);
        if (person == null) throw Fail("Person not found.", 404);
        int personId = QI(person, "id");
        if (r["action"] == "list")
          return Ok("", new Dictionary<string, object> { { "items", db.Query("SELECT * FROM person_gallery_media WHERE person_id=? AND status='active' ORDER BY created_at DESC,id DESC", personId).Select(GalleryItem).ToList() }, { "count", GallerySummary(db, personId, 1)["count"] }, { "can_manage", true } });
        if (!CheckCsrf(csrf)) throw Fail("Your session token expired. Refresh the page and try again.", 403);
        if (r["action"] == "upload") {
          List<TreeUpload> files; r.Files.TryGetValue("media_files", out files);
          files = (files ?? new List<TreeUpload>()).Where(f => f.Data.Length > 0).ToList();
          if (files.Count == 0 || files.Count > 20) throw Fail("Choose between 1 and 20 media files at a time.");
          List<string> stored = new List<string>();
          db.Exec("BEGIN IMMEDIATE");
          try {
            foreach (TreeUpload file in files) {
              Dictionary<string, object> item = StoreGalleryUpload(file);
              stored.Add((string)item["file_name"]);
              db.Execute("INSERT INTO person_gallery_media(long_id,person_id,media_type,file_name,original_name,mime_type,file_size,width,height,created_at,updated_at)VALUES(?,?,?,?,?,?,?,?,?,?,?)", NewId(20), personId, item["media_type"], item["file_name"], item["original_name"], item["mime_type"], item["file_size"], item["width"], item["height"], Now(), Now());
            }
            db.Exec("COMMIT");
          } catch {
            try { db.Exec("ROLLBACK"); } catch { }
            foreach (string name in stored) DeleteGalleryFiles(name);
            throw;
          }
          return Ok("Media added to the private gallery.", new Dictionary<string, object> { { "summary", GallerySummary(db, personId, 5) } });
        }
        if (r["action"] == "delete") {
          Dictionary<string, string> item = db.QueryOne("SELECT * FROM person_gallery_media WHERE long_id=? AND person_id=? AND status='active'", r["media"], personId);
          if (item == null) throw Fail("Media not found.", 404);
          db.Execute("DELETE FROM person_gallery_media WHERE id=?", QI(item, "id"));
          string profile = Q(item, "file_name").StartsWith("profile_", StringComparison.Ordinal) ? Q(item, "file_name").Substring(8) : "";
          if (profile == "") DeleteGalleryFiles(Q(item, "file_name"));
          else if (db.QueryOne("SELECT id FROM persons WHERE image_name=?", profile) == null && db.QueryOne("SELECT id FROM person_gallery_media WHERE file_name=? AND status='active'", Q(item, "file_name")) == null) DeletePhotoFiles(profile);
          return Ok("Media removed from the private gallery.", new Dictionary<string, object> { { "summary", GallerySummary(db, personId, 5) } });
        }
        throw Fail("Unknown private gallery action.", 400);
      }
    }

    private static string SafeExtension(string original, string[] allowed) {
      string[] parts = Path.GetFileName(original ?? "").ToLowerInvariant().Split('.');
      if (parts.Length < 2) return "";
      string[] dangerous = { "php", "phtml", "exe", "bat", "cmd", "sh", "js", "html", "htm", "jar", "jsp", "asp", "aspx", "cgi", "pl", "py", "rb", "com", "vbs", "ws", "wsf", "ps1", "scr", "msi", "dll", "lnk" };
      if (parts.Take(parts.Length - 1).Any(dangerous.Contains)) return "";
      return allowed.Contains(parts[parts.Length - 1]) ? parts[parts.Length - 1] : "";
    }
    private static string SniffMime(byte[] d) {
      Func<int, string, bool> at = (offset, text) => d.Length >= offset + text.Length && Encoding.ASCII.GetString(d, offset, text.Length) == text;
      if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
      if (d.Length > 8 && d[0] == 0x89 && at(1, "PNG")) return "image/png";
      if (at(0, "GIF8")) return "image/gif";
      if (at(0, "RIFF") && at(8, "WEBP")) return "image/webp";
      if (at(4, "ftyp")) return "video/mp4";
      if (d.Length > 4 && d[0] == 0x1A && d[1] == 0x45 && d[2] == 0xDF && d[3] == 0xA3) return "video/webm";
      if (at(0, "OggS")) return "video/ogg";
      return "";
    }

    // Images are re-encoded (EXIF dropped) to a 2600 px full size and a 520 px thumbnail; GIF and WebP keep the
    // original as the full file because GDI+ cannot write them. Videos are stored as uploaded.
    private static Dictionary<string, object> StoreGalleryUpload(TreeUpload file) {
      Directory.CreateDirectory(GalleryDir);
      string mime = SniffMime(file.Data), original = Path.GetFileName(file.FileName ?? "media"), baseName = NewId(16);
      if (original.Length > 255) original = original.Substring(0, 255);
      Dictionary<string, string[]> images = new Dictionary<string, string[]> { { "image/jpeg", new[] { "jpg", "jpeg" } }, { "image/png", new[] { "png" } }, { "image/webp", new[] { "webp" } }, { "image/gif", new[] { "gif" } } };
      Dictionary<string, string> videos = new Dictionary<string, string> { { "video/mp4", "mp4" }, { "video/webm", "webm" }, { "video/ogg", "ogv" } };
      int width = 0, height = 0; string fileName, type;
      if (images.ContainsKey(mime)) {
        if (file.Data.Length > 15 * 1024 * 1024 || SafeExtension(original, images[mime]) == "") throw Fail("Images must be JPEG, PNG, WebP, or GIF files no larger than 15 MB.");
        type = "image";
        bool keepOriginal = mime == "image/gif" || mime == "image/webp";
        fileName = baseName + (keepOriginal ? (mime == "image/gif" ? ".gif" : ".webp") : ".jpg");
        try {
          using (MemoryStream input = new MemoryStream(file.Data))
          using (Image source = Image.FromStream(input, true, true)) {
            width = source.Width; height = source.Height;
            if (width > 12000 || height > 12000 || (long)width * height > 60000000L) throw Fail("The image dimensions are too large.");
            if (keepOriginal) File.WriteAllBytes(Path.Combine(GalleryDir, fileName), file.Data);
            else SaveJpeg(source, Path.Combine(GalleryDir, fileName), 2600, 2600, false);
            SaveJpeg(source, Path.Combine(GalleryDir, baseName + "_sm.jpg"), 520, 520, false);
          }
        } catch (TreeError) { throw; } catch {
          if (mime != "image/webp") throw Fail("This media file is invalid or unsupported.");
          File.WriteAllBytes(Path.Combine(GalleryDir, fileName), file.Data); // No WebP codec on this Windows: the thumbnail falls back to the full file.
        }
      } else if (videos.ContainsKey(mime)) {
        string extension = SafeExtension(original, new[] { "mp4", "webm", "ogv", "mov", "m4v" });
        if (extension == "" || file.Data.Length > 500L * 1024 * 1024) throw Fail("Videos must be MP4, WebM, or OGV files no larger than 500 MB.");
        type = "video"; fileName = baseName + "." + videos[mime];
        File.WriteAllBytes(Path.Combine(GalleryDir, fileName), file.Data);
      } else throw Fail("This media file is invalid or unsupported.");
      return new Dictionary<string, object> { { "media_type", type }, { "file_name", fileName }, { "original_name", original }, { "mime_type", mime }, { "file_size", file.Data.LongLength }, { "width", width }, { "height", height } };
    }

    private static string GalleryPath(Dictionary<string, string> item, bool thumb) {
      string name = Q(item, "file_name");
      if (name.StartsWith("profile_", StringComparison.Ordinal)) { string file = ExistingPhotoFile(name.Substring(8), thumb ? "_sm" : "_lg"); return file == "" ? "" : Path.Combine(PersonsDir, file); }
      if (!Regex.IsMatch(name, @"^[a-f0-9]{32}\.(jpg|gif|webp|mp4|webm|ogv)$")) return "";
      if (thumb && Q(item, "media_type") == "image") { string small = Path.Combine(GalleryDir, name.Substring(0, 32) + "_sm.jpg"); if (File.Exists(small)) return small; }
      string path = Path.Combine(GalleryDir, name);
      return File.Exists(path) ? path : "";
    }
    private static void DeleteGalleryFiles(string name) {
      if (!Regex.IsMatch(name ?? "", @"^[a-f0-9]{32}\.[a-z0-9]{2,4}$")) return;
      foreach (string file in new[] { name, name.Substring(0, 32) + "_sm.jpg" }) { try { string path = Path.Combine(GalleryDir, file); if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    internal void TreeGalleryFile(HttpListenerContext context, string mediaId, bool thumb) {
      Dictionary<string, string> item;
      using (SqliteDb db = Open()) item = db.QueryOne("SELECT * FROM person_gallery_media WHERE long_id=? AND status='active'", mediaId ?? "");
      string path = item == null ? "" : GalleryPath(item, thumb);
      if (path == "") { context.Response.StatusCode = 404; context.Response.Close(); return; }
      string extension = Path.GetExtension(path).ToLowerInvariant();
      string type = extension == ".jpg" ? "image/jpeg" : extension == ".gif" ? "image/gif" : extension == ".webp" ? "image/webp" : extension == ".webm" ? "video/webm" : extension == ".ogv" ? "video/ogg" : "video/mp4";
      using (FileStream stream = File.OpenRead(path)) {
        long size = stream.Length, start = 0, end = size - 1;
        Match range = Regex.Match(context.Request.Headers["Range"] ?? "", @"^bytes=(\d*)-(\d*)$");
        if (range.Success && size > 0) {
          if (range.Groups[1].Value == "") { start = Math.Max(0, size - Int64.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture)); }
          else { start = Int64.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture); if (range.Groups[2].Value != "") end = Math.Min(end, Int64.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture)); }
          if (start > end || start >= size) { context.Response.StatusCode = 416; context.Response.Headers["Content-Range"] = "bytes */" + size; context.Response.Close(); return; }
          context.Response.StatusCode = 206; context.Response.Headers["Content-Range"] = "bytes " + start + "-" + end + "/" + size;
        }
        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "private, no-store, max-age=0";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentLength64 = end - start + 1;
        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[1024 * 1024]; long remaining = end - start + 1; int read;
        try {
          while (remaining > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0) { context.Response.OutputStream.Write(buffer, 0, read); remaining -= read; }
        } catch (HttpListenerException) { }
        try { context.Response.Close(); } catch { }
      }
    }

    internal Dictionary<string, object> TreeRemovePhoto(TreeRequest r, string csrf) {
      if (r["action"] == "details") {
        using (SqliteDb db = Open()) {
          int id = QI(db.QueryOne("SELECT id FROM persons WHERE long_id=?", r["person"]), "id");
          if (id == 0) throw Fail("Person not found.", 404);
          return Ok("", new Dictionary<string, object> { { "person", BasePerson(db, id) } });
        }
      }
      if (r["action"] != "remove_photo") throw Fail("Unknown action.", 400);
      if (!CheckCsrf(csrf)) throw Fail("Your session token expired. Refresh the page and try again.", 403);
      using (SqliteDb db = Open()) {
        Dictionary<string, string> person = db.QueryOne("SELECT id,image_name,gender FROM persons WHERE long_id=?", r["person"]);
        if (person == null) throw Fail("Person not found.", 404);
        if (Q(person, "image_name") != r["expected_image"]) throw Fail("This profile image changed. Reopen the editor and try again.");
        if (Q(person, "image_name") != "") { RetainProfile(db, QI(person, "id"), Q(person, "image_name")); db.Execute("UPDATE persons SET image_name='',updated_at=? WHERE id=?", Now(), QI(person, "id")); }
        return Ok("Profile image removed.", new Dictionary<string, object> { { "person", new Dictionary<string, object> { { "id", QI(person, "id") }, { "image_name", "" }, { "image_url", DefaultImageUrl(Q(person, "gender") == "" ? "M" : Q(person, "gender")) } } } });
      }
    }

    // ---------- Partial export, copy and paste ----------
    private const int PackageMaxBytes = 6 * 1024 * 1024, PackageMaxPeople = 2000;

    private string PackageJson(Dictionary<string, object> package) {
      Dictionary<string, object> copy = new Dictionary<string, object>(package); copy.Remove("sig");
      return new JavaScriptSerializer { MaxJsonLength = PackageMaxBytes * 2 }.Serialize(copy);
    }

    // Top-level <li> elements of a well-formed fragment produced by FamilyTreeRenderer.
    private static List<string> TopLevelItems(string html) {
      List<string> items = new List<string>(); int depth = 0, start = -1;
      foreach (Match tag in Regex.Matches(html, @"<(/?)li\b[^>]*>")) {
        if (tag.Groups[1].Value == "") { if (depth++ == 0) start = tag.Index; }
        else if (--depth == 0) items.Add(html.Substring(start, tag.Index + tag.Length - start));
      }
      return items;
    }
    private static string InnerList(string li) {
      int open = li.IndexOf("<ul", StringComparison.Ordinal);
      if (open < 0) return "";
      int startTag = li.IndexOf('>', open) + 1, depth = 1;
      foreach (Match tag in Regex.Matches(li.Substring(startTag), @"<(/?)ul\b[^>]*>")) {
        depth += tag.Groups[1].Value == "" ? 1 : -1;
        if (depth == 0) return li.Substring(startTag, tag.Index);
      }
      return "";
    }

    private Dictionary<string, object> PartialPackage(SqliteDb db, TreeRequest r) {
      Dictionary<string, string> tree = RequireTree(db, r);
      int treeId = QI(tree, "id"), relationshipId = Math.Max(0, r.Int("relationship_id"));
      bool excludeParents = r["exclude_parents"] == "1", photos = !r.Has("photos") || r["photos"] == "1", gallery = !r.Has("gallery") || r["gallery"] == "1";
      Dictionary<string, string> anchor = PartialAnchor(db, treeId, relationshipId);
      if (anchor == null) throw Fail("The selected parent couple is no longer available.");
      FamilyTreeRenderer renderer = Renderer(LoadGraph(db, tree), false, relationshipId);
      string html = renderer.BuildTree(QI(anchor, "person1_id"));
      if (html == "") throw Fail("The selected parent couple is no longer available.");
      if (excludeParents) {
        html = String.Join("", TopLevelItems(InnerList(html)).Where(li => !Regex.IsMatch(li, "^<li class=\"external_partner ")));
        if (html == "") throw Fail("This couple has no children to export.");
      }
      if (!photos) html = Regex.Replace(html, "(data-person-gender='([^']*)'[^>]*>\\s*<img src=')[^']*'", m => m.Groups[1].Value + DefaultImageUrl(m.Groups[2].Value) + "'");
      List<int> ids = Regex.Matches(html, "data-person-id='(\\d+)'").Cast<Match>().Select(m => Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
      if (ids.Count == 0) throw Fail("The selected parent couple is no longer available.");
      string idSql = String.Join(",", ids);
      List<Dictionary<string, string> > rows = db.Query("SELECT * FROM persons WHERE id IN (" + idSql + ") ORDER BY id");
      Dictionary<int, int> refs = new Dictionary<int, int>();
      for (int i = 0; i < rows.Count; i++) refs[QI(rows[i], "id")] = i + 1;
      Dictionary<int, List<object> > media = new Dictionary<int, List<object> >();
      if (gallery) foreach (Dictionary<string, string> item in db.Query("SELECT person_id,long_id FROM person_gallery_media WHERE person_id IN (" + idSql + ") AND status='active' ORDER BY id")) {
        List<object> list; if (!media.TryGetValue(QI(item, "person_id"), out list)) media[QI(item, "person_id")] = list = new List<object>(); list.Add(Q(item, "long_id"));
      }
      List<object> people = new List<object>();
      foreach (Dictionary<string, string> row in rows) {
        Dictionary<string, object> person = new Dictionary<string, object> { { "ref", refs[QI(row, "id")] } };
        foreach (string field in PersonFields) person[field] = field == "is_deceased" ? (object)(Deceased(row) ? 1 : 0) : Q(row, field);
        person["image"] = photos ? PhotoBase(Q(row, "image_name")) : "";
        List<object> items; person["gallery"] = media.TryGetValue(QI(row, "id"), out items) ? items : new List<object>();
        people.Add(person);
      }
      List<object> relationships = new List<object>();
      foreach (Dictionary<string, string> u in db.Query("SELECT * FROM relationships WHERE family_tree_id=? AND person1_id IN (" + idSql + ") AND person2_id IN (" + idSql + ") ORDER BY id", treeId)) {
        int sibling; refs.TryGetValue(QI(u, "sibling_person_id"), out sibling);
        relationships.Add(new Dictionary<string, object> { { "a", refs[QI(u, "person1_id")] }, { "b", refs[QI(u, "person2_id")] }, { "type", Q(u, "relationship_type") }, { "current", QI(u, "is_current") }, { "start", Q(u, "start_date") }, { "end", Q(u, "end_date") }, { "sibling", sibling } });
      }
      List<object> parents = db.Query("SELECT DISTINCT child_id,parent_id FROM parent_child WHERE family_tree_id=? AND child_id IN (" + idSql + ") AND parent_id IN (" + idSql + ")", treeId)
        .Select(l => (object)new Dictionary<string, object> { { "child", refs[QI(l, "child_id")] }, { "parent", refs[QI(l, "parent_id")] } }).ToList();
      int[] anchorIds = { QI(anchor, "person1_id"), QI(anchor, "person2_id") };
      List<int> roots;
      if (excludeParents) roots = db.Query("SELECT DISTINCT child_id FROM parent_child WHERE family_tree_id=? AND parent_id IN (?,?) AND child_id IN (" + idSql + ") ORDER BY child_id", treeId, anchorIds[0], anchorIds[1]).Select(l => refs[QI(l, "child_id")]).Distinct().ToList();
      else { int sibling = QI(anchor, "sibling_person_id"); int rootRef; roots = refs.TryGetValue(anchorIds.Contains(sibling) ? sibling : anchorIds[0], out rootRef) ? new List<int> { rootRef } : new List<int>(); }
      if (roots.Count == 0) throw Fail("This couple has no children to export.");
      List<object> couple = excludeParents ? new List<object>() : anchorIds.Where(refs.ContainsKey).Select(id => (object)refs[id]).ToList();
      Dictionary<string, object> package = new Dictionary<string, object> {
        { "kind", "racinage_partial" }, { "version", 1 }, { "name", (Q(anchor, "person1_name") + " & " + Q(anchor, "person2_name")).Trim() }, { "family", "local:" + DeviceId },
        { "created_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) }, { "exclude_parents", excludeParents }, { "photos", photos }, { "gallery", gallery },
        { "html", "<ul>" + html + "</ul>" }, { "roots", roots.Cast<object>().ToList() }, { "couple", couple }, { "people", people }, { "relationships", relationships }, { "parents", parents },
      };
      package["sig"] = Hmac("package", PackageJson(package));
      Dictionary<string, object> result = PackagePreview(package);
      result["package"] = new JavaScriptSerializer { MaxJsonLength = PackageMaxBytes * 2 }.Serialize(package);
      return Ok("", result);
    }

    private static Dictionary<string, object> PackagePreview(Dictionary<string, object> package) {
      Dictionary<int, Dictionary<string, object> > people = ((System.Collections.IEnumerable)package["people"]).Cast<Dictionary<string, object> >().ToDictionary(p => Convert.ToInt32(p["ref"], CultureInfo.InvariantCulture));
      HashSet<int> roots = new HashSet<int>(((System.Collections.IEnumerable)package["roots"]).Cast<object>().Select(o => Convert.ToInt32(o, CultureInfo.InvariantCulture)));
      List<object> couple = new List<object>();
      foreach (object value in (System.Collections.IEnumerable)(package.ContainsKey("couple") ? package["couple"] : new object[0])) {
        int reference = Convert.ToInt32(value, CultureInfo.InvariantCulture); Dictionary<string, object> p;
        if (people.TryGetValue(reference, out p)) couple.Add(new Dictionary<string, object> { { "ref", reference }, { "name", (Convert.ToString(p["first_name"]) + " " + Convert.ToString(p["last_name"])).Trim() }, { "gender", Convert.ToString(p["gender"]) }, { "sibling", roots.Contains(reference) } });
      }
      return new Dictionary<string, object> { { "couple", couple }, { "name", Convert.ToString(package["name"]) }, { "html", Convert.ToString(package["html"]) }, { "count", people.Count }, { "exclude_parents", true.Equals(package["exclude_parents"]) }, { "photos", true.Equals(package["photos"]) }, { "gallery", true.Equals(package["gallery"]) } };
    }

    // Only packages this install signed are accepted: the preview HTML and media references are trusted only because of that.
    private Dictionary<string, object> VerifyPackage(string raw) {
      raw = (raw ?? "").Trim();
      if (raw == "" || raw.Length > PackageMaxBytes) throw Fail("This is not a Racinage Partial.");
      Dictionary<string, object> package;
      try { package = new JavaScriptSerializer { MaxJsonLength = PackageMaxBytes * 2 }.DeserializeObject(raw) as Dictionary<string, object>; } catch { package = null; }
      object kind, version, sig, people;
      if (package == null || !package.TryGetValue("kind", out kind) || (kind as string) != "racinage_partial" || !package.TryGetValue("version", out version) || !1.Equals(version) || !package.TryGetValue("sig", out sig) || !(sig is string) || !package.TryGetValue("people", out people) || !(people is object[]))
        throw Fail("This is not a Racinage Partial.");
      if (!FixedEquals(Hmac("package", PackageJson(package)), (string)sig)) throw Fail("This Partial was changed after it was copied. Copy it again.");
      int count = ((object[])people).Length;
      if (count == 0 || count > PackageMaxPeople) throw Fail("This is not a Racinage Partial.");
      return package;
    }

    private static string NameKey(string first, string last) { return String.Join(" ", Regex.Split((first + " " + last).Trim().ToLowerInvariant(), @"\s+").Where(w => w != "").OrderBy(w => w, StringComparer.Ordinal)); }
    private static string S(Dictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
    private static int SI(Dictionary<string, object> row, string key) { int value; return Int32.TryParse(S(row, key), out value) ? value : 0; }
    private static IEnumerable<Dictionary<string, object> > Items(Dictionary<string, object> package, string key) { object value; return package.TryGetValue(key, out value) && value is object[] ? ((object[])value).OfType<Dictionary<string, object> >() : Enumerable.Empty<Dictionary<string, object> >(); }
    private static List<int> Ints(Dictionary<string, object> package, string key) { object value; return package.TryGetValue(key, out value) && value is object[] ? ((object[])value).Select(o => { int i; return Int32.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out i) ? i : 0; }).Where(i => i > 0).ToList() : new List<int>(); }

    // Pasted people whose name already exists in the family, each with the existing candidates.
    private static List<Dictionary<string, object> > PackageConflicts(SqliteDb db, Dictionary<string, object> package, int treeId) {
      HashSet<string> unknown = new HashSet<string>(UnknownNames.Select(n => NameKey(n, "")));
      Dictionary<string, List<Dictionary<string, object> > > byName = new Dictionary<string, List<Dictionary<string, object> > >();
      foreach (Dictionary<string, object> p in Items(package, "people")) {
        string key = NameKey(S(p, "first_name"), S(p, "last_name"));
        if (key == "" || unknown.Contains(key)) continue;
        List<Dictionary<string, object> > list; if (!byName.TryGetValue(key, out list)) byName[key] = list = new List<Dictionary<string, object> >(); list.Add(p);
      }
      List<Dictionary<string, object> > conflicts = new List<Dictionary<string, object> >();
      if (byName.Count == 0) return conflicts;
      HashSet<int> inTree = TreeMemberIds(db, treeId);
      Dictionary<string, List<Dictionary<string, object> > > candidates = new Dictionary<string, List<Dictionary<string, object> > >();
      foreach (Dictionary<string, string> row in db.Query("SELECT id,first_name,last_name,birth_date,death_date,gender,image_name FROM persons ORDER BY id")) {
        string key = NameKey(Q(row, "first_name"), Q(row, "last_name"));
        if (!byName.ContainsKey(key)) continue;
        List<Dictionary<string, object> > list; if (!candidates.TryGetValue(key, out list)) candidates[key] = list = new List<Dictionary<string, object> >();
        list.Add(new Dictionary<string, object> { { "id", QI(row, "id") }, { "name", (Q(row, "first_name") + " " + Q(row, "last_name")).Trim() }, { "birth_date", Q(row, "birth_date") }, { "death_date", Q(row, "death_date") }, { "image_url", PersonImageUrl(Q(row, "image_name"), Q(row, "gender"), "sm") }, { "in_tree", inTree.Contains(QI(row, "id")) } });
      }
      foreach (var pair in byName) {
        List<Dictionary<string, object> > list;
        if (!candidates.TryGetValue(pair.Key, out list)) continue;
        foreach (Dictionary<string, object> p in pair.Value) {
          string birth = S(p, "birth_date");
          int match = birth == "" ? 0 : list.Where(c => (string)c["birth_date"] == birth).Select(c => (int)c["id"]).FirstOrDefault();
          conflicts.Add(new Dictionary<string, object> { { "ref", SI(p, "ref") }, { "name", (S(p, "first_name") + " " + S(p, "last_name")).Trim() }, { "birth_date", birth }, { "death_date", S(p, "death_date") }, { "suggested", match > 0 ? "use:" + N(match) : "new" }, { "candidates", list } });
        }
      }
      return conflicts;
    }

    private Dictionary<string, object> PastePartial(SqliteDb db, TreeRequest r, bool preview) {
      Dictionary<string, object> package = VerifyPackage(r["package"]);
      Dictionary<string, string> tree = RequireTree(db, r);
      int treeId = QI(tree, "id");
      if (preview) { Dictionary<string, object> result = PackagePreview(package); result["conflicts"] = PackageConflicts(db, package, treeId); return Ok("", result); }
      string mode = r["paste_mode"];
      int anchorId = r.Int("anchor_person_id"), secondId = r.Int("second_parent_id"), siblingRef = r.Int("paste_sibling_ref");
      List<int> roots = Ints(package, "roots"), couple = Ints(package, "couple");
      if (siblingRef > 0 && couple.Contains(siblingRef)) roots = new List<int> { siblingRef };
      if (roots.Count == 0) throw Fail("This is not a Racinage Partial.");
      int otherParent = 0;
      if (mode == "add_child") {
        if (anchorId < 1 || !InTree(db, treeId, anchorId)) throw Fail("Anchor person not found.");
        otherParent = AddChildParentIds(db, treeId, anchorId, secondId).Where(id => id != anchorId).FirstOrDefault();
      } else if (mode == "add_first_member") {
        if (QI(tree, "root_person_id") > 0) throw Fail("This tree already has a first member.");
        if (roots.Count != 1) throw Fail("Include the main parents to paste this Partial into an empty tree.");
      } else throw Fail("Paste a Partial from an Add Child button.");
      // Resolutions may only point at the server's own candidate list.
      Dictionary<int, List<int> > allowed = PackageConflicts(db, package, treeId).ToDictionary(c => (int)c["ref"], c => ((List<Dictionary<string, object> >)c["candidates"]).Select(x => (int)x["id"]).ToList());
      Dictionary<string, object> requested = null;
      try { requested = new JavaScriptSerializer().DeserializeObject(r.Has("resolutions") ? r["resolutions"] : "{}") as Dictionary<string, object>; } catch { }
      Dictionary<int, Tuple<string, int> > resolutions = new Dictionary<int, Tuple<string, int> >(); HashSet<int> used = new HashSet<int>();
      foreach (var choice in requested ?? new Dictionary<string, object>()) {
        string value = choice.Value as string;
        if (value == null || value == "new") continue;
        Match match = Regex.Match(value, "^(use|overwrite):(\\d+)$");
        int reference, id; Int32.TryParse(choice.Key, out reference);
        if (!match.Success || !Int32.TryParse(match.Groups[2].Value, out id)) throw Fail("Choose a listed person to merge with.");
        List<int> list;
        if (!allowed.TryGetValue(reference, out list) || !list.Contains(id)) throw Fail("Choose a listed person to merge with.");
        if (!used.Add(id)) throw Fail("This person was already chosen for another pasted person.");
        if (id == anchorId || id == otherParent) throw Fail("A person cannot be pasted under themselves.");
        resolutions[reference] = Tuple.Create(match.Groups[1].Value, id);
      }
      Dictionary<int, int> map = new Dictionary<int, int>(); HashSet<int> mediaRefs = new HashSet<int>(), replaced = new HashSet<int>();
      foreach (Dictionary<string, object> p in Items(package, "people")) {
        int reference = SI(p, "ref");
        if (reference < 1) throw Fail("This is not a Racinage Partial.");
        string first = CleanText(S(p, "first_name"), 120) ?? T("Unknown person"), last = CleanText(S(p, "last_name"), 120) ?? "", gender = S(p, "gender") == "F" ? "F" : "M";
        string death = CleanDate(S(p, "death_date"));
        Dictionary<string, object> fields = new Dictionary<string, object> {
          { "birth_date", CleanDate(S(p, "birth_date")) }, { "death_date", death }, { "is_deceased", death != null || SI(p, "is_deceased") == 1 ? 1 : 0 },
          { "phone1", CleanText(S(p, "phone1"), 50) }, { "phone2", CleanText(S(p, "phone2"), 50) }, { "email1", CleanEmail(S(p, "email1")) }, { "email2", CleanEmail(S(p, "email2")) },
          { "address", CleanText(S(p, "address"), 255) }, { "religion", CleanText(S(p, "religion"), 120) }, { "website", CleanText(S(p, "website"), 255) }, { "birth_place", CleanText(S(p, "birth_place"), 191) },
          { "nationality", CleanText(S(p, "nationality"), 120) }, { "blood_group", CleanText(S(p, "blood_group"), 10) }, { "education_level", CleanText(S(p, "education_level"), 120) },
          { "marital_status", new[] { "single", "married", "widowed", "divorced", "separated" }.Contains(S(p, "marital_status")) ? S(p, "marital_status") : "single" },
        };
        Tuple<string, int> resolution;
        if (resolutions.TryGetValue(reference, out resolution)) {
          if (resolution.Item1 == "overwrite") {
            // The pasted person is kept: every detail replaces the existing one.
            List<object> args = new List<object> { first, last, gender, fields["is_deceased"] };
            foreach (string column in ExtraColumns) args.Add(fields[column]);
            args.Add(Now()); args.Add(resolution.Item2);
            db.Execute("UPDATE persons SET first_name=?,last_name=?,gender=?,is_deceased=?," + String.Join(",", ExtraColumns.Select(c => c + "=?")) + ",updated_at=? WHERE id=?", args.ToArray());
            mediaRefs.Add(reference); replaced.Add(resolution.Item2);
          }
          map[reference] = resolution.Item2;
          continue;
        }
        int created = CreatePerson(db, first, last, gender, fields);
        if (created == 0) throw Fail("Could not add person to this tree.");
        map[reference] = created; mediaRefs.Add(reference);
      }
      Func<int, int> mapped = reference => { int id; return map.TryGetValue(reference, out id) ? id : 0; };
      foreach (Dictionary<string, object> link in Items(package, "relationships")) {
        int a = mapped(SI(link, "a")), b = mapped(SI(link, "b"));
        if (a == 0 || b == 0 || a == b) continue;
        int sibling = mapped(SI(link, "sibling"));
        if (couple.Count > 0 && couple.Contains(SI(link, "a")) && couple.Contains(SI(link, "b")) && mapped(roots[0]) > 0) sibling = mapped(roots[0]);
        if (db.QueryOne("SELECT 1 AS x FROM relationships WHERE family_tree_id=? AND ((person1_id=?2 AND person2_id=?3) OR (person1_id=?3 AND person2_id=?2))", treeId, a, b) != null) continue;
        db.Execute("INSERT INTO relationships(family_tree_id,person1_id,person2_id,relationship_type,is_current,start_date,end_date,sibling_person_id,created_at)VALUES(?,?,?,?,?,?,?,?,?)", treeId, a, b, S(link, "type") == "partner" ? "partner" : "spouse", SI(link, "current") == 0 ? 0 : 1, CleanDate(S(link, "start")), CleanDate(S(link, "end")), sibling > 0 ? (object)sibling : null, Now());
      }
      List<int[]> links = Items(package, "parents").Select(l => new[] { mapped(SI(l, "child")), mapped(SI(l, "parent")) }).ToList();
      List<int> rootIds = roots.Select(mapped).Where(id => id > 0).ToList();
      if (rootIds.Count == 0) throw Fail("This is not a Racinage Partial.");
      if (mode == "add_child") foreach (int root in rootIds) foreach (int parent in new[] { anchorId, otherParent }.Where(id => id > 0)) links.Add(new[] { root, parent });
      foreach (int[] link in links) if (link[0] > 0 && link[1] > 0 && link[0] != link[1]) AddParentLink(db, treeId, link[0], link[1]);
      if (mode == "add_first_member") SetRoot(db, treeId, rootIds[0]);
      int failed = CopyPackageMedia(db, package, map.Where(p => mediaRefs.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), replaced);
      UpdateRootIfNeeded(db, treeId);
      if (mode == "add_child") SaveSiblingBirthOrder(db, treeId, rootIds[0], r);
      return Success(db, failed > 0 ? "Partial pasted. Some media could not be copied." : "Partial pasted.", Q(tree, "long_id"), rootIds[0], false, r);
    }

    // Copies photos and gallery media of new and overwritten people. Kept pasted people replace the existing media.
    private static int CopyPackageMedia(SqliteDb db, Dictionary<string, object> package, Dictionary<int, int> map, HashSet<int> replaced) {
      int failed = 0;
      bool photos = true.Equals(package.ContainsKey("photos") ? package["photos"] : false), gallery = true.Equals(package.ContainsKey("gallery") ? package["gallery"] : false);
      Dictionary<int, Tuple<string, List<Dictionary<string, string> > > > previous = replaced.ToDictionary(id => id, id => Tuple.Create(Q(db.QueryOne("SELECT image_name FROM persons WHERE id=?", id), "image_name"), gallery ? db.Query("SELECT * FROM person_gallery_media WHERE person_id=?", id) : new List<Dictionary<string, string> >()));
      foreach (Dictionary<string, object> p in Items(package, "people")) {
        int personId; if (!map.TryGetValue(SI(p, "ref"), out personId)) continue;
        string image = S(p, "image");
        if (image == "") { if (replaced.Contains(personId) && photos) db.Execute("UPDATE persons SET image_name='' WHERE id=?", personId); continue; }
        string source = ExistingPhotoFile(image, "");
        if (source == "") { failed++; continue; }
        string copy = NewId(10);
        foreach (string suffix in new[] { "", "_sm", "_lg" }) {
          string file = ExistingPhotoFile(image, suffix);
          if (file != "") try { File.Copy(Path.Combine(PersonsDir, file), Path.Combine(PersonsDir, copy + suffix + Path.GetExtension(file))); } catch { }
        }
        db.Execute("UPDATE persons SET image_name=? WHERE id=?", copy, personId);
      }
      if (gallery) {
        Dictionary<string, int> owners = new Dictionary<string, int>();
        foreach (Dictionary<string, object> p in Items(package, "people")) { int personId; if (map.TryGetValue(SI(p, "ref"), out personId)) foreach (object id in (object[])(p.ContainsKey("gallery") && p["gallery"] is object[] ? p["gallery"] : new object[0])) owners[Convert.ToString(id, CultureInfo.InvariantCulture)] = personId; }
        foreach (var owner in owners) {
          Dictionary<string, string> item = db.QueryOne("SELECT * FROM person_gallery_media WHERE long_id=? AND status='active'", owner.Key);
          string source = item == null ? "" : GalleryPath(item, false);
          if (source == "") { failed++; continue; }
          string baseName = NewId(16), name = baseName + (Q(item, "file_name").StartsWith("profile_", StringComparison.Ordinal) ? Path.GetExtension(source) : Path.GetExtension(Q(item, "file_name")));
          try {
            Directory.CreateDirectory(GalleryDir);
            File.Copy(source, Path.Combine(GalleryDir, name));
            string thumb = GalleryPath(item, true);
            if (Q(item, "media_type") == "image" && thumb != "" && thumb != source) File.Copy(thumb, Path.Combine(GalleryDir, baseName + "_sm.jpg"));
            db.Execute("INSERT INTO person_gallery_media(long_id,person_id,media_type,file_name,original_name,caption,mime_type,file_size,width,height,created_at,updated_at)VALUES(?,?,?,?,?,?,?,?,?,?,?,?)", NewId(20), owner.Value, Q(item, "media_type"), name, Q(item, "original_name"), Q(item, "caption"), Q(item, "mime_type"), QI(item, "file_size"), QI(item, "width"), QI(item, "height"), Now(), Now());
          } catch { DeleteGalleryFiles(name); failed++; }
        }
      }
      // Delete the discarded person's media rows and files that nobody else uses ("keep pasted").
      foreach (var entry in previous) {
        foreach (Dictionary<string, string> item in entry.Value.Item2) {
          db.Execute("DELETE FROM person_gallery_media WHERE id=?", QI(item, "id"));
          if (!Q(item, "file_name").StartsWith("profile_", StringComparison.Ordinal)) DeleteGalleryFiles(Q(item, "file_name"));
        }
        string old = PhotoBase(entry.Value.Item1);
        if (old != "" && Q(db.QueryOne("SELECT image_name FROM persons WHERE id=?", entry.Key), "image_name") != entry.Value.Item1 && db.QueryOne("SELECT id FROM persons WHERE image_name=?", old) == null && db.QueryOne("SELECT id FROM person_gallery_media WHERE file_name=?", "profile_" + old) == null) DeletePhotoFiles(old);
      }
      return failed;
    }
  }
}

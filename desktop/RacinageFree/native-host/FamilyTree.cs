using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Port of hosted includes/tree_graph.php and includes/FamilyTree.php (batch/graph mode, non-public rendering).
// The markup must stay byte-identical to the hosted renderer so the vendored hosted tree JS and CSS work unchanged.
// Rows keep hosted column names and hold strings or null, like mysqli rows. Golden check: tests/family-tree.
namespace RacinageFreeDesktop {
  internal static class LocalFamilyTreeSchema {
    // database/2026_11_06_local_family_tree.sql, embedded by the build so the script and the app never drift.
    internal static void Apply(SqliteDb db) {
      using (Stream stream = typeof(LocalFamilyTreeSchema).Assembly.GetManifestResourceStream("RacinageFree.LocalFamilyTree.sql"))
      using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) db.Exec(reader.ReadToEnd());
    }
  }

  internal sealed class FamilyTreeGraph {
    private readonly Dictionary<int, Dictionary<string, string> > people = new Dictionary<int, Dictionary<string, string> >();
    private readonly Dictionary<int, Dictionary<string, string> > unions = new Dictionary<int, Dictionary<string, string> >();
    private readonly Dictionary<int, List<Dictionary<string, string> > > byPerson = new Dictionary<int, List<Dictionary<string, string> > >();
    // ponytail: insertion-ordered lists with Contains, fine for per-person fan-out; switch to ordered sets if a family gets huge.
    private readonly Dictionary<int, List<int> > parents = new Dictionary<int, List<int> >();
    private readonly Dictionary<int, List<int> > children = new Dictionary<int, List<int> >();
    private readonly Dictionary<int, int> insertion = new Dictionary<int, int>();
    private readonly int primary;

    internal FamilyTreeGraph(IEnumerable<Dictionary<string, string> > personRows, IEnumerable<Dictionary<string, string> > unionRows, IEnumerable<Dictionary<string, string> > links, int primaryRelationshipId) {
      primary = primaryRelationshipId;
      foreach (Dictionary<string, string> person in personRows) people[I(person, "id")] = person;
      foreach (Dictionary<string, string> union in unionRows.OrderBy(u => I(u, "id"))) {
        unions[I(union, "id")] = union;
        foreach (int id in new[] { I(union, "person1_id"), I(union, "person2_id") }.Distinct()) List(byPerson, id).Add(union);
      }
      foreach (Dictionary<string, string> link in links) {
        int child = I(link, "child_id"), parent = I(link, "parent_id"), id = I(link, "id");
        AddOnce(List(parents, child), parent); AddOnce(List(children, parent), child);
        int current;
        insertion[child] = insertion.TryGetValue(child, out current) ? Math.Min(current, id) : id;
      }
    }

    internal static int I(Dictionary<string, string> row, string key) {
      string value; int number;
      return row != null && row.TryGetValue(key, out value) && value != null && Int32.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    internal static string S(Dictionary<string, string> row, string key) { string value; return row != null && row.TryGetValue(key, out value) && value != null ? value : ""; }
    private static List<T> List<T>(Dictionary<int, List<T> > map, int key) { List<T> list; if (!map.TryGetValue(key, out list)) map[key] = list = new List<T>(); return list; }
    private static List<T> Get<T>(Dictionary<int, List<T> > map, int key) { List<T> list; return map.TryGetValue(key, out list) ? list : new List<T>(); }
    private static void AddOnce(List<int> list, int value) { if (!list.Contains(value)) list.Add(value); }
    private static bool IsUnion(Dictionary<string, string> u) { string type = S(u, "relationship_type"); return type == "spouse" || type == "partner"; }
    private static int Other(Dictionary<string, string> u, int id) { return I(u, "person1_id") == id ? I(u, "person2_id") : I(u, "person1_id"); }
    private static Dictionary<string, string> With(Dictionary<string, string> row, params string[] pairs) {
      Dictionary<string, string> copy = new Dictionary<string, string>(row);
      for (int i = 0; i < pairs.Length; i += 2) if (!copy.ContainsKey(pairs[i])) copy[pairs[i]] = pairs[i + 1];
      return copy;
    }

    internal Dictionary<string, string> Person(int id) { Dictionary<string, string> person; return people.TryGetValue(id, out person) ? person : null; }

    internal Dictionary<string, string> Union(int id) {
      Dictionary<string, string> union;
      return unions.TryGetValue(id, out union) && people.ContainsKey(I(union, "person1_id")) && people.ContainsKey(I(union, "person2_id")) && IsUnion(union) ? union : null;
    }

    internal Dictionary<string, string> Spouse(int id, int anchor) {
      IEnumerable<Dictionary<string, string> > candidates = Get(byPerson, id).Where(u => IsUnion(u) && (anchor > 0 ? I(u, "id") == anchor : (S(u, "relationship_type") == "spouse" || I(u, "id") == primary)))
        .OrderByDescending(u => I(u, "id") == primary).ThenByDescending(u => I(u, "is_current")).ThenBy(u => I(u, "id"));
      foreach (Dictionary<string, string> union in candidates) {
        Dictionary<string, string> other = Person(Other(union, id));
        if (other == null) continue;
        return With(other, "relationship_id", union["id"], "selected_relationship_type", union["relationship_type"],
          "relationship_person1_id", union["person1_id"], "relationship_person2_id", union["person2_id"], "relationship_sibling_person_id", S(union, "sibling_person_id") == "" ? null : union["sibling_person_id"]);
      }
      return null;
    }

    internal List<Dictionary<string, string> > Partners(int id, int exclude) {
      List<Dictionary<string, string> > output = new List<Dictionary<string, string> >();
      HashSet<int> seen = new HashSet<int>();
      Dictionary<string, string> selected;
      unions.TryGetValue(exclude, out selected);
      foreach (Dictionary<string, string> union in Get(byPerson, id)) {
        if (!IsUnion(union)) continue;
        if (selected != null && Math.Min(I(selected, "person1_id"), I(selected, "person2_id")) == Math.Min(I(union, "person1_id"), I(union, "person2_id"))
          && Math.Max(I(selected, "person1_id"), I(selected, "person2_id")) == Math.Max(I(union, "person1_id"), I(union, "person2_id"))) continue;
        int other = Other(union, id);
        if (!people.ContainsKey(other) || !seen.Add(other)) continue;
        int shared = Get(children, id).Count(c => Get(children, other).Contains(c));
        output.Add(With(people[other], "relationship_id", union["id"], "selected_relationship_type", union["relationship_type"], "shared_child_count", shared.ToString(CultureInfo.InvariantCulture)));
      }
      return output;
    }

    // Hosted children($first, $second, true): shared children plus children recorded with just one of the pair,
    // unless that child belongs to the parent's other known union.
    internal List<Dictionary<string, string> > CoupleChildren(int first, int second) {
      List<int> ids = new List<int>(Get(children, first));
      foreach (int id in Get(children, second)) AddOnce(ids, id);
      List<Dictionary<string, string> > rows = new List<Dictionary<string, string> >();
      foreach (int id in ids) {
        if (!people.ContainsKey(id)) continue;
        List<int> childParents = Get(parents, id);
        if (!(first != second && childParents.Contains(first) && childParents.Contains(second))) {
          bool eligible = false;
          foreach (int parent in new[] { first, second }.Distinct()) {
            if (!childParents.Contains(parent)) continue;
            bool otherUnion = Get(byPerson, parent).Any(u => IsUnion(u) && Other(u, parent) != first && Other(u, parent) != second && childParents.Contains(Other(u, parent)));
            if (!otherUnion) { eligible = true; break; }
          }
          if (!eligible) continue;
        }
        int order;
        rows.Add(With(people[id], "tree_insertion_order", insertion.TryGetValue(id, out order) ? order.ToString(CultureInfo.InvariantCulture) : null));
      }
      return OrderSiblings(rows);
    }

    // A birth position is a slot: position 4 shows 4th. Children without one fill the free
    // slots in the order they were added to the tree; positions past the end follow in order.
    internal static List<Dictionary<string, string> > OrderSiblings(IEnumerable<Dictionary<string, string> > rows) {
      List<Dictionary<string, string> > added = rows.OrderBy(r => S(r, "tree_insertion_order") == "" ? Int64.MaxValue : I(r, "tree_insertion_order")).ThenBy(r => I(r, "id")).ToList();
      Queue<Dictionary<string, string> > ordered = new Queue<Dictionary<string, string> >(added.Where(r => I(r, "sibling_birth_order") > 0).OrderBy(r => I(r, "sibling_birth_order")));
      Queue<Dictionary<string, string> > free = new Queue<Dictionary<string, string> >(added.Where(r => I(r, "sibling_birth_order") < 1));
      List<Dictionary<string, string> > output = new List<Dictionary<string, string> >();
      while (ordered.Count > 0 || free.Count > 0)
        output.Add(ordered.Count > 0 && (I(ordered.Peek(), "sibling_birth_order") <= output.Count + 1 || free.Count == 0) ? ordered.Dequeue() : free.Dequeue());
      return output;
    }

    internal List<int> ParentIds(int id) { return new List<int>(Get(parents, id)); }
    internal bool HasParents(int id) { return Get(parents, id).Count > 0; }
    internal bool CanAddParents(int id) {
      return Get(parents, id).Select(p => S(Person(p), "gender")).Where(g => g == "M" || g == "F").Distinct().Count() < 2;
    }
    internal bool HasSiblings(int id) { return Get(parents, id).Any(p => Get(children, p).Count > 1); }
  }

  internal sealed class FamilyTreeRenderer {
    private static readonly Dictionary<string, string> French = new Dictionary<string, string> {
      { "Hidden person", "Personne masquée" }, { "Private person", "Personne privée" }, { "Unknown", "Inconnue" },
      { "Birth position", "Rang de naissance" }, { "Add Spouse", "Ajouter un conjoint" }, { "Add Partner", "Ajouter un partenaire" },
      { "Add Parents", "Ajouter des parents" }, { "Add Child", "Ajouter un enfant" }, { "Make Main Partner", "Définir comme partenaire principal" },
      { "Change Partner", "Changer de partenaire" },
      { "Move this external partner and their children to the other person in this couple.", "Déplacer ce partenaire externe et ses enfants vers l’autre personne de ce couple." },
      { "Move", "Déplacer" }, { "Birthday today", "Anniversaire aujourd’hui" }, { "Birthday tomorrow", "Anniversaire demain" }
    };
    private const string PlusSvg = "<svg class='svg-icon' viewBox='0 0 20 20'><path d='M15.898,4.045c-0.271-0.272-0.713-0.272-0.986,0l-4.71,4.711L5.493,4.045c-0.272-0.272-0.714-0.272-0.986,0s-0.272,0.714,0,0.986l4.709,4.711l-4.71,4.711c-0.272,0.271-0.272,0.713,0,0.986c0.136,0.136,0.314,0.203,0.492,0.203c0.179,0,0.357-0.067,0.493-0.203l4.711-4.711l4.71,4.711c0.137,0.136,0.314,0.203,0.494,0.203c0.178,0,0.355-0.067,0.492-0.203c0.273-0.273,0.273-0.715,0-0.986l-4.711-4.711l4.711-4.711C16.172,4.759,16.172,4.317,15.898,4.045z'></path></svg>";
    private const string BirthdayPath = "<path d=\"M16.557,4.467h-1.64v-0.82c0-0.225-0.183-0.41-0.409-0.41c-0.226,0-0.41,0.185-0.41,0.41v0.82H5.901v-0.82c0-0.225-0.185-0.41-0.41-0.41c-0.226,0-0.41,0.185-0.41,0.41v0.82H3.442c-0.904,0-1.64,0.735-1.64,1.639v9.017c0,0.904,0.736,1.64,1.64,1.64h13.114c0.904,0,1.64-0.735,1.64-1.64V6.106C18.196,5.203,17.461,4.467,16.557,4.467 M17.377,15.123c0,0.453-0.366,0.819-0.82,0.819H3.442c-0.453,0-0.82-0.366-0.82-0.819V8.976h14.754V15.123z M17.377,8.156H2.623V6.106c0-0.453,0.367-0.82,0.82-0.82h1.639v1.23c0,0.225,0.184,0.41,0.41,0.41c0.225,0,0.41-0.185,0.41-0.41v-1.23h8.196v1.23c0,0.225,0.185,0.41,0.41,0.41c0.227,0,0.409-0.185,0.409-0.41v-1.23h1.64c0.454,0,0.82,0.367,0.82,0.82V8.156z\"></path>";

    private readonly FamilyTreeGraph graph;
    private readonly bool canEditTree, french;
    private readonly int partialRelationshipId;
    private readonly DateTime today;
    private readonly Func<string, string, string> imageUrl;
    private int partialAnchorPersonId;
    private readonly HashSet<int> renderedPersonIds = new HashSet<int>(), renderedExternalPartnerIds = new HashSet<int>(), activeBranchPersonIds = new HashSet<int>();

    // imageUrl(image_name, gender) returns the final small-photo URL, version suffix included.
    internal FamilyTreeRenderer(FamilyTreeGraph graph, bool canEditTree, int partialRelationshipId, string lang, DateTime today, Func<string, string, string> imageUrl) {
      this.graph = graph; this.canEditTree = canEditTree; this.partialRelationshipId = Math.Max(0, partialRelationshipId);
      french = lang == "fr"; this.today = today.Date; this.imageUrl = imageUrl;
    }

    internal IEnumerable<int> RenderedPersonIds { get { return renderedPersonIds; } }
    internal bool IsRenderedExternalPartner(int personId) { return renderedExternalPartnerIds.Contains(personId); }

    private static int I(Dictionary<string, string> row, string key) { return FamilyTreeGraph.I(row, key); }
    private static string S(Dictionary<string, string> row, string key) { return FamilyTreeGraph.S(row, key); }
    private static string N(int value) { return value.ToString(CultureInfo.InvariantCulture); }
    // PHP htmlspecialchars(ENT_QUOTES) and trim().
    internal static string H(string value) { return (value ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("'", "&#039;").Replace("<", "&lt;").Replace(">", "&gt;"); }
    private static string Trim(string value) { return (value ?? "").Trim(' ', '\t', '\n', '\r', '\0', '\x0B'); }
    private string T(string key) { string value; return H(french && French.TryGetValue(key, out value) ? value : key); }

    private static bool IsDeceased(Dictionary<string, string> person) {
      string death = Trim(S(person, "death_date"));
      return I(person, "is_deceased") == 1 || (death != "" && death != "0000-00-00");
    }

    private static bool UnknownIdentity(Dictionary<string, string> person) {
      // Earlier versions stored the translated placeholder as the first name.
      string first = Trim(S(person, "first_name")).ToLowerInvariant();
      return Trim(S(person, "last_name")) == "" && (first == "unknown person" || first == "unknown" || first == "personne inconnue" || first == "inconnu" || first == "inconnue");
    }

    private string Name(Dictionary<string, string> person) {
      return UnknownIdentity(person) ? T("Unknown") : H(Trim(Trim(S(person, "first_name")) + " " + Trim(S(person, "last_name"))));
    }

    private string Image(Dictionary<string, string> person) { return imageUrl(S(person, "image_name"), S(person, "gender")); }

    // racinage_person_birthday_status + racinage_person_birthday_icon. Birthdays are calendar dates, never shifted.
    private string BirthdayIcon(Dictionary<string, string> person) {
      if (IsDeceased(person)) return "";
      Match m = Regex.Match(Trim(S(person, "birth_date")), @"^(\d{4})-(\d{2})-(\d{2})$");
      if (!m.Success) return "";
      int year = Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), month = Int32.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), day = Int32.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
      if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year == 0 ? 2000 : year, month)) return "";
      string status = month == today.Month && day == today.Day ? "today" : (month == today.AddDays(1).Month && day == today.AddDays(1).Day ? "tomorrow" : "");
      if (status == "") return "";
      string label = T(status == "today" ? "Birthday today" : "Birthday tomorrow");
      return "<svg class=\"person_birthday_icon " + status + "\" viewBox=\"0 0 20 20\" role=\"img\" aria-label=\"" + label + "\"><title>" + label + "</title>" + BirthdayPath + "</svg>";
    }

    private string PlusButton(string cssClass, string gender, string title) {
      return "\n                <div class='" + cssClass + " " + gender + "' title='" + T(title) + "'>\n                  <div class='plus_btn'>" + PlusSvg + "</div>\n                </div>";
    }

    internal string RenderPerson(Dictionary<string, string> person, string cssClass, string noSpouse, string noPartner, bool canAddParents, bool canAddPartner) {
      string deadClass = IsDeceased(person) ? "dead" : "";
      string hiddenClass = (S(person, "visibility") == "" ? "visible" : person["visibility"]) == "hidden" ? "tree_person_hidden" : "";
      string allClasses = Trim("one_person " + cssClass + " " + deadClass + " " + hiddenClass + " " + (noSpouse ?? "") + " " + (noPartner ?? ""));
      string imagePath = Image(person), gender = S(person, "gender"), name = Name(person);
      if (UnknownIdentity(person)) allClasses += " tree_identity_unknown";
      int personId = I(person, "id");
      renderedPersonIds.Add(personId);
      string birthdayIcon = BirthdayIcon(person);
      bool isSibling = Regex.Split(Trim(cssClass), @"\s+").Contains("sibling");
      int birthOrder = I(person, "sibling_birth_order");
      string birthOrderBadge = isSibling && birthOrder > 0
        ? "<span class='tree_sibling_birth_order' title='" + T("Birth position") + "' aria-label='" + T("Birth position") + ": " + N(birthOrder) + "'>" + N(birthOrder) + "</span>"
        : "";
      string editBtns = "";
      if (canEditTree) {
        string spouseBtn = noSpouse == "no_spouse" ? PlusButton("add_spouse", gender, "Add Spouse") : "";
        string partnerBtn = canAddPartner ? PlusButton("add_partner", gender, "Add Partner") : "";
        editBtns = "\n                " + (canAddParents ? PlusButton("add_parents", gender, "Add Parents") : "") + "\n                " + spouseBtn + "\n                " + partnerBtn;
      }
      string personLongId = H(S(person, "long_id"));
      return "\n        <div class='" + allClasses + "' title='" + name + "' data-person-id='" + N(personId) + "' data-person-long-id='" + personLongId + "' data-person-gender='" + gender + "' data-sibling-birth-order='" + (birthOrder > 0 ? N(birthOrder) : "") + "'>"
        + "\n            <img src='" + imagePath + "' loading='lazy' decoding='async' width='60' height='60'>"
        + "\n            " + birthdayIcon
        + "\n            " + birthOrderBadge
        + "\n            <span class='limit_lines l1 align_center'>" + name + "</span>"
        + "\n            " + editBtns
        + "\n        </div>";
    }

    // Merge a CSS class into the opening <li ...> tag of an HTML fragment.
    private static string AddClassToFirstLi(string html, string newClass) {
      return new Regex("^<li([^>]*)>").Replace(html, m => {
        Match cm = Regex.Match(m.Groups[1].Value, "class=\"([^\"]*)\"");
        return cm.Success ? "<li class=\"" + Trim(cm.Groups[1].Value + " " + newClass) + "\">" : "<li class=\"" + newClass + "\">";
      }, 1);
    }

    // External partners are shown with a single (first) name only.
    private string ExternalPartnerName(Dictionary<string, string> partner) {
      if (UnknownIdentity(partner)) return Name(partner);
      string first = Trim(S(partner, "first_name"));
      return H(first != "" ? first : Trim(S(partner, "last_name")));
    }

    private string RenderExternalPartner(Dictionary<string, string> partner, string cssClass, int associatedParentId, HashSet<int> mainChildIds, int branchAnchor, int mainRelationshipId) {
      string imagePath = Image(partner), fullName = Name(partner), name = ExternalPartnerName(partner);
      string identityClass = UnknownIdentity(partner) ? " tree_identity_unknown" : "";
      int partnerId = I(partner, "id");
      if (activeBranchPersonIds.Contains(partnerId)) return "";
      activeBranchPersonIds.Add(partnerId);
      renderedPersonIds.Add(partnerId);
      renderedExternalPartnerIds.Add(partnerId);
      string partnerLongId = H(S(partner, "long_id"));
      string birthdayIcon = BirthdayIcon(partner);
      if (IsDeceased(partner)) identityClass += " dead";
      StringBuilder html = new StringBuilder();
      html.Append("<li class=\"external_partner " + cssClass + "\" data-associated-parent-id=\"" + N(associatedParentId) + "\">");
      html.Append("<div class='one_person partner" + identityClass + "' title='" + fullName + "' data-person-id='" + N(partnerId) + "' data-person-long-id='" + partnerLongId + "' data-person-gender='" + H(S(partner, "gender")) + "'>");
      html.Append("<img src='" + imagePath + "' loading='lazy' decoding='async' width='60' height='60'>");
      html.Append(birthdayIcon);
      html.Append("<span>" + name + "</span>");
      int relationshipId = I(partner, "relationship_id");
      int[] targetIds = { partnerId, associatedParentId }; Array.Sort(targetIds);
      string targetPair = String.Join(",", targetIds.Select(N));
      Dictionary<string, string> mainUnion = canEditTree && mainRelationshipId > 0 ? graph.Union(mainRelationshipId) : null;
      int[] sourceIds = mainUnion != null ? new[] { I(mainUnion, "person1_id"), I(mainUnion, "person2_id") } : new int[0]; Array.Sort(sourceIds);
      string sourcePair = String.Join(",", sourceIds.Select(N));
      if (canEditTree && relationshipId > 0 && mainRelationshipId > 0 && !(partialRelationshipId > 0 && branchAnchor == partialAnchorPersonId)) {
        string common = "data-anchor-person-id='" + N(branchAnchor) + "' data-source-relationship-id='" + N(mainRelationshipId) + "' data-source-person-ids='" + sourcePair + "' data-target-person-ids='" + targetPair + "' data-relationship-id='" + N(relationshipId) + "'";
        html.Append("<template data-tree-person-actions data-person-id='" + N(partnerId) + "'><button type='button' class='button1 secondary' data-tree-branch-action='promote_partner' data-person-action='promote_partner' " + common + ">" + T("Make Main Partner") + "</button><button type='button' class='button1 secondary' data-tree-branch-action='change_partner' data-person-action='change_partner' " + common + " title='" + T("Move this external partner and their children to the other person in this couple.") + "'>" + T("Change Partner") + "</button></template>");
      }
      // Register a child under this external partner and their couple member.
      if (canEditTree) {
        html.Append("<div class='add_child external_add_child' title='" + T("Add Child") + "' data-person-id='" + N(partnerId) + "' data-second-parent-id='" + N(associatedParentId) + "'>"
          + "<div class='plus_btn'>" + PlusSvg + "</div></div>");
      }
      html.Append("</div>");

      List<Dictionary<string, string> > partnerChildren = graph.CoupleChildren(associatedParentId, partnerId);
      List<Dictionary<string, string> > otherPartners = graph.Partners(partnerId, relationshipId);
      if (partnerChildren.Count > 0 || otherPartners.Count > 0) {
        html.Append("<ul class=\"first_ul\">");
        HashSet<int> externalChildIds = new HashSet<int>(mainChildIds);
        foreach (Dictionary<string, string> child in partnerChildren) {
          int childId = I(child, "id");
          if (mainChildIds.Contains(childId)) continue;
          externalChildIds.Add(childId);
          string button = "";
          if (canEditTree && relationshipId > 0) {
            List<int> parentIds = graph.ParentIds(childId); parentIds.Sort();
            button = "<template data-tree-person-actions data-person-id='" + N(childId) + "'><button type='button' class='button1 secondary' data-tree-branch-action='move_child' data-person-action='move_child' data-person-id='" + N(childId) + "' data-expected-parent-ids='" + String.Join(",", parentIds.Select(N)) + "' data-source-person-ids='" + targetPair + "' data-source-relationship-id='" + N(relationshipId) + "'>" + T("Move") + "</button></template>";
          }
          html.Append(BuildTree(childId, false, button));
        }
        foreach (Dictionary<string, string> other in otherPartners) {
          if (activeBranchPersonIds.Contains(I(other, "id"))) continue;
          html.Append(RenderExternalPartner(other, cssClass, partnerId, externalChildIds, branchAnchor, mainRelationshipId));
        }
        html.Append("</ul>");
      }
      html.Append("</li>");
      activeBranchPersonIds.Remove(partnerId);
      return html.ToString();
    }

    internal string BuildTree(int personId, bool isFirstCall = true, string branchAction = "") {
      if (isFirstCall) {
        partialAnchorPersonId = personId;
        renderedPersonIds.Clear(); renderedExternalPartnerIds.Clear(); activeBranchPersonIds.Clear();
      }
      if (activeBranchPersonIds.Contains(personId)) return "";
      int branchAnchor = personId;
      Dictionary<string, string> person = graph.Person(personId);
      if (person == null) return "";
      int preferred = I(person, "preferred_relationship_id"), preferredAnchor = 0;
      if (preferred > 0 && !(isFirstCall && partialRelationshipId > 0)) {
        Dictionary<string, string> union = graph.Union(preferred);
        if (union != null) {
          Dictionary<string, string> pivot = graph.Person(I(union, "person1_id"));
          if (pivot != null && graph.Spouse(I(pivot, "id"), preferred) != null) { person = pivot; personId = I(pivot, "id"); preferredAnchor = preferred; }
        }
      }
      if (activeBranchPersonIds.Contains(personId)) return "";
      activeBranchPersonIds.Add(personId);

      // Detect only-child root (no parent, no siblings)
      string rootClass = isFirstCall && !graph.HasSiblings(personId) ? " class=\"only_child_root\"" : "";
      StringBuilder html = new StringBuilder("<li" + rootClass + ">");
      Dictionary<string, string> spouse = graph.Spouse(personId, isFirstCall && partialRelationshipId > 0 ? partialRelationshipId : preferredAnchor);
      if ((spouse != null && activeBranchPersonIds.Contains(I(spouse, "id"))) || (isFirstCall && partialRelationshipId > 0 && spouse == null)) {
        activeBranchPersonIds.Remove(personId);
        return "";
      }
      if (spouse != null) {
        activeBranchPersonIds.Add(I(spouse, "id"));
        html.Append("<div class=\"couple\" data-relationship-id=\"" + N(I(spouse, "relationship_id")) + "\">");
        Dictionary<string, string> firstPerson = person, secondPerson = spouse;
        if (S(person, "gender") == "F" && S(spouse, "gender") == "M") { firstPerson = spouse; secondPerson = person; }
        int siblingPersonId = I(spouse, "relationship_sibling_person_id");
        if (siblingPersonId <= 0) siblingPersonId = I(spouse, "relationship_person1_id");
        bool firstIsChild = siblingPersonId > 0 ? I(firstPerson, "id") == siblingPersonId : graph.HasParents(I(firstPerson, "id"));
        bool secondIsChild = siblingPersonId > 0 ? I(secondPerson, "id") == siblingPersonId : graph.HasParents(I(secondPerson, "id"));
        html.Append(RenderPerson(firstPerson, firstIsChild ? "sibling" : "", null, "no_partner", isFirstCall && graph.CanAddParents(I(firstPerson, "id")), true));
        html.Append(RenderPerson(secondPerson, "wife" + (secondIsChild ? " sibling" : ""), null, "no_partner", isFirstCall && graph.CanAddParents(I(secondPerson, "id")), true));
        if (canEditTree) {
          html.Append("\n                    <div class='add_child' title='" + T("Add Child") + "' data-person-id='" + S(firstPerson, "id") + "' data-second-parent-id='" + S(secondPerson, "id") + "'>\n                        <div class='plus_btn'>" + PlusSvg + "</div>\n                    </div>");
        }
        html.Append("</div>");
      } else {
        html.Append(RenderPerson(person, graph.HasParents(I(person, "id")) ? "sibling" : "", "no_spouse", null, isFirstCall && graph.CanAddParents(I(person, "id")), false));
      }
      html.Append(branchAction);

      List<Dictionary<string, string> > childrenArray = graph.CoupleChildren(personId, spouse != null ? I(spouse, "id") : personId);
      HashSet<int> mainChildIds = new HashSet<int>(childrenArray.Select(c => I(c, "id")));
      int mainRelationshipId = I(spouse, "relationship_id");
      // A Partial Tree keeps the anchored couple focused, but still includes
      // both members' other partner branches exactly as the canonical tree does.
      List<Dictionary<string, string> > partnersMain = graph.Partners(personId, mainRelationshipId);
      List<Dictionary<string, string> > partnersSpouse = spouse != null ? graph.Partners(I(spouse, "id"), mainRelationshipId) : new List<Dictionary<string, string> >();
      int personParent = I(person, "id"), spouseParent = I(spouse, "id");
      // Left = husband's partners, Right = wife's partners
      List<KeyValuePair<Dictionary<string, string>, int> > leftPartners, rightPartners;
      if (S(person, "gender") == "M") {
        leftPartners = partnersMain.Select(p => new KeyValuePair<Dictionary<string, string>, int>(p, personParent)).ToList();
        rightPartners = partnersSpouse.Select(p => new KeyValuePair<Dictionary<string, string>, int>(p, spouseParent)).ToList();
      } else {
        rightPartners = partnersMain.Select(p => new KeyValuePair<Dictionary<string, string>, int>(p, personParent)).ToList();
        leftPartners = partnersSpouse.Select(p => new KeyValuePair<Dictionary<string, string>, int>(p, spouseParent)).ToList();
      }

      if (childrenArray.Count > 0 || leftPartners.Count > 0 || rightPartners.Count > 0) {
        html.Append("<ul>");
        foreach (KeyValuePair<Dictionary<string, string>, int> partnerData in leftPartners)
          html.Append(RenderExternalPartner(partnerData.Key, "husband", partnerData.Value, mainChildIds, branchAnchor, mainRelationshipId));
        for (int index = 0; index < childrenArray.Count; index++) {
          string childHtml = BuildTree(I(childrenArray[index], "id"), false);
          if (leftPartners.Count > 0 && index == 0) childHtml = AddClassToFirstLi(childHtml, "first_child_after_external");
          if (rightPartners.Count > 0 && index == childrenArray.Count - 1) childHtml = AddClassToFirstLi(childHtml, "first_child_before_external");
          html.Append(childHtml);
        }
        foreach (KeyValuePair<Dictionary<string, string>, int> partnerData in rightPartners)
          html.Append(RenderExternalPartner(partnerData.Key, "wife", partnerData.Value, mainChildIds, branchAnchor, mainRelationshipId));
        html.Append("</ul>");
      }
      html.Append("</li>");
      activeBranchPersonIds.Remove(personId);
      if (spouse != null) activeBranchPersonIds.Remove(I(spouse, "id"));
      return html.ToString();
    }
  }
}

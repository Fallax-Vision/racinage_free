using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// Golden markup check for native-host/FamilyTree.cs against hosted PHP output, plus the local tree migration.
// Run through run-family-tree-checks.ps1. Exits non-zero on the first failing area.
namespace RacinageFreeDesktop {
  internal static partial class FamilyTreeSelfCheck {
    private static int failures;

    private static int Main(string[] args) {
      if (args.Length > 0 && args[0] == "--serve") return Serve(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : "");
      string fixtures = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
      try {
        CheckGolden(fixtures);
        CheckApi();
        CheckMigration(Path.Combine(Path.GetTempPath(), "racinage-free-tree-check-" + Guid.NewGuid().ToString("N") + ".sqlite"), true);
        if (args.Length > 1) CheckMigration(args[1], false);
      } catch (Exception error) { Console.WriteLine("ERROR " + error); return 2; }
      Console.WriteLine(failures == 0 ? "All family tree checks passed." : failures + " family tree check(s) failed.");
      return failures == 0 ? 0 : 1;
    }

    internal static void Check(bool ok, string label) { if (!ok) { failures++; Console.WriteLine("FAIL " + label); } }

    private static Dictionary<string, string> Row(object value) {
      return ((Dictionary<string, object>)value).ToDictionary(p => p.Key, p => p.Value == null ? null : Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string FixtureImage(string image, string gender) {
      string name = Path.GetFileNameWithoutExtension(Path.GetFileName(image ?? ""));
      if (name == "") return "/media/images/" + (gender.ToLowerInvariant() == "f" || gender.ToLowerInvariant() == "female" ? "default_female.webp" : "default_male.webp");
      return "/media/images/persons/" + Uri.EscapeDataString(name + "_sm.webp");
    }

    private static void CheckGolden(string folder) {
      JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
      int cases = 0;
      string[] files = Directory.GetFiles(folder, "*.json");
      Check(files.Length > 0, "golden fixtures exist in " + folder);
      foreach (string file in files) {
        Dictionary<string, object> fixture = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8));
        List<Dictionary<string, string> > people = ((object[])fixture["people"]).Cast<object>().Select(Row).ToList();
        List<Dictionary<string, string> > unions = ((object[])fixture["unions"]).Cast<object>().Select(Row).ToList();
        List<Dictionary<string, string> > links = ((object[])fixture["links"]).Cast<object>().Select(Row).ToList();
        DateTime today = DateTime.ParseExact((string)fixture["today"], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (Dictionary<string, object> c in ((object[])fixture["cases"]).Cast<Dictionary<string, object> >()) {
          FamilyTreeGraph graph = new FamilyTreeGraph(people, unions, links, (int)c["primary_relationship_id"]);
          string actual = new FamilyTreeRenderer(graph, (bool)c["can_edit"], (int)c["partial_relationship_id"], (string)c["lang"], today, FixtureImage).BuildTree((int)c["root_person_id"]);
          string expected = (string)c["html"];
          cases++;
          if (actual == expected) continue;
          int at = 0;
          while (at < Math.Min(actual.Length, expected.Length) && actual[at] == expected[at]) at++;
          int from = Math.Max(0, at - 120);
          Check(false, Path.GetFileName(file) + " [" + c["label"] + "] differs at " + at + "\n  php: " + Snip(expected, from) + "\n  c#:  " + Snip(actual, from));
        }
      }
      Console.WriteLine("Golden markup: " + cases + " cases in " + files.Length + " fixtures.");
    }

    private static string Snip(string value, int from) { return from >= value.Length ? "(end)" : value.Substring(from, Math.Min(240, value.Length - from)).Replace("\n", "\\n"); }

    private static void CheckMigration(string path, bool seed) {
      using (SqliteDb db = new SqliteDb(path)) {
        // Same legacy table as LocalStore.Initialize.
        db.Exec("CREATE TABLE IF NOT EXISTS people (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, relationship TEXT NOT NULL DEFAULT '', birth_date TEXT NOT NULL DEFAULT '', place TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '', deleted_at TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)");
        if (seed) {
          string now = "2026-11-06T00:00:00.0000000Z";
          db.Execute("INSERT INTO people(full_name,relationship,birth_date,place,notes,created_at,updated_at)VALUES(?,?,?,?,?,?,?)", "  Jean  Paul Dupont ", "Grandfather", "1940-05-01", "Lyon", "Served in the navy", now, now);
          db.Execute("INSERT INTO people(full_name,relationship,birth_date,place,notes,created_at,updated_at)VALUES(?,?,?,?,?,?,?)", "Amélie", "", "", "", "", now, now);
          db.Execute("INSERT INTO people(full_name,relationship,birth_date,place,notes,deleted_at,created_at,updated_at)VALUES(?,?,?,?,?,?,?,?)", "Gone Person", "", "", "", "", now, now, now);
          db.Execute("INSERT INTO people(full_name,relationship,birth_date,place,notes,created_at,updated_at)VALUES(?,?,?,?,?,?,?)", "Mary Ann", "", "", "", "Notes only", now, now);
        }
        int legacy = Count(db, "SELECT COUNT(*) FROM people WHERE deleted_at IS NULL");
        int before = TableExists(db, "persons") ? Count(db, "SELECT COUNT(*) FROM persons") : 0;
        for (int run = 0; run < 3; run++) LocalFamilyTreeSchema.Apply(db);
        Check(Count(db, "SELECT COUNT(*) FROM local_legacy_people_migrations") == legacy, "every live legacy person is mapped once (" + path + ")");
        Check(Count(db, "SELECT COUNT(*) FROM persons") - before <= legacy, "reruns do not duplicate persons");
        Check(Count(db, "SELECT COUNT(*) FROM family_trees") == 0 || !seed, "migrated people stay unplaced");
        Check(db.QueryOne("PRAGMA foreign_key_check") == null && (string)db.Scalar("PRAGMA integrity_check") == "ok", "integrity after migration");
        if (!seed) { Console.WriteLine("Migration on " + path + ": " + legacy + " legacy people mapped."); return; }

        Dictionary<string, string> jean = db.QueryOne("SELECT * FROM persons WHERE legacy_people_id = 1");
        Check(jean["first_name"] == "Jean  Paul" && jean["last_name"] == "Dupont" && jean["birth_date"] == "1940-05-01" && jean["birth_place"] == "Lyon", "name split on last space with place and birth date");
        Check(jean["migration_note"] == "Legacy relationship: Grandfather\nLegacy notes: Served in the navy", "relationship and notes kept in migration note");
        Dictionary<string, string> amelie = db.QueryOne("SELECT * FROM persons WHERE legacy_people_id = 2");
        Check(amelie["first_name"] == "Amélie" && amelie["last_name"] == "" && amelie["birth_date"] == "" && amelie["migration_note"] == "", "single name keeps first name only");
        Check(db.QueryOne("SELECT id FROM persons WHERE legacy_people_id = 3") == null, "deleted legacy people are not migrated");
        Check(db.QueryOne("SELECT migration_note FROM persons WHERE legacy_people_id = 4")["migration_note"] == "Legacy notes: Notes only", "notes-only migration note");
        Check(Count(db, "SELECT COUNT(*) FROM people") == 4, "legacy people table left in place");

        db.Execute("DELETE FROM persons WHERE legacy_people_id = 2");
        db.Execute("INSERT INTO people(full_name,created_at,updated_at)VALUES('Late Addition','x','x')");
        LocalFamilyTreeSchema.Apply(db);
        Check(db.QueryOne("SELECT id FROM persons WHERE legacy_people_id = 2") == null, "a deleted migrated person is not brought back");
        Check(Count(db, "SELECT COUNT(*) FROM persons WHERE legacy_people_id = 5") == 1, "people added later are migrated on the next start");
        Console.WriteLine("Migration: seeded legacy people migrated idempotently.");
      }
      File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
    }

    private static bool TableExists(SqliteDb db, string table) { return db.QueryOne("SELECT name FROM sqlite_master WHERE type='table' AND name=?", table) != null; }
    private static int Count(SqliteDb db, string sql) { return Int32.Parse((string)db.Scalar(sql)); }
  }
}

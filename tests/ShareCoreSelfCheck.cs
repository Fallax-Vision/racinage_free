using System;
using RacinageFreeDesktop;

internal static class ShareCoreSelfCheck {
  private static int Main() {
    try {
      PortableShareActions contract = new PortableShareActions {
        contract_version = 1,
        actions = new[] {
          new PortableShareAction {
            id = "import_recipe_source",
            accepts = new[] { "url" },
            label = new PortableLocalizedText { en = "Import recipe source", fr = "Importer une source de recette" },
            description = new PortableLocalizedText { en = "Queue this link.", fr = "Mettre ce lien en file." },
            target_kind = "plugin_workspace"
          }
        }
      };
      Require(LocalShareContract.SerializeValidated("kitchen-planner", contract).Contains("import_recipe_source"), "valid share contract rejected");
      contract.actions[0].id = "Bad-ID";
      Require(LocalShareContract.SerializeValidated("kitchen-planner", contract) == "", "invalid action id accepted");
      Require(LocalShareContract.ExtractHttpUrl("Recipe: https://example.com/pasta#story") == "https://example.com/pasta", "URL extraction or fragment removal failed");

      string structured = "<html lang='en'><script type='application/ld+json'>{\"@type\":\"Recipe\",\"name\":\"Tomato Pasta\",\"recipeYield\":\"2 servings\",\"recipeIngredient\":[\"200 g pasta\",\"2 tomatoes\"],\"recipeInstructions\":[{\"@type\":\"HowToStep\",\"text\":\"Boil the pasta.\"},{\"@type\":\"HowToStep\",\"text\":\"Add tomatoes.\"}]}</script></html>";
      TraditionalRecipeResult recipe = TraditionalRecipeExtractor.Extract(structured, "text/html", "https://example.com/pasta");
      Require(recipe.Structured && recipe.Title == "Tomato Pasta" && recipe.Ingredients.Count == 2 && recipe.Steps.Count == 2 && recipe.Confidence == "high", "JSON-LD Recipe extraction failed");

      string semantic = "<html lang='fr'><h1>Soupe</h1><h2>Ingrédients</h2><ul><li>2 tomates</li><li>1 l eau</li></ul><h2>Préparation</h2><ol><li>Couper les tomates.</li><li>Cuire 20 minutes.</li></ol></html>";
      recipe = TraditionalRecipeExtractor.Extract(semantic, "text/html", "https://example.com/soupe");
      Require(!recipe.Structured && recipe.Title == "Soupe" && recipe.Ingredients.Count == 2 && recipe.Steps.Count == 2 && recipe.SourceLanguage == "fr", "semantic multilingual extraction failed");

      string nonFood = "<script type='application/ld+json'>{\"@type\":\"SoftwareApplication\",\"name\":\"Tool\"}</script>";
      recipe = TraditionalRecipeExtractor.Extract(nonFood, "text/html", "https://example.com/tool");
      Require(recipe.ExplicitNonFood, "explicit non-food classification failed");
      Console.WriteLine("ShareCoreSelfCheck passed");
      return 0;
    } catch (Exception error) {
      Console.Error.WriteLine(error.Message);
      return 1;
    }
  }

  private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

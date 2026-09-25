namespace eThangAgent.SkillDomain;

/// <summary>One description-lint finding for a skill's trigger description
/// (vault move 6b): deterministic checks over the documented authoring rules -
/// third person, what+when-to-use coverage, front-loaded key terms. A finding
/// names its rule so tooling can explain the fix.</summary>
public sealed record SkillDescriptionFinding(string Rule, string Message);

/// <summary>Lints one skill description against the documented trigger rules.
/// Pure and deterministic: no store, no model, no I/O. Empty result = clean.
/// The rules (Anthropic authoring guidance, echoed in the vault's evidence
/// note): write in third person; say what it does AND when to use it;
/// front-load the key terms instead of boilerplate openers.</summary>
public static class SkillDescriptionLinter
{
  private static readonly string[] FirstPersonMarkers =
  [
    "I ", "I'", "MY ", "WE ", "OUR ", "ME ",
  ];

  private static readonly string[] BoilerplateOpeners =
  [
    "A SKILL FOR ", "A SKILL TO ", "THIS SKILL ", "THIS IS ", "THE SKILL ",
  ];

  private static readonly string[] WhenClauses =
  [
    "WHEN TO USE", "USE WHEN", "USE THIS WHEN", "USE FOR", "WHEN THE USER",
    "WHEN A ", "WHEN AN ", "WHENEVER ", "BEFORE ", "AFTER ",
  ];

  public static IReadOnlyList<SkillDescriptionFinding> Lint(string description)
  {
    ArgumentNullException.ThrowIfNull(description);
    if (description.Trim().Length == 0)
    {
      return [new SkillDescriptionFinding("Empty", "Description is empty.")];
    }

    List<SkillDescriptionFinding> findings = [];
    string upper = description.ToUpperInvariant();

    foreach (string marker in FirstPersonMarkers)
    {
      int at = upper.IndexOf(marker, StringComparison.Ordinal);
      if (at >= 0)
      {
        findings.Add(new SkillDescriptionFinding(
            "ThirdPerson",
            $"First-person marker '{marker.TrimEnd()}' at char {at}: write in third person."));
        break;
      }
    }

    if (!WhenClauses.Any(upper.Contains))
    {
      findings.Add(new SkillDescriptionFinding(
          "WhatAndWhen",
          "Say what it does AND when to use it (add a 'use when ...' clause)."));
    }

    if (BoilerplateOpeners.FirstOrDefault(upper.StartsWith) is { } matched)
    {
      findings.Add(new SkillDescriptionFinding(
          "FrontLoad",
          $"Opens with boilerplate '{matched.TrimEnd()}': front-load the skill's key terms instead."));
    }

    return findings;
  }
}

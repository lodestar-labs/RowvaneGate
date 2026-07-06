using Rowvane.Gate.Rulesets;

namespace Rowvane.Gate.Tests;

[TestFixture]
public class RulesetTests
{
    [Test]
    public void Valid_ruleset_passes_validation()
    {
        Assert.That(TestData.Trips().Validate(), Is.Empty);
    }

    [Test]
    public void Serializer_round_trips_polymorphic_checks()
    {
        var original = TestData.Trips();
        original.Rules.Add(new Rule
        {
            Entity = "HL",
            Field = "Weight",
            Check = new RangeCheck { Min = 0, Max = 5000 },
            When = new RuleCondition { Field = "Gear", OneOf = ["OTB"] },
        });
        original.Rules.Add(new Rule
        {
            Entity = "TR",
            Check = new UniqueCheck { Fields = ["TripId"], Scope = UniqueScope.Dataset },
        });

        var json = RulesetSerializer.Serialize(original);
        var restored = RulesetSerializer.Deserialize(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"type\": \"range\""));
            Assert.That(restored.Rules.OfType<Rule>().Select(r => r.Check.GetType().Name),
                Is.EquivalentTo(new[] { "RequiredCheck", "TypeCheck", "RangeCheck", "UniqueCheck" }));
            Assert.That(restored.Rules.First(r => r.Check is RangeCheck).When!.OneOf, Is.EqualTo(new[] { "OTB" }));
        });
    }

    [Test]
    public void Rule_ids_are_assigned_and_stable()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule { Entity = "TR", Field = "Vessel", Check = new RequiredCheck() });
        ruleset.AssignRuleIds();

        Assert.Multiple(() =>
        {
            Assert.That(ruleset.Rules[0].Id, Is.EqualTo("TRIP-ID"), "explicit ids are kept");
            Assert.That(ruleset.Rules[2].Id, Is.EqualTo("R001"), "generated ids fill the gaps");
        });
    }

    [Test]
    public void Unknown_entity_and_field_are_reported()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule { Entity = "NOPE", Field = "X", Check = new RequiredCheck() });
        ruleset.Rules.Add(new Rule { Entity = "TR", Field = "Missing", Check = new RequiredCheck() });

        var errors = ruleset.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("unknown entity 'NOPE'"));
            Assert.That(errors, Has.Some.Contains("'Missing' does not exist"));
        });
    }

    [Test]
    public void Check_specific_validation_runs()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule { Entity = "TR", Field = "TripId", Check = new RangeCheck() });
        ruleset.Rules.Add(new Rule { Entity = "TR", Check = new UniqueCheck() });
        ruleset.Rules.Add(new Rule
        {
            Entity = "TR",
            Field = "TotalWeight",
            Check = new AggregateCheck { ChildEntity = "SA", ChildField = "x" },
        });

        var errors = ruleset.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("requires 'min' and/or 'max'"));
            Assert.That(errors, Has.Some.Contains("at least one field"));
            Assert.That(errors, Has.Some.Contains("not a child of entity 'TR'"));
        });
    }

    [Test]
    public void Duplicate_entity_names_are_invalid()
    {
        var ruleset = TestData.Trips();
        ruleset.Shape.Children[0].Name = "TR";

        Assert.That(ruleset.Validate(), Has.Some.Contains("unique"));
    }
}

[TestFixture]
public class SilentNoOpGuardTests
{
    [Test]
    public void Mistyped_compare_otherField_is_rejected_at_registration()
    {
        // A typo'd otherField resolves null on every record at run time — a rule that
        // silently never fires. Registration must catch it.
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Entity = "HL",
            Field = "Weight",
            Check = new CompareCheck { Op = CompareOp.Le, OtherField = "parent.TotalWiehgt" },
        });

        var errors = ruleset.Validate();

        Assert.That(errors, Has.Some.Contains("'TotalWiehgt' does not exist on entity 'TR'"));
    }

    [Test]
    public void Valid_parent_climbing_otherField_passes()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Entity = "HL",
            Field = "Weight",
            Check = new CompareCheck { Op = CompareOp.Le, OtherField = "parent.TotalWeight" },
        });

        Assert.That(ruleset.Validate(), Is.Empty);
    }

    [Test]
    public void OtherField_climbing_above_the_root_is_rejected()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Entity = "TR",
            Field = "TotalWeight",
            Check = new CompareCheck { Op = CompareOp.Ge, OtherField = "parent.Anything" },
        });

        Assert.That(ruleset.Validate(), Has.Some.Contains("climbs above the root entity"));
    }

    [Test]
    public void Mistyped_when_condition_field_is_rejected_at_registration()
    {
        var ruleset = TestData.Trips();
        ruleset.Rules.Add(new Rule
        {
            Entity = "TR",
            Field = "TotalWeight",
            When = new RuleCondition { Field = "Statsu", OneOf = ["landed"] },
            Check = new RequiredCheck(),
        });

        Assert.That(ruleset.Validate(), Has.Some.Contains("when.field 'Statsu' does not exist"));
    }
}

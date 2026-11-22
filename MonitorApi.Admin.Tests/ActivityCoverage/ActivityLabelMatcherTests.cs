using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// Deterministic tests for matching published activity labels to the activity list. The cases are the
/// ones found when the matcher was prototyped against live labels: the wins it has to keep, and the
/// near-misses it has to refuse.
/// </summary>
public class ActivityLabelMatcherTests
{
	private readonly ActivityLabelMatcher matcher = TestVocabulary.Build().Matcher;

	[Theory]
	// Exact once normalised: case, ™/®, accents, & vs "and", punctuation, spaces.
	[InlineData("Swimming", "Swimming")]
	[InlineData("SWIMMING", "Swimming")]
	[InlineData("body pump", "Bodypump™")]
	[InlineData("BodyPump", "Bodypump™")]
	[InlineData("crèche", "Creche")]
	[InlineData("zumba", "Zumba®")]
	[InlineData("legs, bums & tums", "Legs Bums And Tums")]
	[InlineData("rpm", "Rpm™")]
	// Containment: every word of the concept appears in the label.
	[InlineData("lane swimming", "Swimming")]
	[InlineData("Swimming for All", "Swimming")]
	[InlineData("public swimming", "Swimming")]
	[InlineData("squash booking", "Squash")]
	[InlineData("les mills sprint", "Sprint®")]
	[InlineData("open athletics session", "Athletics")]
	[InlineData("athletics track (running only)", "Athletics")]
	// The concept with more words wins when several are contained.
	[InlineData("pilates - reformer - beginner", "Reformer Pilates")]
	// An exact match beats containment of a shorter concept.
	[InlineData("walking football", "Walking Football")]
	// Containment beats a near-miss Dice score on a different concept.
	[InlineData("swimming sessions", "Swimming")]
	[InlineData("dance fit", "Dance")]
	// Reordered words.
	[InlineData("mind, body and soul", "Body Mind and Soul")]
	public void MatchesTheExpectedConcept(string label, string expected)
	{
		var match = matcher.Match(label);

		Assert.NotNull(match);
		Assert.Equal(expected, match.Concept.PrefLabel);
	}

	[Theory]
	// Ambiguous single-word concepts are excluded from containment: a learner pool is for swimming.
	[InlineData("learner pool")]
	// Genuinely outside the vocabulary.
	[InlineData("gym session")]
	[InlineData("fitness classes")]
	[InlineData("health suite")]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("(8-15yrs)")]
	public void RefusesLabelsWithNoConfidentConcept(string label)
	{
		Assert.Null(matcher.Match(label));
	}

	[Fact]
	public void AnAmbiguousConceptStillMatchesExactly()
	{
		Assert.Equal("Pool", matcher.Match("Pool")?.Concept.PrefLabel);
	}

	[Fact]
	public void ExactMatchesScoreOne()
	{
		Assert.Equal(1.0, matcher.Match("body pump")!.Score);
	}

	[Fact]
	public void ContainmentScoresTheFixedContainmentScore()
	{
		Assert.Equal(ActivityLabelMatcher.ContainmentScore, matcher.Match("lane swimming")!.Score);
	}

	[Fact]
	public void ThresholdOfOne_AcceptsExactMatchesOnly()
	{
		Assert.NotNull(matcher.Match("body pump", minSimilarity: 1));
		Assert.Null(matcher.Match("lane swimming", minSimilarity: 1));
	}

	[Fact]
	public void LoweringTheThresholdNeverLosesAMatch()
	{
		string[] labels = ["lane swimming", "body pump", "mind, body and soul", "dance fit", "gym session"];

		foreach (var label in labels)
		{
			if (matcher.Match(label, minSimilarity: 0.95) is { } strict)
			{
				Assert.Equal(strict.Concept, matcher.Match(label, minSimilarity: 0.5)?.Concept);
			}
		}
	}

	[Fact]
	public void TheMemoisedResultDoesNotDependOnTheFirstThresholdAsked()
	{
		var fresh = TestVocabulary.Build().Matcher;

		Assert.Null(fresh.Match("lane swimming", minSimilarity: 1));
		Assert.Equal("Swimming", fresh.Match("lane swimming", minSimilarity: 0.85)?.Concept.PrefLabel);
	}

	[Theory]
	[InlineData("Bodypump™", "bodypump")]
	[InlineData("Legs, Bums & Tums", "legs bums and tums")]
	[InlineData("Crèche", "creche")]
	[InlineData("Junior Fitness (8-15yrs)", "junior fitness")]
	[InlineData("  Pilates - Reformer - Beginner  ", "pilates reformer beginner")]
	public void Normalise_FoldsTheLabel(string label, string expected)
	{
		Assert.Equal(expected, ActivityLabelMatcher.Normalise(label));
	}

	[Fact]
	public void Dice_IsOneForIdenticalAndZeroForDisjointStrings()
	{
		Assert.Equal(1.0, ActivityLabelMatcher.Dice("swimming", "swimming"));
		Assert.Equal(0.0, ActivityLabelMatcher.Dice("abc", "xyz"));
	}
}

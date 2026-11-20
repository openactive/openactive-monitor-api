using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>Deterministic tests for reading the published activity list.</summary>
public class ActivityVocabularyTests
{
	[Fact]
	public void ParsesEveryConcept()
	{
		var vocabulary = TestVocabulary.Build();

		Assert.Equal(20, vocabulary.Concepts.Count);
		Assert.Contains(vocabulary.Concepts, c => c.Id == TestVocabulary.SwimmingId && c.PrefLabel == "Swimming");
	}

	[Theory]
	[InlineData("Swimming")]
	[InlineData("swimming")]
	[InlineData(" SWIMMING ")]
	[InlineData(TestVocabulary.SwimmingId)]
	public void Find_ByLabelIgnoringCaseOrById(string key)
	{
		Assert.Equal(TestVocabulary.SwimmingId, TestVocabulary.Build().Find(key)?.Id);
	}

	[Fact]
	public void Find_IsExactNotFuzzy()
	{
		Assert.Null(TestVocabulary.Build().Find("lane swimming"));
	}

	[Fact]
	public void ConceptsWithoutAnIdOrLabelAreSkipped()
	{
		var vocabulary = ActivityVocabulary.Parse("""
			{ "concept": [
			  { "id": "a", "prefLabel": "Kept" },
			  { "id": "b" },
			  { "prefLabel": "No id" },
			  { "id": "c", "prefLabel": "   " }
			] }
			""");

		Assert.Equal("Kept", Assert.Single(vocabulary.Concepts).PrefLabel);
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("""{ "title": "no concepts" }""")]
	[InlineData("""{ "concept": {} }""")]
	[InlineData("""{ "concept": [] }""")]
	public void UnreadableDocumentsAreInvalidData(string json)
	{
		// InvalidDataException is what the controller turns into a 502.
		Assert.Throws<InvalidDataException>(() => ActivityVocabulary.Parse(json));
	}
}

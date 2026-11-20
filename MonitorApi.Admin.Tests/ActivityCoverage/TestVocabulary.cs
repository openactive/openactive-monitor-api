using MonitorApi.Services.Admin;

namespace MonitorApi.Admin.Tests.ActivityCoverage;

/// <summary>
/// A slice of the real OpenActive activity list, with labels exactly as published. Chosen to include
/// the near-misses the matcher has to tell apart: Swimming / Swimming Lessons, Dance / Popdance Fit,
/// Football / Walking Football, Pilates / Reformer Pilates, and the ambiguous Pool. The ids are
/// stand-ins, since only labels drive matching.
/// </summary>
internal static class TestVocabulary
{
	public const string SwimmingId = "https://openactive.io/activity-list#swimming";
	public const string AquaFitId = "https://openactive.io/activity-list#aqua-fit";

	public const string JsonLd = """
		{
		  "@context": "https://openactive.io/",
		  "id": "https://openactive.io/activity-list",
		  "type": "ConceptScheme",
		  "concept": [
		    { "id": "https://openactive.io/activity-list#swimming", "type": "Concept", "prefLabel": "Swimming" },
		    { "id": "https://openactive.io/activity-list#swimming-lessons", "type": "Concept", "prefLabel": "Swimming Lessons" },
		    { "id": "https://openactive.io/activity-list#aqua-fit", "type": "Concept", "prefLabel": "Aqua Fit" },
		    { "id": "https://openactive.io/activity-list#bodypump", "type": "Concept", "prefLabel": "Bodypump™" },
		    { "id": "https://openactive.io/activity-list#creche", "type": "Concept", "prefLabel": "Creche" },
		    { "id": "https://openactive.io/activity-list#zumba", "type": "Concept", "prefLabel": "Zumba®" },
		    { "id": "https://openactive.io/activity-list#lbt", "type": "Concept", "prefLabel": "Legs Bums And Tums" },
		    { "id": "https://openactive.io/activity-list#rpm", "type": "Concept", "prefLabel": "Rpm™" },
		    { "id": "https://openactive.io/activity-list#sprint", "type": "Concept", "prefLabel": "Sprint®" },
		    { "id": "https://openactive.io/activity-list#pilates", "type": "Concept", "prefLabel": "Pilates" },
		    { "id": "https://openactive.io/activity-list#reformer-pilates", "type": "Concept", "prefLabel": "Reformer Pilates" },
		    { "id": "https://openactive.io/activity-list#football", "type": "Concept", "prefLabel": "Football" },
		    { "id": "https://openactive.io/activity-list#walking-football", "type": "Concept", "prefLabel": "Walking Football" },
		    { "id": "https://openactive.io/activity-list#dance", "type": "Concept", "prefLabel": "Dance" },
		    { "id": "https://openactive.io/activity-list#popdance-fit", "type": "Concept", "prefLabel": "Popdance Fit" },
		    { "id": "https://openactive.io/activity-list#pool", "type": "Concept", "prefLabel": "Pool" },
		    { "id": "https://openactive.io/activity-list#bmas", "type": "Concept", "prefLabel": "Body Mind and Soul" },
		    { "id": "https://openactive.io/activity-list#squash", "type": "Concept", "prefLabel": "Squash" },
		    { "id": "https://openactive.io/activity-list#athletics", "type": "Concept", "prefLabel": "Athletics" },
		    { "id": "https://openactive.io/activity-list#gym-workout", "type": "Concept", "prefLabel": "Gym Workout" }
		  ]
		}
		""";

	public static ActivityVocabulary Build() => ActivityVocabulary.Parse(JsonLd);
}

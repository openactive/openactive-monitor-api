using System.Text.Json;

namespace MonitorApi.Services.Admin;

/// <summary>One concept from the OpenActive activity list.</summary>
/// <param name="Id">The concept's <c>@id</c>, e.g. <c>https://openactive.io/activity-list#2750229d-…</c>.</param>
/// <param name="PrefLabel">The canonical label, e.g. <c>Swimming</c>.</param>
public sealed record ActivityConcept(string Id, string PrefLabel);

/// <summary>
/// The OpenActive activity list (<c>activity-list.jsonld</c>), parsed, with the label matcher built
/// over it.
/// </summary>
/// <remarks>
/// Pure: built from the document text, so it can be tested from a string literal. The network fetch
/// lives in <see cref="ActivityVocabularySource"/>.
///
/// Only <c>id</c> and <c>prefLabel</c> are read. The list's hierarchy (<c>broader</c>/<c>narrower</c>)
/// is deliberately ignored: coverage is reported per concept, with no rollup.
/// </remarks>
public sealed class ActivityVocabulary
{
	private readonly Dictionary<string, ActivityConcept> byId;
	private readonly Dictionary<string, ActivityConcept> byLabel;

	/// <summary>Builds the vocabulary from concepts already in hand.</summary>
	public ActivityVocabulary(IEnumerable<ActivityConcept> concepts)
	{
		Concepts = concepts.ToList();
		byId = new Dictionary<string, ActivityConcept>(StringComparer.Ordinal);
		byLabel = new Dictionary<string, ActivityConcept>(StringComparer.OrdinalIgnoreCase);

		foreach (var concept in Concepts)
		{
			byId.TryAdd(concept.Id, concept);
			byLabel.TryAdd(concept.PrefLabel.Trim(), concept);
		}

		Matcher = new ActivityLabelMatcher(Concepts);
	}

	/// <summary>Every concept, in document order.</summary>
	public IReadOnlyList<ActivityConcept> Concepts { get; }

	/// <summary>Matches published labels to concepts. Memoised, so shared by every request.</summary>
	public ActivityLabelMatcher Matcher { get; }

	/// <summary>
	/// Looks a concept up by its exact <c>@id</c>, or by its <c>prefLabel</c> ignoring case. Used for
	/// the <c>activity</c> filter, where the caller names a concept rather than a published label, so
	/// no fuzzy matching applies.
	/// </summary>
	public ActivityConcept? Find(string idOrLabel)
	{
		var key = idOrLabel.Trim();
		return byId.GetValueOrDefault(key) ?? byLabel.GetValueOrDefault(key);
	}

	/// <summary>Parses the published JSON-LD document.</summary>
	/// <exception cref="InvalidDataException">
	/// The document is not JSON, has no <c>concept</c> array, or holds no usable concept.
	/// </exception>
	public static ActivityVocabulary Parse(string json)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json);
		}
		catch (JsonException e)
		{
			throw new InvalidDataException("The activity list is not valid JSON.", e);
		}

		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("concept", out var conceptArray)
				|| conceptArray.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("The activity list has no 'concept' array.");
			}

			var concepts = new List<ActivityConcept>();
			foreach (var element in conceptArray.EnumerateArray())
			{
				var id = ReadString(element, "id") ?? ReadString(element, "@id");
				var label = ReadString(element, "prefLabel");
				if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(label))
				{
					concepts.Add(new ActivityConcept(id, label));
				}
			}

			if (concepts.Count == 0)
			{
				throw new InvalidDataException("The activity list holds no concepts.");
			}

			return new ActivityVocabulary(concepts);
		}
	}

	private static string? ReadString(JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(property, out var value)
		&& value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}

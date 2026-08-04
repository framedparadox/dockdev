using System.Text;

namespace DevDX.Services.Tools;

public enum LoremUnit { Words, Sentences, Paragraphs }

/// <summary>
/// Placeholder text. The word pool is the classical Lorem Ipsum vocabulary, sampled rather than
/// replayed verbatim, so two paragraphs of the same length don't come out identical.
/// </summary>
public static class LoremTools
{
    private static readonly string[] Words =
    [
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do",
        "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim",
        "ad", "minim", "veniam", "quis", "nostrud", "exercitation", "ullamco", "laboris", "nisi",
        "aliquip", "ex", "ea", "commodo", "consequat", "duis", "aute", "irure", "in", "reprehenderit",
        "voluptate", "velit", "esse", "cillum", "eu", "fugiat", "nulla", "pariatur", "excepteur",
        "sint", "occaecat", "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia",
        "deserunt", "mollit", "anim", "id", "est", "laborum", "perspiciatis", "unde", "omnis",
        "iste", "natus", "error", "voluptatem", "accusantium", "doloremque", "laudantium", "totam",
        "rem", "aperiam", "eaque", "ipsa", "quae", "ab", "illo", "inventore", "veritatis", "quasi",
        "architecto", "beatae", "vitae", "dicta", "explicabo", "nemo", "ipsam", "quia", "voluptas",
    ];

    private const string ClassicOpening = "Lorem ipsum dolor sit amet, consectetur adipiscing elit";

    /// <summary>
    /// Generates <paramref name="count"/> words, sentences or paragraphs.
    /// </summary>
    /// <param name="startClassic">Open with the canonical "Lorem ipsum dolor sit amet…" so the
    /// output is recognisable as placeholder text at a glance.</param>
    public static string Generate(LoremUnit unit, int count, bool startClassic = true)
    {
        count = Math.Clamp(count, 1, 500);
        var random = new Random();

        return unit switch
        {
            LoremUnit.Words => WordRun(random, count, startClassic),
            LoremUnit.Sentences => string.Join(' ', Enumerable.Range(0, count)
                .Select(i => Sentence(random, startClassic && i == 0))),
            _ => string.Join("\n\n", Enumerable.Range(0, count)
                .Select(i => Paragraph(random, startClassic && i == 0))),
        };
    }

    private static string WordRun(Random random, int count, bool startClassic)
    {
        var words = new List<string>(count);
        if (startClassic)
            words.AddRange(ClassicOpening.Replace(",", "").ToLowerInvariant().Split(' '));
        while (words.Count < count)
            words.Add(Words[random.Next(Words.Length)]);
        words.RemoveRange(count, words.Count - count);
        return Capitalise(string.Join(' ', words));
    }

    private static string Sentence(Random random, bool classic)
    {
        if (classic)
            return ClassicOpening + ".";

        int length = random.Next(6, 16);
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(Words[random.Next(Words.Length)]);
            // An occasional comma mid-sentence, never on the first or last word.
            if (i > 1 && i < length - 2 && random.Next(8) == 0)
                sb.Append(',');
        }
        sb.Append('.');
        return Capitalise(sb.ToString());
    }

    private static string Paragraph(Random random, bool classic) =>
        string.Join(' ', Enumerable.Range(0, random.Next(3, 7))
            .Select(i => Sentence(random, classic && i == 0)));

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

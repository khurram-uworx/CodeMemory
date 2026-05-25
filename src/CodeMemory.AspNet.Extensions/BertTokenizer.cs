using System.Text.RegularExpressions;

namespace CodeMemory.AspNet.Extensions;

public sealed record TokenizedSequence(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds);

public sealed partial class BertTokenizer : IDisposable
{
    //static readonly HashSet<string> SpecialTokens =
    //[
    //    "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"
    //];

    readonly Dictionary<string, int> vocabulary;
    readonly int maxLength;

    public BertTokenizer(string vocabPath, int maxLen = 512)
    {
        var lines = File.ReadAllLines(vocabPath);
        vocabulary = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
            vocabulary[lines[i]] = i;

        maxLength = maxLen;
    }

    public BertTokenizer(Stream vocabStream, int maxLen = 512)
    {
        using var reader = new StreamReader(vocabStream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        vocabulary = new Dictionary<string, int>(lines.Count, StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
            vocabulary[lines[i]] = i;

        maxLength = maxLen;
    }

    public int PadId => vocabulary.GetValueOrDefault("[PAD]", 0);
    public int UnkId => vocabulary.GetValueOrDefault("[UNK]", 100);
    public int ClsId => vocabulary.GetValueOrDefault("[CLS]", 101);
    public int SepId => vocabulary.GetValueOrDefault("[SEP]", 102);
    public int MaskId => vocabulary.GetValueOrDefault("[MASK]", 103);
    public int VocabSize => vocabulary.Count;
    public int MaxLength => maxLength;

    void wordPiece(string word, List<string> pieces)
    {
        if (vocabulary.ContainsKey(word))
        {
            pieces.Add(word);
            return;
        }

        var remaining = word.AsSpan();
        while (remaining.Length > 0)
        {
            var found = false;
            for (int len = remaining.Length; len > 0; len--)
            {
                var subword = remaining[..len].ToString();
                var lookup = pieces.Count == 0 ? subword : "##" + subword;

                if (vocabulary.ContainsKey(lookup))
                {
                    pieces.Add(lookup);
                    remaining = remaining[len..];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                pieces.Add("[UNK]");
                break;
            }
        }
    }

    public TokenizedSequence Tokenize(string text)
    {
        var pieces = new List<string>();
        var words = WordSplitRegex().Matches(text.ToLowerInvariant());

        foreach (var word in words.Select(m => m.Value))
            wordPiece(word, pieces);

        var tokens = new List<long>(maxLength) { ClsId };
        foreach (var piece in pieces)
        {
            if (vocabulary.TryGetValue(piece, out var id))
                tokens.Add(id);
            else
                tokens.Add(UnkId);
        }
        tokens.Add(SepId);

        if (tokens.Count > maxLength)
        {
            tokens[maxLength - 1] = SepId;
            tokens = tokens.Take(maxLength).ToList();
        }

        var inputIds = new long[maxLength];
        var attentionMask = new long[maxLength];
        var tokenTypeIds = new long[maxLength];

        for (int i = 0; i < tokens.Count; i++)
        {
            inputIds[i] = tokens[i];
            attentionMask[i] = 1;
        }

        return new TokenizedSequence(inputIds, attentionMask, tokenTypeIds);
    }

    public void Dispose() { }

    [GeneratedRegex(@"\w+|[^\w\s]")]
    private static partial Regex WordSplitRegex();
}

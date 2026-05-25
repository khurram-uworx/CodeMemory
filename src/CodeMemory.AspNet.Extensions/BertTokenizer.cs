using System.Text.RegularExpressions;

namespace CodeMemory.AspNet.Extensions;

public sealed partial class BertTokenizer : IDisposable
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _maxLen;

    private static readonly HashSet<string> SpecialTokens =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"
    ];

    public BertTokenizer(string vocabPath, int maxLen = 512)
    {
        var lines = File.ReadAllLines(vocabPath);
        _vocab = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
            _vocab[lines[i]] = i;

        _maxLen = maxLen;
    }

    public BertTokenizer(Stream vocabStream, int maxLen = 512)
    {
        using var reader = new StreamReader(vocabStream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        _vocab = new Dictionary<string, int>(lines.Count, StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
            _vocab[lines[i]] = i;

        _maxLen = maxLen;
    }

    public int PadId => _vocab.GetValueOrDefault("[PAD]", 0);
    public int UnkId => _vocab.GetValueOrDefault("[UNK]", 100);
    public int ClsId => _vocab.GetValueOrDefault("[CLS]", 101);
    public int SepId => _vocab.GetValueOrDefault("[SEP]", 102);
    public int MaskId => _vocab.GetValueOrDefault("[MASK]", 103);
    public int VocabSize => _vocab.Count;
    public int MaxLength => _maxLen;

    public TokenizedSequence Tokenize(string text)
    {
        var pieces = new List<string>();
        var words = WordSplitRegex().Matches(text.ToLowerInvariant());

        foreach (var word in words.Select(m => m.Value))
            WordPiece(word, pieces);

        var tokens = new List<long>(_maxLen) { ClsId };
        foreach (var piece in pieces)
        {
            if (_vocab.TryGetValue(piece, out var id))
                tokens.Add(id);
            else
                tokens.Add(UnkId);
        }
        tokens.Add(SepId);

        if (tokens.Count > _maxLen)
        {
            tokens[_maxLen - 1] = SepId;
            tokens = tokens.Take(_maxLen).ToList();
        }

        var inputIds = new long[_maxLen];
        var attentionMask = new long[_maxLen];
        var tokenTypeIds = new long[_maxLen];

        for (int i = 0; i < tokens.Count; i++)
        {
            inputIds[i] = tokens[i];
            attentionMask[i] = 1;
        }

        return new TokenizedSequence(inputIds, attentionMask, tokenTypeIds);
    }

    private void WordPiece(string word, List<string> pieces)
    {
        if (_vocab.ContainsKey(word))
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

                if (_vocab.ContainsKey(lookup))
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

    public void Dispose() { }

    [GeneratedRegex(@"\w+|[^\w\s]")]
    private static partial Regex WordSplitRegex();
}

public sealed record TokenizedSequence(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds);

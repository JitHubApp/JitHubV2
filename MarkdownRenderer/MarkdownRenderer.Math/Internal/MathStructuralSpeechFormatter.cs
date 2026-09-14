using System.Text;

namespace MarkdownRenderer.Math.Internal;

internal static class MathStructuralSpeechFormatter
{
    internal static string Format(
        string tex,
        Func<string, string> resolve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tex);
        ArgumentNullException.ThrowIfNull(resolve);
        cancellationToken.ThrowIfCancellationRequested();

        if (tex.Length > MathAccessibilityLimits.MaximumStructuralInputLength)
            return CreateBoundedSourceFallback(tex, cancellationToken);

        var parser = new Parser(tex, resolve, cancellationToken);
        string result = parser.ReadSequence('\0');
        cancellationToken.ThrowIfCancellationRequested();
        return parser.WasLimited || string.IsNullOrWhiteSpace(result)
            ? CreateBoundedSourceFallback(tex, cancellationToken)
            : result;
    }

    private static string CreateBoundedSourceFallback(
        string tex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int maximumLength = MathAccessibilityLimits.MaximumStructuralSpeechLength;
        if (tex.Length <= maximumLength)
            return tex;

        int prefixLength = maximumLength - 1;
        if (prefixLength > 0 &&
            char.IsHighSurrogate(tex[prefixLength - 1]) &&
            char.IsLowSurrogate(tex[prefixLength]))
        {
            prefixLength--;
        }

        string result = string.Create(
            prefixLength + 1,
            (Tex: tex, PrefixLength: prefixLength),
            static (destination, state) =>
            {
                state.Tex.AsSpan(0, state.PrefixLength).CopyTo(destination);
                destination[^1] = '\u2026';
            });
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private sealed class Parser
    {
        private readonly string _tex;
        private readonly Func<string, string> _resolve;
        private readonly CancellationToken _cancellationToken;
        private int _position;
        private int _recursionDepth;
        private bool _depthLimitReached;
        private bool _outputLimitReached;

        internal Parser(
            string tex,
            Func<string, string> resolve,
            CancellationToken cancellationToken)
        {
            _tex = tex;
            _resolve = resolve;
            _cancellationToken = cancellationToken;
        }

        internal bool WasLimited => _depthLimitReached || _outputLimitReached;

        internal string ReadSequence(char terminator)
        {
            var output = new StringBuilder(
                System.Math.Min(
                    _tex.Length,
                    MathAccessibilityLimits.MaximumStructuralSpeechLength));
            while (!WasLimited &&
                _position < _tex.Length &&
                (terminator == '\0' || _tex[_position] != terminator))
            {
                Checkpoint();
                char value = _tex[_position++];
                switch (value)
                {
                    case '\\':
                        Append(output, ReadCommand());
                        break;
                    case '^':
                        Append(output, Resolve(MathStringKeys.Superscript));
                        Append(output, ReadArgument());
                        if (WasLimited)
                            break;
                        Append(output, Resolve(MathStringKeys.EndSuperscript));
                        break;
                    case '_':
                        Append(output, Resolve(MathStringKeys.Subscript));
                        Append(output, ReadArgument());
                        if (WasLimited)
                            break;
                        Append(output, Resolve(MathStringKeys.EndSubscript));
                        break;
                    case '{':
                        Append(output, ReadNestedSequence('}'));
                        Consume('}');
                        break;
                    case '+':
                        Append(output, Resolve(MathStringKeys.Plus));
                        break;
                    case '-':
                    case '\u2212':
                        Append(output, Resolve(MathStringKeys.Minus));
                        break;
                    case '=':
                        Append(output, Resolve(MathStringKeys.EqualsOperator));
                        break;
                    case '*':
                    case '\u00d7':
                        Append(output, Resolve(MathStringKeys.Times));
                        break;
                    case '/':
                    case '\u00f7':
                        Append(output, Resolve(MathStringKeys.DividedBy));
                        break;
                    default:
                        if (!char.IsWhiteSpace(value))
                            Append(output, value.ToString());
                        break;
                }
            }

            _cancellationToken.ThrowIfCancellationRequested();
            return output.ToString();
        }

        private string ReadNestedSequence(char terminator)
        {
            if (!TryEnterRecursion())
                return string.Empty;

            try
            {
                return ReadSequence(terminator);
            }
            finally
            {
                ExitRecursion();
            }
        }

        private string ReadCommand()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_position >= _tex.Length)
                return "backslash";

            int start = _position;
            if (char.IsLetter(_tex[_position]))
            {
                while (_position < _tex.Length && char.IsLetter(_tex[_position]))
                {
                    Checkpoint();
                    _position++;
                    if (_position - start > MathAccessibilityLimits.MaximumStructuralSpeechLength)
                    {
                        _outputLimitReached = true;
                        return string.Empty;
                    }
                }
            }
            else
            {
                _position++;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            string command = _tex[start.._position];
            return command switch
            {
                "frac" => ReadFraction(),
                "sqrt" => ReadRoot(),
                "cdot" or "times" => Resolve(MathStringKeys.Times),
                "div" => Resolve(MathStringKeys.DividedBy),
                "pm" => Compose(Resolve(MathStringKeys.Plus), "or", Resolve(MathStringKeys.Minus)),
                "alpha" => "alpha",
                "beta" => "beta",
                "gamma" => "gamma",
                "delta" => "delta",
                "epsilon" => "epsilon",
                "theta" => "theta",
                "lambda" => "lambda",
                "mu" => "mu",
                "pi" => "pi",
                "rho" => "rho",
                "sigma" => "sigma",
                "phi" => "phi",
                "omega" => "omega",
                "infty" => "infinity",
                "sum" => "sum",
                "prod" => "product",
                "int" => "integral",
                "left" or "right" => string.Empty,
                "," or ";" or ":" or "!" => string.Empty,
                _ => command.Replace('-', ' '),
            };
        }

        private string ReadFraction()
        {
            string numerator = ReadArgument();
            if (WasLimited)
                return string.Empty;
            string denominator = ReadArgument();
            if (WasLimited)
                return string.Empty;

            return Compose(
                Resolve(MathStringKeys.Fraction),
                Resolve(MathStringKeys.Numerator),
                numerator,
                Resolve(MathStringKeys.Denominator),
                denominator,
                Resolve(MathStringKeys.EndFraction));
        }

        private string ReadRoot()
        {
            if (_position < _tex.Length && _tex[_position] == '[')
                SkipBracketArgument();
            if (WasLimited)
                return string.Empty;

            string root = Resolve(MathStringKeys.SquareRoot);
            string argument = ReadArgument();
            if (WasLimited)
                return string.Empty;

            return Compose(root, argument, Resolve(MathStringKeys.EndRoot));
        }

        private string ReadArgument()
        {
            if (!TryEnterRecursion())
                return string.Empty;

            try
            {
                SkipWhitespace();
                if (WasLimited || _position >= _tex.Length)
                    return string.Empty;

                if (_tex[_position] == '{')
                {
                    _position++;
                    string result = ReadSequence('}');
                    Consume('}');
                    return result;
                }

                if (_tex[_position] == '\\')
                {
                    _position++;
                    return ReadCommand();
                }

                return _tex[_position++].ToString();
            }
            finally
            {
                ExitRecursion();
            }
        }

        private void SkipBracketArgument()
        {
            int depth = 0;
            do
            {
                Checkpoint();
                char value = _tex[_position++];
                if (value == '[')
                    depth++;
                else if (value == ']')
                    depth--;
            }
            while (_position < _tex.Length && depth > 0);
        }

        private void SkipWhitespace()
        {
            while (_position < _tex.Length && char.IsWhiteSpace(_tex[_position]))
            {
                Checkpoint();
                _position++;
            }
        }

        private void Consume(char expected)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_position < _tex.Length && _tex[_position] == expected)
                _position++;
        }

        private string Resolve(string key)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string value = _resolve(key);
            _cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        private string Compose(params string[] values)
        {
            var output = new StringBuilder();
            foreach (string value in values)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Append(output, value);
                if (WasLimited)
                    break;
            }

            return output.ToString();
        }

        private void Append(StringBuilder output, string? value)
        {
            if (WasLimited || string.IsNullOrEmpty(value))
                return;

            int start = 0;
            while (start < value.Length && char.IsWhiteSpace(value[start]))
            {
                Checkpoint(start);
                start++;
            }

            int end = value.Length;
            while (end > start && char.IsWhiteSpace(value[end - 1]))
            {
                Checkpoint(value.Length - end);
                end--;
            }

            if (start == end)
                return;

            bool addSeparator = output.Length > 0 && output[^1] != ' ';
            int required = checked((addSeparator ? 1 : 0) + end - start);
            int available = MathAccessibilityLimits.MaximumStructuralSpeechLength - output.Length;
            if (required <= available)
            {
                if (addSeparator)
                    output.Append(' ');
                _cancellationToken.ThrowIfCancellationRequested();
                output.Append(value.AsSpan(start, end - start));
                _cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            if (addSeparator && available > 0)
            {
                output.Append(' ');
                available--;
            }

            int take = System.Math.Min(available, end - start);
            if (take > 0 &&
                start + take < end &&
                char.IsHighSurrogate(value[start + take - 1]) &&
                char.IsLowSurrogate(value[start + take]))
            {
                take--;
            }

            if (take > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                output.Append(value.AsSpan(start, take));
                _cancellationToken.ThrowIfCancellationRequested();
            }

            _outputLimitReached = true;
        }

        private bool TryEnterRecursion()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_recursionDepth >= MathAccessibilityLimits.MaximumStructuralRecursionDepth)
            {
                _depthLimitReached = true;
                return false;
            }

            _recursionDepth++;
            return true;
        }

        private void ExitRecursion() => _recursionDepth--;

        private void Checkpoint(int counter = -1)
        {
            int value = counter >= 0 ? counter : _position;
            if ((value & 0x3f) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

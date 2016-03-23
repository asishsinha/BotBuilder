﻿using Chronic;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Bot.Builder.Form.Advanced
{
    /// <summary>
    /// Recognizer for enumerated values.
    /// </summary>
    public sealed class RecognizeEnumeration<T> : IRecognize<T>
        where T : class, new()
    {
        /// <summary>
        /// Delegate for mapping from a C# value to it's description.
        /// </summary>
        /// <param name="value">C# value to get description for.</param>
        /// <returns>Description of C# value.</returns>
        public delegate string DescriptionDelegate(object value);

        /// <summary>
        /// Delegate to return the terms to match on for a C# value.
        /// </summary>
        /// <param name="value">C# value to get terms for.</param>
        /// <returns>Enumeration of regular expressions to match on for value.</returns>
        public delegate IEnumerable<string> TermsDelegate(object value);

        /// <summary>
        /// Constructor based on <see cref="IField{T}"/>.
        /// </summary>
        /// <param name="field">Field with enumerated values.</param>
        public RecognizeEnumeration(IField<T> field)
        {
            var configuration = field.Form().Configuration();
            _form = field.Form();
            _allowNumbers = field.AllowNumbers();
            _description = field.Description();
            _terms = field.Terms();
            _values = field.Values();
            _valueDescriptions = field.ValueDescriptions();
            _descriptionDelegate = (value) => field.ValueDescription(value);
            _termsDelegate = (value) => field.Terms(value);
            _helpFormat = field.Template(field.AllowNumbers()
                ? (field.AllowsMultiple() ? TemplateUsage.EnumManyNumberHelp : TemplateUsage.EnumOneNumberHelp)
                : (field.AllowsMultiple() ? TemplateUsage.EnumManyWordHelp : TemplateUsage.EnumOneWordHelp));
            _noPreference = field.Optional() ? configuration.NoPreference : null;
            _currentChoice = configuration.CurrentChoice.FirstOrDefault();
            BuildPerValueMatcher(configuration.CurrentChoice);
        }

        /// <summary>
        /// Explicitly contructed recognizer.
        /// </summary>
        /// <param name="form">Form recognizer is being used in.</param>
        /// <param name="description">Description of the field being asked for.</param>
        /// <param name="terms">Regular expressions that when matched mean this field.</param>
        /// <param name="values">Possible C# values for field.</param>
        /// <param name="descriptionDelegate">Mapping from C# value to it's description.</param>
        /// <param name="termsDelegate">Mapping from C# value to it's regular expressions for matching.</param>
        /// <param name="allowNumbers">True to allow matching on numbers.</param>
        /// <param name="helpFormat">Template for generating overall help.</param>
        /// <param name="noPreference">Regular expressions for identifying no preference as choice.</param>
        /// <param name="currentChoice">Regular expressions for identifying the current choice.</param>
        public RecognizeEnumeration(IForm<T> form,
            string description,
            IEnumerable<object> terms,
            IEnumerable<object> values,
            DescriptionDelegate descriptionDelegate,
            TermsDelegate termsDelegate,
            bool allowNumbers,
            Template helpFormat,
            IEnumerable<string> noPreference = null,
            IEnumerable<string> currentChoice = null)
        {
            _form = form;
            _allowNumbers = allowNumbers;
            _values = values;
            _descriptionDelegate = descriptionDelegate;
            _termsDelegate = termsDelegate;
            _valueDescriptions = (from value in values select _descriptionDelegate(value)).ToArray();
            _helpFormat = helpFormat;
            _noPreference = noPreference;
            if (currentChoice != null)
            {
                _currentChoice = currentChoice.FirstOrDefault();
            }
            BuildPerValueMatcher(currentChoice);
        }

        public IEnumerable<object> Values()
        {
            return _values;
        }

        public IEnumerable<string> ValueDescriptions()
        {
            return _valueDescriptions;
        }

        public string ValueDescription(object value)
        {
            return _descriptionDelegate(value);
        }

        public IEnumerable<string> ValidInputs(object value)
        {
            return _termsDelegate(value);
        }

        public string Help(T state, object defaultValue)
        {
            var values = _valueDescriptions;
            var max = _max;
            if (_noPreference != null)
            {
                values = values.Union(new string[] { _noPreference.First() });
                if (defaultValue == null)
                {
                    --max;
                }
            }
            if ((defaultValue != null || _noPreference != null) && _currentChoice != null)
            {
                values = values.Union(new string[] { _currentChoice + " or 'c'" });
            }
            var args = new List<object>();
            if (_allowNumbers)
            {
                args.Add(1);
                args.Add(max);
            }
            else
            {
                args.Add(null);
                args.Add(null);
            }
            args.Add(Language.BuildList(values, _helpFormat.Separator, _helpFormat.LastSeparator));
            return new Prompter<T>(_helpFormat, _form, this).Prompt(state, "", args.ToArray());
        }

        public IEnumerable<TermMatch> Matches(string input, object defaultValue)
        {
            // if the user hit enter on an optional prompt, then consider taking the current choice as a low confidence option
            bool userSkippedPrompt = string.IsNullOrWhiteSpace(input) && (defaultValue != null || _noPreference != null);
            if (userSkippedPrompt)
            {
                yield return new TermMatch(0, input.Length, 1.0, defaultValue);
            }

            foreach (var expression in _expressions)
            {
                double longest = expression.Longest.Length;
                foreach (Match match in expression.Expression.Matches(input))
                {
                    var group1 = match.Groups[1];
                    var group2 = match.Groups[2];
                    if (group1.Success)
                    {
                        var confidence = System.Math.Min(group1.Length / longest, 1.0);
                        if (expression.Value is Special)
                        {
                            var special = (Special)expression.Value;
                            if (special == Special.CurrentChoice && (_noPreference != null || defaultValue != null))
                            {
                                yield return new TermMatch(group1.Index, group1.Length, confidence, defaultValue);
                            }
                            else if (special == Special.NoPreference)
                            {
                                yield return new TermMatch(group1.Index, group1.Length, confidence, null);
                            }
                        }
                        else
                        {
                            yield return new TermMatch(group1.Index, group1.Length, confidence, expression.Value);
                        }
                    }
                    else if (group2.Success)
                    {
                        yield return new TermMatch(group2.Index, group2.Length, 1.0, expression.Value);
                    }
                }
            }
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.AppendFormat("EnumeratedRecognizer({0}", _description);
            builder.Append(" [");
            foreach (var description in _valueDescriptions)
            {
                builder.Append(" ");
                builder.Append(description);
            }
            builder.Append("])");
            return builder.ToString();
        }

        private enum Special { CurrentChoice, NoPreference };

        // Word character, any word character, any digit, any positive group over word characters
        private const string WORD = @"(\w|\\w|\\d|(\[(?>(\w|-)+|\[(?<number>)|\](?<-number>))*(?(number)(?!))\]))";
        private static Regex _wordStart = new Regex(string.Format(@"^{0}|\(", WORD), RegexOptions.Compiled);
        private static Regex _wordEnd = new Regex(string.Format(@"({0}|\))(\?|\*|\+|\{{\d+\}}|\{{,\d+\}}|\{{\d+,\d+\}})?$", WORD), RegexOptions.Compiled);

        private void BuildPerValueMatcher(IEnumerable<string> currentChoice)
        {
            if (currentChoice != null)
            {
                // 0 is reserved for current default if any
                AddExpression(0, Special.CurrentChoice, currentChoice, _allowNumbers);
            }
            var n = 1;
            foreach (var value in _values)
            {
                n = AddExpression(n, value, _termsDelegate(value), _allowNumbers);
            }
            if (_noPreference != null)
            {
                // Add recognizer for no preference
                n = AddExpression(n, Special.NoPreference, _noPreference, _allowNumbers);
            }
            if (_terms != null && _terms.Count() > 0)
            {
                // Add field terms to help disambiguate
                AddExpression(n, SpecialValues.Field, _terms, false);
            }
            _max = n - 1;
        }

        private int AddExpression(int n, object value, IEnumerable<string> terms, bool allowNumbers)
        {
            var orderedTerms = (from term in terms orderby term.Length descending select term).ToArray();
            var word = new StringBuilder();
            var nonWord = new StringBuilder();
            var first = true;
            var firstNonWord = true;
            foreach (var term in orderedTerms)
            {
                var nterm = term.Trim().Replace(" ", @"\s+");
                if (nterm == "") nterm = "qqqq";
                if (_wordStart.Match(nterm).Success && _wordEnd.Match(nterm).Success)
                {
                    if (first)
                    {
                        first = false;
                        word.Append(@"(\b(?:");
                    }
                    else
                    {
                        word.Append('|');
                    }
                    word.Append(@"(?:");
                    word.Append(nterm);
                    word.Append(')');
                }
                else
                {
                    if (firstNonWord)
                    {
                        firstNonWord = false;
                        nonWord.Append('(');
                    }
                    else
                    {
                        nonWord.Append('|');
                    }
                    nonWord.Append(@"(?:");
                    nonWord.Append(nterm);
                    nonWord.Append(')');
                }
            }
            if (first)
            {
                word.Append("(qqqq)");
            }
            else
            {
                if (n == 0)
                {
                    word.Append("|c");
                }
                else if (allowNumbers)
                {
                    word.AppendFormat(@"|{0}", n);
                }
                word.Append(@")\b)");
            }
            if (firstNonWord)
            {
                nonWord.Append("(qqqq)");
            }
            else
            {
                nonWord.Append(')');
            }
            ++n;
            var expr = string.Format("{0}|{1}",
                word.ToString(),
                nonWord.ToString());
            _expressions.Add(new ValueAndExpression(value, new Regex(expr, RegexOptions.IgnoreCase), orderedTerms.First()));
            return n;
        }

        private class ValueAndExpression
        {
            public ValueAndExpression(object value, Regex expression, string longest)
            {
                Value = value;
                Expression = expression;
                Longest = longest;
            }

            public readonly object Value;
            public readonly Regex Expression;
            public readonly string Longest;
        }

        private readonly IForm<T> _form;
        private readonly string _description;
        private readonly IEnumerable<string> _noPreference;
        private readonly string _currentChoice;
        private readonly bool _allowNumbers;
        private readonly IEnumerable<string> _terms;
        private readonly IEnumerable<object> _values;
        private readonly IEnumerable<string> _valueDescriptions;
        private readonly DescriptionDelegate _descriptionDelegate;
        private readonly TermsDelegate _termsDelegate;
        private readonly Template _helpFormat;
        private int _max;
        private readonly List<ValueAndExpression> _expressions = new List<ValueAndExpression>();
    }

    /// <summary>
    /// Abstract class for constructing primitive value recognizers.
    /// </summary>
    /// <typeparam name="T">Form state.</typeparam>
    public abstract class RecognizePrimitive<T> : IRecognize<T>
        where T : class, new()
    {

        /// <summary>
        /// Constructor using <see cref="IField{T}"/>.
        /// </summary>
        /// <param name="field">Field to build recognizer for.</param>
        public RecognizePrimitive(IField<T> field)
        {
            _field = field;
            _currentChoices = new HashSet<string>(from choice in field.Form().Configuration().CurrentChoice
                                                  select choice.Trim().ToLower());
            if (field.Optional())
            {
                if (field.IsNullable())
                {
                    _noPreference = new HashSet<string>(from choice in field.Form().Configuration().NoPreference
                                                        select choice.Trim().ToLower());
                }
                else
                {
                    throw new ArgumentException("Primitive values must be nullable to be optional.");
                }
            }
        }

        /// <summary>
        /// Abstract method for parsing input.
        /// </summary>
        /// <param name="input">Input to match.</param>
        /// <returns>TermMatch if input is a match.</returns>
        public abstract TermMatch Parse(string input);

        /// <summary>
        /// Match input with optional default value.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public virtual IEnumerable<TermMatch> Matches(string input, object defaultValue = null)
        {
            var matchValue = input.Trim().ToLower();
            if (_noPreference != null && _noPreference.Contains(matchValue))
            {
                yield return new TermMatch(0, input.Length, 1.0, null);
            }
            else if ((defaultValue != null || _noPreference != null) && (matchValue == "" || matchValue == "c" || _currentChoices.Contains(matchValue)))
            {
                yield return new TermMatch(0, input.Length, 1.0, defaultValue);
            }
            else {
                var result = Parse(input);
                if (result != null)
                {
                    yield return result;
                }
            }
        }

        public abstract IEnumerable<string> ValidInputs(object value);

        public abstract string ValueDescription(object value);

        public virtual IEnumerable<string> ValueDescriptions()
        {
            return new string[0];
        }

        public virtual IEnumerable<object> Values()
        {
            return null;
        }

        public abstract string Help(T state, object defaultValue);

        /// <summary>
        /// Return the help template args for current choice and no preference.
        /// </summary>
        /// <param name="state">Form state.</param>
        /// <param name="defaultValue">Current value of field.</param>
        /// <returns></returns>
        protected List<object> HelpArgs(T state, object defaultValue)
        {
            var args = new List<object>();
            if (defaultValue != null || _field.Optional())
            {
                args.Add(_field.Form().Configuration().CurrentChoice.First() + " or 'c'");
                if (_field.Optional())
                {
                    args.Add(_field.Form().Configuration().NoPreference.First());
                }
                else
                {
                    args.Add(null);
                }
            }
            else
            {
                args.Add(null);
                args.Add(null);
            }
            return args;
        }

        /// <summary>
        /// Field being filled information.
        /// </summary>
        protected IField<T> _field;

        private HashSet<string> _currentChoices;
        private HashSet<string> _noPreference;
    }

    /// <summary>
    /// Recognize a boolean value.
    /// </summary>
    /// <typeparam name="T">Form state.</typeparam>
    public sealed class RecognizeBool<T> : RecognizePrimitive<T>
        where T : class, new()
    {
        /// <summary>
        /// Construct a boolean recognizer for a field.
        /// </summary>
        /// <param name="field">Boolean field.</param>
        public RecognizeBool(IField<T> field)
            : base(field)
        {
            _yes = new HashSet<string>(from term in field.Form().Configuration().Yes
                                       select term.Trim().ToLower());
            _no = new HashSet<string>(from term in field.Form().Configuration().No
                                      select term.Trim().ToLower());
        }

        public override TermMatch Parse(string input)
        {
            TermMatch result = null;
            var matchValue = input.Trim().ToLower();
            if (_yes.Contains(matchValue))
            {
                result = new TermMatch(0, input.Length, 1.0, true);
            }
            else if (_no.Contains(matchValue))
            {
                result = new TermMatch(0, input.Length, 1.0, false);
            }
            return result;
        }

        public override string Help(T state, object defaultValue)
        {
            var prompt = new Prompter<T>(_field.Template(TemplateUsage.BoolHelp), _field.Form(), null);
            var args = HelpArgs(state, defaultValue);
            return prompt.Prompt(state, _field.Name(), args.ToArray());
        }

        public override IEnumerable<string> ValidInputs(object value)
        {
            return (bool)value
                ? _field.Form().Configuration().Yes
                : _field.Form().Configuration().No;
        }

        public override string ValueDescription(object value)
        {
            return ((bool)value
                ? _field.Form().Configuration().Yes
                : _field.Form().Configuration().No).First();
        }

        private HashSet<string> _yes;
        private HashSet<string> _no;
    }

    /// <summary>
    /// Recognize a string field.
    /// </summary>
    /// <typeparam name="T">Form state.</typeparam>
    public sealed class RecognizeString<T> : RecognizePrimitive<T>
        where T : class, new()
    {
        /// <summary>
        /// Construct a string recognizer for a field.
        /// </summary>
        /// <param name="field">String field.</param>
        public RecognizeString(IField<T> field)
            : base(field)
        {
        }

        public override IEnumerable<string> ValidInputs(object value)
        {
            yield return value as string;
        }

        public override string ValueDescription(object value)
        {
            return value as string;
        }

        public override TermMatch Parse(string input)
        {
            TermMatch result = null;
            if (!string.IsNullOrWhiteSpace(input))
            {
                // Confidence is 0.0 so commands get a crack
                result = new TermMatch(0, input.Length, 0.0, input);
            }
            return result;
        }

        public override string Help(T state, object defaultValue)
        {
            var prompt = new Prompter<T>(_field.Template(TemplateUsage.StringHelp), _field.Form(), null);
            var args = HelpArgs(state, defaultValue);
            return prompt.Prompt(state, _field.Name(), args.ToArray());
        }
    }

    /// <summary>
    /// Recognize a numeric field.
    /// </summary>
    /// <typeparam name="T">Form state.</typeparam>
    public sealed class RecognizeNumber<T> : RecognizePrimitive<T>
        where T : class, new()
    {
        /// <summary>
        /// Construct a numeric recognizer for a field.
        /// </summary>
        /// <param name="field">Numeric field.</param>
        /// <param name="culture">Culture to use for parsing.</param>
        public RecognizeNumber(IField<T> field, CultureInfo culture)
            : base(field)
        {
            _culture = culture;
            double min, max;
            _showLimits = field.Limits(out min, out max);
            _min = (long)min;
            _max = (long)max;
        }

        public override string ValueDescription(object value)
        {
            return ((long)Convert.ChangeType(value, typeof(long))).ToString(_culture.NumberFormat);
        }

        public override IEnumerable<string> ValidInputs(object value)
        {
            yield return ((long)value).ToString(_culture.NumberFormat);
        }

        public override TermMatch Parse(string input)
        {
            TermMatch result = null;
            long number;
            if (long.TryParse(input, out number))
            {
                if (number >= _min && number <= _max)
                {
                    result = new TermMatch(0, input.Length, 1.0, number);
                }
            }
            return result;
        }

        public override string Help(T state, object defaultValue)
        {
            var prompt = new Prompter<T>(_field.Template(TemplateUsage.IntegerHelp), _field.Form(), null);
            var args = HelpArgs(state, defaultValue);
            if (_showLimits)
            {
                args.Add(_min);
                args.Add(_max);
            }
            return prompt.Prompt(state, _field.Name(), args.ToArray());
        }

        private long _min;
        private long _max;
        private bool _showLimits;
        private CultureInfo _culture;
    }

    /// <summary>
    /// Recognize a double or float field.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class RecognizeDouble<T> : RecognizePrimitive<T>
        where T : class, new()
    {

        /// <summary>
        /// Construct a double or float recognizer for a field.
        /// </summary>
        /// <param name="field">Float or double field.</param>
        /// <param name="culture">Culture to use for parsing.</param>
        public RecognizeDouble(IField<T> field, CultureInfo culture)
            : base(field)
        {
            _culture = culture;
            _showLimits = field.Limits(out _min, out _max);
        }

        public override string ValueDescription(object value)
        {
            return ((double)Convert.ChangeType(value, typeof(double))).ToString(_culture.NumberFormat);
        }

        public override IEnumerable<string> ValidInputs(object value)
        {
            yield return ((double)value).ToString(_culture.NumberFormat);
        }

        public override TermMatch Parse(string input)
        {
            TermMatch result = null;
            double number;
            if (double.TryParse(input, out number))
            {
                if (number >= _min && number <= _max)
                {
                    result = new TermMatch(0, input.Length, 1.0, number);
                }
            }
            return result;
        }

        public override string Help(T state, object defaultValue)
        {
            var prompt = new Prompter<T>(_field.Template(TemplateUsage.DoubleHelp), _field.Form(), null);
            var args = HelpArgs(state, defaultValue);
            if (_showLimits)
            {
                args.Add(_min);
                args.Add(_max);
            }
            return prompt.Prompt(state, _field.Name(), args.ToArray());
        }

        private double _min;
        private double _max;
        private bool _showLimits;
        private CultureInfo _culture;
    }

    /// <summary>
    /// Recognize a date/time expression.
    /// </summary>
    /// <typeparam name="T">Form state.</typeparam>
    /// <remarks>
    /// Expressions recognized are based on the C# nuget package Chronic.
    /// </remarks>
    public sealed class RecognizeDateTime<T> : RecognizePrimitive<T>
        where T : class, new()
    {
        /// <summary>
        /// Construct a date/time recognizer.
        /// </summary>
        /// <param name="field">DateTime field.</param>
        /// <param name="culture">Culture to use for parsing.</param>
        public RecognizeDateTime(IField<T> field, CultureInfo culture)
            : base(field)
        {
            _culture = culture;
            _parser = new Chronic.Parser();
        }

        public override string Help(T state, object defaultValue)
        {
            var prompt = new Prompter<T>(_field.Template(TemplateUsage.DateTimeHelp), _field.Form(), null);
            var args = HelpArgs(state, defaultValue);
            return prompt.Prompt(state, _field.Name(), args.ToArray());
        }

        public override TermMatch Parse(string input)
        {
            TermMatch match = null;
            var parse = _parser.Parse(input);
            if (parse != null && parse.Start.HasValue)
            {
                match = new TermMatch(0, input.Length, 1.0, parse.Start.Value);
            }
            return match;
        }

        public override IEnumerable<string> ValidInputs(object value)
        {
            yield return ValueDescription(value);
        }

        public override string ValueDescription(object value)
        {
            return ((DateTime) value).ToString(CultureInfo.CurrentCulture.DateTimeFormat);
        }

        private CultureInfo _culture;
        private Parser _parser;
    }
}

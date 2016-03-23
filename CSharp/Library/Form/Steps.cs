﻿using Microsoft.Bot.Builder.Form.Advanced;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Bot.Builder.Form
{
    internal class FieldStep<T> : IStep<T>
        where T : class, new()
    {
        public FieldStep(string name, IFormModel<T> model)
        {
            _name = name;
            _field = model.Fields.Field(name);
        }

        public string Name
        {
            get
            {
                return _name;
            }
        }

        public StepType Type
        {
            get
            {
                return StepType.Field;
            }
        }

        public IField<T> Field
        {
            get
            {
                return _field;
            }
        }

        public bool Active(T state)
        {
            return _field.Active(state);
        }

        public string Start(IDialogContext context, T state, FormState form)
        {
            form.SetPhase(StepPhase.Responding);
            form.StepState = new FieldStepState(FieldStepStates.SentPrompt);
            return _field.Prompt().Prompt(state, _name);
        }

        public IEnumerable<TermMatch> Match(IDialogContext context, T state, FormState form, string input, out string lastInput)
        {
            IEnumerable<TermMatch> matches = null;
            Debug.Assert(form.Phase() == StepPhase.Responding);
            var stepState = form.StepState as FieldStepState;
            lastInput = input;
            if (stepState.State == FieldStepStates.SentPrompt)
            {
                matches = _field.Prompt().Recognizer().Matches(input, _field.GetValue(state));
            }
            else if (stepState.State == FieldStepStates.SentClarify)
            {
                var fieldState = form.StepState as FieldStepState;
                var iprompt = _field.Prompt();
                Ambiguous clarify;
                var iChoicePrompt = NextClarifyPrompt(state, fieldState, iprompt.Recognizer(), out clarify);
                matches = MatchAnalyzer.Coalesce(MatchAnalyzer.HighestConfidence(iChoicePrompt.Recognizer().Matches(input)), input).ToArray();
                if (matches.Count() > 1)
                {
                    matches = new TermMatch[0];
                }
            }
#if DEBUG
            if (Form<T>.DebugRecognizers)
            {
                MatchAnalyzer.PrintMatches(matches, 2);
            }
#endif
            return matches;
        }

        public NextStep Process(IDialogContext context, T state, FormState form, string input, IEnumerable<TermMatch> matches,
            out string feedback, out string prompt)
        {
            feedback = null;
            prompt = null;
            var iprompt = _field.Prompt();
            var fieldState = form.StepState as FieldStepState;
            object response = null;
            if (fieldState.State == FieldStepStates.SentPrompt)
            {
                // Response to prompt
                var firstMatch = matches.FirstOrDefault();
                if (matches.Count() == 1)
                {
                    response = SetValue(state, firstMatch.Value, form, out feedback);
                }
                else if (matches.Count() > 1)
                {
                    // Check multiple matches for ambiguity
                    var groups = MatchAnalyzer.GroupedMatches(matches);
                    // 1) Could be multiple match groups like for ingredients.
                    // 2) Could be overlapping matches like "onion".
                    // 3) Could be multiple matches where only one is expected.

                    if (!_field.AllowsMultiple())
                    {
                        // Create a single group of all possibilities if only want one value
                        var mergedGroup = groups.SelectMany((group) => group).ToList();
                        groups = new List<List<TermMatch>>() { mergedGroup };
                    }
                    var ambiguous = new List<Ambiguous>();
                    var settled = new List<object>();
                    foreach (var choices in groups)
                    {
                        if (choices.Count() > 1)
                        {
                            var unclearResponses = string.Join(" ", (from choice in choices select input.Substring(choice.Start, choice.Length)).Distinct());
                            var values = from match in choices select match.Value;
                            ambiguous.Add(new Ambiguous(unclearResponses, values));
                        }
                        else
                        {
                            var matchValue = choices.First().Value;
                            if (matchValue.GetType().IsIEnumerable())
                            {
                                foreach (var value in matchValue as System.Collections.IEnumerable)
                                {
                                    settled.Add(value);
                                }
                            }
                            else
                            {
                                settled.Add(choices.First().Value);
                            }
                        }
                    }

                    if (ambiguous.Count() > 0)
                    {
                        // Need 1 or more clarifications
                        Ambiguous clarify;
                        fieldState.State = FieldStepStates.SentClarify;
                        fieldState.Settled = settled;
                        fieldState.Clarifications = ambiguous;
                        response = SetValue(state, null);
                        var iChoicePrompt = NextClarifyPrompt(state, form.StepState as FieldStepState, iprompt.Recognizer(), out clarify);
                        prompt = iChoicePrompt.Prompt(state, _name, clarify.Response);
                    }
                    else
                    {
                        if (_field.AllowsMultiple())
                        {
                            response = SetValue(state, settled, form, out feedback);
                        }
                        else
                        {
                            Debug.Assert(settled.Count() == 1);
                            response = SetValue(state, settled.First(), form, out feedback);
                        }
                    }
                }
                var unmatched = MatchAnalyzer.Unmatched(input, matches);
                var unmatchedWords = string.Join(" ", unmatched);
                var nonNoise = Language.NonNoiseWords(Language.WordBreak(unmatchedWords)).ToArray();
                fieldState.Unmatched = null;
                if (_field.Prompt().Annotation().Feedback == FeedbackOptions.Always)
                {
                    fieldState.Unmatched = string.Join(" ", nonNoise);
                }
                else if (_field.Prompt().Annotation().Feedback == FeedbackOptions.Auto
                        && nonNoise.Length > 0
                        && unmatched.Count() > 0)
                {
                    fieldState.Unmatched = string.Join(" ", nonNoise);
                }
            }
            else if (fieldState.State == FieldStepStates.SentClarify)
            {
                Ambiguous clarify;
                var iChoicePrompt = NextClarifyPrompt(state, fieldState, iprompt.Recognizer(), out clarify);
                if (matches.Count() == 1)
                {
                    // Clarified ambiguity
                    fieldState.Settled.Add(matches.First().Value);
                    fieldState.Clarifications.Remove(clarify);
                    Ambiguous newClarify;
                    var newiChoicePrompt = NextClarifyPrompt(state, fieldState, iprompt.Recognizer(), out newClarify);
                    if (newiChoicePrompt != null)
                    {
                        prompt = newiChoicePrompt.Prompt(state, _name, newClarify.Response);
                    }
                    else
                    {
                        // No clarification left, so set the field
                        if (_field.AllowsMultiple())
                        {
                            response = SetValue(state, fieldState.Settled, form, out feedback);
                        }
                        else
                        {
                            Debug.Assert(fieldState.Settled.Count() == 1);
                            response = SetValue(state, fieldState.Settled.First(), form, out feedback);
                        }
                        form.SetPhase(StepPhase.Completed);
                    }
                }
            }
            if (form.Phase() == StepPhase.Completed)
            {
                form.StepState = null;
                if (fieldState.Unmatched != null)
                {
                    if (fieldState.Unmatched != "")
                    {
                        feedback = new Prompter<T>(_field.Template(TemplateUsage.Feedback), _field.Model, null).Prompt(state, _name, fieldState.Unmatched);
                    }
                    else
                    {
                        feedback = new Prompter<T>(_field.Template(TemplateUsage.Feedback), _field.Model, null).Prompt(state, _name);
                    }
                }
            }
            return _field.Next(response, state);
        }

        public string NotUnderstood(IDialogContext context, T state, FormState form, string input)
        {
            string feedback = null;
            var iprompt = _field.Prompt();
            var fieldState = form.StepState as FieldStepState;
            if (fieldState.State == FieldStepStates.SentPrompt)
            {
                feedback = Template(TemplateUsage.NotUnderstood).Prompt(state, _name, input);
            }
            else if (fieldState.State == FieldStepStates.SentClarify)
            {
                feedback = Template(TemplateUsage.NotUnderstood).Prompt(state, "", input);
            }
            return feedback;
        }

        public bool Back(IDialogContext context, T state, FormState form)
        {
            bool backedUp = false;
            var fieldState = form.StepState as FieldStepState;
            if (fieldState.State == FieldStepStates.SentClarify)
            {
                var desc = _field.Model.Fields.Field(_name);
                if (desc.AllowsMultiple())
                {
                    desc.SetValue(state, fieldState.Settled);
                }
                else if (fieldState.Settled.Count() > 0)
                {
                    desc.SetValue(state, fieldState.Settled.First());
                }
                form.SetPhase(StepPhase.Ready);
                backedUp = true;
            }
            return backedUp;
        }

        public string Help(T state, FormState form, string commandHelp)
        {
            var fieldState = form.StepState as FieldStepState;
            IPrompt<T> template;
            if (fieldState.State == FieldStepStates.SentClarify)
            {
                Ambiguous clarify;
                var recognizer = NextClarifyPrompt(state, fieldState, _field.Prompt().Recognizer(), out clarify).Recognizer();
                template = Template(TemplateUsage.HelpClarify, recognizer);
            }
            else
            {
                template = Template(TemplateUsage.Help, _field.Prompt().Recognizer());
            }
            return "* " + template.Prompt(state, _name, "* " + template.Recognizer().Help(state, _field.GetValue(state)), commandHelp);
        }

        public IEnumerable<string> Dependencies()
        {
            return new string[0];
        }

        private IPrompt<T> Template(TemplateUsage usage, IRecognize<T> recognizer = null)
        {
            var template = _field.Template(usage);
            return new Prompter<T>(template, _field.Model, recognizer == null ? _field.Prompt().Recognizer() : recognizer);
        }

        private object SetValue(T state, object value)
        {
            var desc = _field.Model.Fields.Field(_name);
            if (value == null)
            {
                desc.SetUnknown(state);
            }
            else if (desc.AllowsMultiple())
            {
                if (value is System.Collections.IEnumerable)
                {
                    desc.SetValue(state, value);
                }
                else
                {
                    desc.SetValue(state, new List<object> { value });
                }
            }
            else
            {
                // Singleton value
                desc.SetValue(state, value);
            }
            return value;
        }

        private object SetValue(T state, object value, FormState form, out string feedback)
        {
            var desc = _field.Model.Fields.Field(_name);
            feedback = desc.Validate(state, value);
            if (feedback == null)
            {
                SetValue(state, value);
                form.SetPhase(StepPhase.Completed);
            }
            return value;
        }

        private IPrompt<T> NextClarifyPrompt(T state, FieldStepState stepState, IRecognize<T> recognizer, out Ambiguous clarify)
        {
            IPrompt<T> prompter = null;
            clarify = null;
            foreach (var clarification in stepState.Clarifications)
            {
                if (clarification.Values.Count() > 1)
                {
                    clarify = clarification;
                    break;
                }
            }
            if (clarify != null)
            {
                var template = Template(TemplateUsage.Clarify);
                var helpTemplate = _field.Template(template.Annotation().AllowNumbers != BoolDefault.False ? TemplateUsage.EnumOneNumberHelp : TemplateUsage.EnumManyNumberHelp);
                var choiceRecognizer = new RecognizeEnumeration<T>(_field.Model, "", null,
                    clarify.Values,
                    (value) => recognizer.ValueDescription(value),
                    (value) => recognizer.ValidInputs(value),
                    template.Annotation().AllowNumbers != BoolDefault.False, helpTemplate);
                prompter = Template(TemplateUsage.Clarify, choiceRecognizer);
            }
            return prompter;
        }

        internal enum FieldStepStates { Unknown, SentPrompt, SentClarify };

        [Serializable]
        internal class Ambiguous
        {
            public readonly string Response;
            public object[] Values;
            public Ambiguous(string response, IEnumerable<object> values)
            {
                Response = response;
                Values = values.ToArray<object>();
            }
        }

        [Serializable]
        internal class FieldStepState
        {
            internal FieldStepStates State;
            internal string Unmatched;
            internal List<object> Settled;
            internal List<Ambiguous> Clarifications;
            public FieldStepState(FieldStepStates state)
            {
                State = state;
            }
        }

        private readonly string _name;
        private readonly IField<T> _field;
    }

    internal class ConfirmStep<T> : IStep<T>
        where T : class, new()
    {
        public ConfirmStep(IField<T> field)
        {
            _field = field;
        }

        public bool Back(IDialogContext context, T state, FormState form)
        {
            return false;
        }

        public IField<T> Field
        {
            get
            {
                return _field;
            }
        }

        public bool Active(T state)
        {
            return _field.Active(state);
        }

        public IEnumerable<TermMatch> Match(IDialogContext context, T state, FormState form, string input, out string lastInput)
        {
            lastInput = input;
            return _field.Prompt().Recognizer().Matches(input);
        }

        public string Name
        {
            get
            {
                return _field.Name;
            }
        }

        public string NotUnderstood(IDialogContext context, T state, FormState form, string input)
        {
            var template = _field.Template(TemplateUsage.NotUnderstood);
            var prompter = new Prompter<T>(template, _field.Model, null);
            return prompter.Prompt(state, "", input);
        }

        public NextStep Process(IDialogContext context, T state, FormState form, string input, IEnumerable<TermMatch> matches,
            out string feedback,
            out string prompt)
        {
            feedback = null;
            prompt = null;
            var value = matches.First().Value;
            form.StepState = null;
            form.SetPhase((bool)value ? StepPhase.Completed : StepPhase.Ready);
            return _field.Next(value, state);
        }

        public string Start(IDialogContext context, T state, FormState form)
        {
            form.SetPhase(StepPhase.Responding);
            return _field.Prompt().Prompt(state, _field.Name);
        }

        public string Help(T state, FormState form, string commandHelp)
        {
            var template = _field.Template(TemplateUsage.HelpConfirm);
            var prompt = new Prompter<T>(template, _field.Model, _field.Prompt().Recognizer());
            return "* " + prompt.Prompt(state, _field.Name, "* " + prompt.Recognizer().Help(state, null), commandHelp);
        }

        public StepType Type
        {
            get
            {
                return StepType.Confirm;
            }
        }

        public IEnumerable<string> Dependencies()
        {
            return _field.Dependencies();
        }

        private readonly IField<T> _field;
    }

    internal class NavigationStep<T> : IStep<T>
        where T : class, new()
    {
        public NavigationStep(string name, IFormModel<T> model, T state, FormState formState)
        {
            _name = name;
            _model = model;
            _fields = model.Fields;
            var field = _fields.Field(_name);
            var fieldPrompt = field.Template(TemplateUsage.NavigationFormat);
            var template = field.Template(TemplateUsage.Navigation);
            var recognizer = new RecognizeEnumeration<T>(model, Name, null,
                formState.Next.Names,
                (value) => new Prompter<T>(fieldPrompt, model, _fields.Field(value as string).Prompt().Recognizer()).Prompt(state, value as string),
                (value) => _fields.Field(value as string).Terms(),
                model.Configuration.DefaultPrompt.AllowNumbers != BoolDefault.False,
                field.Template(TemplateUsage.NavigationHelp));
            _prompt = new Prompter<T>(template, model, recognizer);
        }

        public bool Back(IDialogContext context, T state, FormState form)
        {
            form.Next = null;
            return false;
        }

        public IField<T> Field
        {
            get
            {
                return _fields.Field(_name);
            }
        }

        public bool Active(T state)
        {
            return true;
        }

        public IEnumerable<TermMatch> Match(IDialogContext context, T state, FormState form, string input, out string lastInput)
        {
            lastInput = input;
            return _prompt.Recognizer().Matches(input);
        }

        public string Name
        {
            get
            {
                return "Navigation";
            }
        }

        public string NotUnderstood(IDialogContext context, T state, FormState form, string input)
        {
            var field = _fields.Field(_name);
            var template = field.Template(TemplateUsage.NotUnderstood);
            return new Prompter<T>(template, _model, null).Prompt(state, _name, input);
        }

        public NextStep Process(IDialogContext context, T state, FormState form, string input, IEnumerable<TermMatch> matches,
            out string feedback,
            out string prompt)
        {
            feedback = null;
            prompt = null;
            form.Next = null;
            return new NextStep(new string[] { matches.First().Value as string });
        }

        public string Start(IDialogContext context, T state, FormState form)
        {
            return _prompt.Prompt(state, _name);
        }

        public StepType Type
        {
            get
            {
                return StepType.Navigation;
            }
        }

        public string Help(T state, FormState form, string commandHelp)
        {
            var recognizer = _prompt.Recognizer();
            var prompt = new Prompter<T>(Field.Template(TemplateUsage.HelpNavigation), _model, recognizer);
            return "* " + prompt.Prompt(state, _name, "* " + recognizer.Help(state, null), commandHelp);
        }

        public IEnumerable<string> Dependencies()
        {
            return new string[0];
        }

        private string _name;
        private readonly IFormModel<T> _model;
        private readonly IFields<T> _fields;
        private readonly IPrompt<T> _prompt;
    }

    internal class MessageStep<T> : IStep<T>
        where T : class, new()
    {
        public MessageStep(Prompt prompt, ConditionalDelegate<T> condition, IFormModel<T> model)
        {
            _name = "message" + model.Steps.Count.ToString();
            _prompt = new Prompter<T>(prompt, model, null);
            _condition = (condition == null ? (state) => true : condition);
        }

        public bool Active(T state)
        {
            return _condition(state);
        }

        public bool Back(IDialogContext context, T state, FormState form)
        {
            return false;
        }

        public string Help(T state, FormState form, string commandHelp)
        {
            return null;
        }

        public IEnumerable<string> Dependencies()
        {
            throw new NotImplementedException();
        }

        public IField<T> Field
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public IEnumerable<TermMatch> Match(IDialogContext context, T state, FormState form, string input, out string lastInput)
        {
            throw new NotImplementedException();
        }

        public string Name
        {
            get
            {
                return _name;
            }
        }

        public string NotUnderstood(IDialogContext context, T state, FormState form, string input)
        {
            throw new NotImplementedException();
        }

        public NextStep Process(IDialogContext context, T state, FormState form, string input, IEnumerable<TermMatch> matches, out string feedback, out string prompt)
        {
            throw new NotImplementedException();
        }

        public string Start(IDialogContext context, T state, FormState form)
        {
            form.SetPhase(StepPhase.Completed);
            return _prompt.Prompt(state, "");
        }

        public StepType Type
        {
            get
            {
                return StepType.Message;
            }
        }

        private readonly string _name;
        private readonly ConditionalDelegate<T> _condition;
        private readonly IPrompt<T> _prompt;
    }
}

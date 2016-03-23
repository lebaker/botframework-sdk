﻿using Microsoft.Bot.Connector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Bot.Builder
{
    public class Prompts
    {
        public static void Text(IDialogContext context, ResumeAfter<string> resume, string prompt, string retry = null, int attempts = 3)
        {
            var child = new PromptText(prompt, retry, attempts);
            context.Call(child, resume);
        }

        public static void Confirm(IDialogContext context, ResumeAfter<bool> resume, string prompt, string retry = null, int attempts = 3)
        {
            var child = new PromptConfirm(prompt, retry, attempts);
            context.Call(child, resume);
        }

        public static void Number(IDialogContext context, ResumeAfter<int> resume, string prompt, string retry = null, int attempts = 3)
        {
            var child = new PromptInt32(prompt, retry, attempts);
            context.Call(child, resume);
        }

        public static void Choice<T>(IDialogContext context, ResumeAfter<T> resume, IEnumerable<T> options, string prompt, string retry = null, int attempts = 3)
        {
            var child = new PromptChoice<T>(options, prompt, retry, attempts);
            context.Call(child, resume);
        }

        private abstract class Prompt<T> : IDialogNew
        {
            protected readonly string prompt;
            protected readonly string retry;
            protected int attempts;

            public Prompt(string prompt, string retry, int attempts)
            {
                Field.SetNotNull(out this.prompt, nameof(prompt), prompt);
                Field.SetNotNull(out this.retry, nameof(retry), retry ?? prompt);
                this.attempts = attempts;
            }

            async Task IDialogNew.StartAsync(IDialogContext context, IAwaitable<object> arguments)
            {
                await context.PostAsync(this.prompt);
                context.Wait(MessageReceived);
            }

            private async Task MessageReceived(IDialogContext context, IAwaitable<Message> message)
            {
                T result;
                if (this.TryParse(await message, out result))
                {
                    context.Done(result);
                }
                else
                {
                    --this.attempts;
                    if (this.attempts > 0)
                    {
                        var retry = this.retry ?? this.DefaultRetry;
                        await context.PostAsync(retry);
                        context.Wait(MessageReceived);
                    }
                    else
                    {
                        await context.PostAsync("too many attempts");
                        throw new Exception();
                    }
                }
            }

            protected abstract bool TryParse(Message message, out T result);

            protected virtual string DefaultRetry
            {
                get
                {
                    return this.prompt;
                }
            }
        }

        private sealed class PromptText : Prompt<string>
        {
            public PromptText(string prompt, string retry, int attempts)
                : base(prompt, retry, attempts)
            {
            }

            protected override bool TryParse(Message message, out string result)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    result = message.Text;
                    return true;
                }
                else
                {
                    result = null;
                    return false;
                }
            }

            protected override string DefaultRetry
            {
                get
                {
                    return "I didn't understand. Say something in reply.\n" + this.prompt;
                }
            }
        }

        private sealed class PromptConfirm : Prompt<bool>
        {
            public PromptConfirm(string prompt, string retry, int attempts)
                : base(prompt, retry, attempts)
            {
            }

            protected override bool TryParse(Message message, out bool result)
            {
                switch (message.Text)
                {
                    case "y":
                    case "yes":
                    case "ok":
                        result = true;
                        return true;
                    case "n":
                    case "no":
                        result = false;
                        return true;
                    default:
                        result = false;
                        return false;
                }
            }

            protected override string DefaultRetry
            {
                get
                {
                    return "I didn't understand. Valid replies are yes or no.\n" + this.prompt;
                }
            }
        }

        private sealed class PromptInt32 : Prompt<Int32>
        {
            public PromptInt32(string prompt, string retry, int attempts)
                : base(prompt, retry, attempts)
            {
            }

            protected override bool TryParse(Message message, out Int32 result)
            {
                return Int32.TryParse(message.Text, out result);
            }
        }

        private sealed class PromptChoice<T> : Prompt<T>
        {
            private readonly IEnumerable<T> options;

            public PromptChoice(IEnumerable<T> options, string prompt, string retry, int attempts)
                : base(prompt, retry, attempts)
            {
                Field.SetNotNull(out this.options, nameof(options), options);
            }

            protected override bool TryParse(Message message, out T result)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    var selected = this.options
                        .Where(option => option.ToString().IndexOf(message.Text, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        .ToArray();
                    if (selected.Length == 1)
                    {
                        result = selected[0];
                        return true;
                    }
                }

                result = default(T);
                return false;
            }
        }
    }
}